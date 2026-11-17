using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PageAssay.Tests;

/// <summary>A loopback HTTP origin serving fixed files, so every page under test has an origin of its own.</summary>
public sealed class TestSite : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (string Body, string Type)> _files = new(StringComparer.Ordinal);
    private readonly Task _serving;

    public Uri Origin { get; }

    public TestSite()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Origin = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Origin.AbsoluteUri);
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public Uri Page(string path, string html)
    {
        _files["/" + path] = (html, "text/html; charset=utf-8");
        return new Uri(Origin, path);
    }

    public void File(string path, string body, string type) => _files["/" + path] = (body, type);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            var path = context.Request.Url!.AbsolutePath;
            if (_files.TryGetValue(path, out var file))
            {
                context.Response.ContentType = file.Type;
                var bytes = Encoding.UTF8.GetBytes(file.Body);
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _serving.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }
}

/// <summary>One browser for the whole test class; tests skip when none is installed.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    public Browser? Browser { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (Browser.FindExecutable() is not null)
            Browser = await Browser.LaunchAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
    }
}
