using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PageAssay.Cdp;

/// <summary>An event the browser sent: its method, the session it belongs to (null for the browser), and its parameters.</summary>
internal readonly record struct CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>A command the browser refused.</summary>
public sealed class CdpException : Exception
{
    /// <summary>Creates the exception with the browser's message.</summary>
    public CdpException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public CdpException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates the exception with no message.</summary>
    public CdpException() { }
}

/// <summary>
/// The Chrome DevTools Protocol over one WebSocket: commands with an id and their results, events to one handler, sessions
/// multiplexed by <c>sessionId</c> (flat mode). Nothing more — the protocol is small enough that a client library would only add
/// a dependency.
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _receiving;
    private int _nextId;

    /// <summary>Called for every event, on the receiving loop. It must not wait for a command's result.</summary>
    public Action<CdpEvent>? EventReceived { get; set; }

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
        _receiving = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return new CdpConnection(socket);
    }

    /// <summary>Sends a command and waits for its result.</summary>
    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (sessionId is not null)
            message["sessionId"] = sessionId;
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>Sends a command from an event handler: its result is not awaited, and a failure is ignored on purpose.</summary>
    public void Post(string method, JsonObject parameters, string? sessionId) =>
        _ = SendAsync(method, parameters, sessionId).ContinueWith(
            static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _closing.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;
                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The browser went away or we are closing; every waiting command fails below.
        }
        finally
        {
            foreach (var (_, waiting) in _pending)
                waiting.TrySetException(new CdpException("The connection to the browser closed."));
            _pending.Clear();
        }
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            if (!_pending.TryRemove(id, out var waiting))
                return;
            if (root.TryGetProperty("error", out var error))
                waiting.TrySetException(new CdpException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error"));
            else
                waiting.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
            return;
        }
        if (root.TryGetProperty("method", out var method))
        {
            var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            EventReceived?.Invoke(new CdpEvent(method.GetString() ?? "", session, parameters));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync().ConfigureAwait(false);
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // Closing is best effort: the socket is disposed next either way.
            }
        }
        try { await _receiving.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _socket.Dispose();
        _sendLock.Dispose();
        _closing.Dispose();
    }
}
