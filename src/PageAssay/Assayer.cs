using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAssay.Cdp;

namespace PageAssay;

/// <summary>What a run does and how far it waits.</summary>
public sealed record AssayOptions
{
    /// <summary>The viewport width in CSS pixels.</summary>
    public int ViewportWidth { get; init; } = 1280;

    /// <summary>The viewport height in CSS pixels.</summary>
    public int ViewportHeight { get; init; } = 800;

    /// <summary>How long the page may take to fire its load event.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long after load the page may take to settle; past it, <see cref="AssayReport.SettledMs"/> is null.</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The quiet time — no pending request, no change to the document — that counts as settled.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the run keeps watching after the page settled, for errors that happen in use.</summary>
    public TimeSpan UseWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>At most this many findings of one kind; the rest are counted in one <c>more</c> finding.</summary>
    public int PerKindLimit { get; init; } = 3;

    /// <summary>At most this many findings in all; past it the report is truncated.</summary>
    public int TotalLimit { get; init; } = 50;

    /// <summary>Severity per kind, overriding <see cref="FindingKinds.DefaultSeverity"/>.</summary>
    public IReadOnlyDictionary<string, Severity> Severities { get; init; } = new Dictionary<string, Severity>();
}

/// <summary>Runs a page in a browser and reports what went wrong.</summary>
public static class Assayer
{
    private const string World = "page-assay";

    // Runs in an isolated world on every new document: the page cannot see it, and it shares the DOM it watches.
    private const string QuietTracker =
        "(() => { const st = { last: performance.now() }; globalThis.__pageAssay = st;" +
        " new MutationObserver(() => { st.last = performance.now(); })" +
        ".observe(document, { subtree: true, childList: true, attributes: true, characterData: true }); })();";

    /// <summary>Loads <paramref name="page"/> in a fresh browser context of <paramref name="browser"/>, waits until it settles, and reports.</summary>
    public static async Task<AssayReport> RunAsync(Browser browser, Uri page, AssayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(page);
        options ??= new AssayOptions();

        await using var cdp = await CdpConnection.ConnectAsync(browser.WebSocketUrl, cancellationToken).ConfigureAwait(false);
        var version = await cdp.SendAsync("Browser.getVersion", cancellationToken: cancellationToken).ConfigureAwait(false);
        var engine = version.GetProperty("product").GetString() ?? "unknown";

        var browserContext = (await cdp.SendAsync("Target.createBrowserContext", cancellationToken: cancellationToken)
            .ConfigureAwait(false)).GetProperty("browserContextId").GetString()!;
        try
        {
            var target = (await cdp.SendAsync("Target.createTarget",
                    new JsonObject { ["url"] = "about:blank", ["browserContextId"] = browserContext }, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).GetProperty("targetId").GetString()!;
            var session = (await cdp.SendAsync("Target.attachToTarget",
                    new JsonObject { ["targetId"] = target, ["flatten"] = true }, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).GetProperty("sessionId").GetString()!;

            var run = new Run(cdp, session, target, page, options);
            return await run.ExecuteAsync(engine, browser.Headless, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await cdp.SendAsync("Target.disposeBrowserContext", new JsonObject { ["browserContextId"] = browserContext },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // The browser is going away; its contexts go with it.
            }
        }
    }

    private sealed class Run(CdpConnection cdp, string session, string frameId, Uri page, AssayOptions options)
    {
        private readonly object _gate = new();
        private readonly List<(Finding Finding, long At)> _found = [];
        private readonly HashSet<string> _pendingRequests = [];
        private readonly Dictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _browserOwn = new(StringComparer.Ordinal);
        private readonly Stopwatch _clock = new();
        private long _lastNetworkMs;
        private long? _loadMs;
        private long? _settledAtMs;
        private int? _worldContext;

        public async Task<AssayReport> ExecuteAsync(string engine, bool headless, CancellationToken cancellationToken)
        {
            cdp.EventReceived = OnEvent;
            foreach (var domain in new[] { "Runtime.enable", "Page.enable", "Network.enable" })
                await Send(domain, null, cancellationToken).ConfigureAwait(false);
            await Send("Fetch.enable", new JsonObject { ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }) },
                cancellationToken).ConfigureAwait(false);
            await Send("Emulation.setDeviceMetricsOverride", new JsonObject
            {
                ["width"] = options.ViewportWidth, ["height"] = options.ViewportHeight, ["deviceScaleFactor"] = 1, ["mobile"] = false,
            }, cancellationToken).ConfigureAwait(false);
            await Send("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = QuietTracker, ["worldName"] = World },
                cancellationToken).ConfigureAwait(false);

            _clock.Start();
            var navigation = await Send("Page.navigate", new JsonObject { ["url"] = page.AbsoluteUri }, cancellationToken)
                .ConfigureAwait(false);
            var context = new RunContext(engine, headless, page.Scheme == Uri.UriSchemeFile ? "file" : "http",
                options.ViewportWidth, options.ViewportHeight);
            if (navigation.TryGetProperty("errorText", out var navigationError))
            {
                Add(FindingKinds.Resource, Fields(("url", PathOf(page)), ("error", navigationError.GetString())));
                return Build(loaded: false, settledMs: null, context);
            }

            var loaded = await WaitAsync(() => _loadMs is not null, options.LoadTimeout, cancellationToken).ConfigureAwait(false);
            long? settledMs = null;
            if (loaded)
            {
                var deadline = _clock.ElapsedMilliseconds + (long)options.SettleTimeout.TotalMilliseconds;
                while (_clock.ElapsedMilliseconds < deadline)
                {
                    if (await IsQuietAsync(cancellationToken).ConfigureAwait(false))
                    {
                        settledMs = _clock.ElapsedMilliseconds - (long)options.QuietPeriod.TotalMilliseconds;
                        lock (_gate) _settledAtMs = _clock.ElapsedMilliseconds;
                        break;
                    }
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(options.UseWindow, cancellationToken).ConfigureAwait(false);
                await LookForErrorTextAsync(cancellationToken).ConfigureAwait(false);
            }
            cdp.EventReceived = null;
            return Build(loaded, settledMs, context);
        }

        private async Task<bool> IsQuietAsync(CancellationToken cancellationToken)
        {
            long sinceNetwork;
            lock (_gate)
            {
                if (_pendingRequests.Count > 0) return false;
                sinceNetwork = _clock.ElapsedMilliseconds - _lastNetworkMs;
            }
            var quiet = (long)options.QuietPeriod.TotalMilliseconds;
            if (sinceNetwork < quiet) return false;
            var sinceChange = await EvaluateAsync("performance.now() - globalThis.__pageAssay.last", cancellationToken)
                .ConfigureAwait(false);
            return sinceChange is { ValueKind: JsonValueKind.Number } ms && ms.GetDouble() >= quiet;
        }

        // The page caught an exception and printed its message: no error event and no console line exist, only the text.
        private const string ErrorTextProbe = """
            (() => {
              const patterns = [
                /\b(?:ReferenceError|TypeError|SyntaxError|RangeError)\b/,
                /Cannot access '[^']+' before initialization/,
                /\bis not defined\b/, /\bis not a function\b/, /\bis not iterable\b/,
                /Cannot (?:read|set) properties of (?:undefined|null)/,
                /Unexpected token/,
              ];
              const selectorOf = (el) => {
                const parts = [];
                for (let e = el; e && e.nodeType === 1 && parts.length < 4; e = e.parentElement) {
                  if (e.id) { parts.unshift('#' + CSS.escape(e.id)); break; }
                  const tag = e.localName;
                  const same = e.parentElement ? [...e.parentElement.children].filter(c => c.localName === tag) : [];
                  parts.unshift(same.length > 1 ? `${tag}:nth-of-type(${same.indexOf(e) + 1})` : tag);
                }
                return parts.join(' > ');
              };
              const out = [];
              const walker = document.createTreeWalker(document.body || document, NodeFilter.SHOW_TEXT);
              for (let n = walker.nextNode(); n; n = walker.nextNode()) {
                const text = n.textContent.trim();
                const el = n.parentElement;
                if (!text || !el || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE'].includes(el.tagName)) continue;
                if (el.getClientRects().length === 0) continue;
                if (patterns.some(p => p.test(text))) out.push({ selector: selectorOf(el), text: text.slice(0, 200) });
              }
              return out;
            })()
            """;

        private async Task LookForErrorTextAsync(CancellationToken cancellationToken)
        {
            var found = await EvaluateAsync(ErrorTextProbe, cancellationToken).ConfigureAwait(false);
            if (found is not { ValueKind: JsonValueKind.Array } list) return;
            foreach (var item in list.EnumerateArray())
                Add(FindingKinds.ErrorOnScreen, Fields(("selector", item.GetProperty("selector").GetString()),
                    ("text", item.GetProperty("text").GetString())), PageAssay.Phase.Use);
        }

        private async Task<JsonElement?> EvaluateAsync(string expression, CancellationToken cancellationToken)
        {
            int? world;
            lock (_gate) world = _worldContext;
            if (world is null) return null;
            try
            {
                var result = await Send("Runtime.evaluate", new JsonObject
                {
                    ["expression"] = expression, ["contextId"] = world, ["returnByValue"] = true,
                }, cancellationToken).ConfigureAwait(false);
                return result.TryGetProperty("exceptionDetails", out _) ? null
                    : result.GetProperty("result").TryGetProperty("value", out var value) ? value : null;
            }
            catch (CdpException)
            {
                return null; // the context went away (a navigation); the next poll finds the new one
            }
        }

        private void OnEvent(CdpEvent e)
        {
            if (e.SessionId != session) return;
            var p = e.Params;
            switch (e.Method)
            {
                case "Page.loadEventFired":
                    lock (_gate) _loadMs ??= _clock.ElapsedMilliseconds;
                    break;
                case "Runtime.executionContextCreated":
                {
                    var c = p.GetProperty("context");
                    if (c.TryGetProperty("name", out var name) && name.GetString() == World
                        && c.TryGetProperty("auxData", out var aux) && aux.TryGetProperty("frameId", out var frame)
                        && frame.GetString() == frameId)
                        lock (_gate) _worldContext = c.GetProperty("id").GetInt32();
                    break;
                }
                case "Runtime.executionContextsCleared":
                    lock (_gate) _worldContext = null;
                    break;
                case "Runtime.exceptionThrown":
                    OnException(p.GetProperty("exceptionDetails"));
                    break;
                case "Runtime.consoleAPICalled" when p.GetProperty("type").GetString() == "error":
                    Add(FindingKinds.ConsoleError, Fields(("message", ConsoleText(p.GetProperty("args")))));
                    break;
                case "Fetch.requestPaused":
                    OnRequestPaused(p);
                    break;
                case "Network.requestWillBeSent":
                    lock (_gate)
                    {
                        _pendingRequests.Add(p.GetProperty("requestId").GetString()!);
                        _requestUrls[p.GetProperty("requestId").GetString()!] = p.GetProperty("request").GetProperty("url").GetString()!;
                        // A request no script, element or style of the page made (the browser's own favicon fetch) is not the page's.
                        if (p.GetProperty("initiator").GetProperty("type").GetString() == "other"
                            && p.TryGetProperty("type", out var requestType) && requestType.GetString() != "Document")
                            _browserOwn.Add(p.GetProperty("requestId").GetString()!);
                        _lastNetworkMs = _clock.ElapsedMilliseconds;
                    }
                    break;
                case "Network.responseReceived":
                {
                    var response = p.GetProperty("response");
                    var status = response.GetProperty("status").GetInt32();
                    var url = new Uri(response.GetProperty("url").GetString()!);
                    bool own;
                    lock (_gate) own = !_browserOwn.Contains(p.GetProperty("requestId").GetString()!);
                    if (status >= 400 && SameOrigin(url) && own)
                        Add(FindingKinds.Resource, Fields(("url", PathOf(url)), ("status", status)));
                    break;
                }
                case "Network.loadingFinished":
                case "Network.loadingFailed":
                    lock (_gate)
                    {
                        _pendingRequests.Remove(p.GetProperty("requestId").GetString()!);
                        _lastNetworkMs = _clock.ElapsedMilliseconds;
                    }
                    if (e.Method == "Network.loadingFailed")
                    {
                        string? url;
                        bool own;
                        lock (_gate)
                        {
                            _requestUrls.TryGetValue(p.GetProperty("requestId").GetString()!, out url);
                            own = !_browserOwn.Contains(p.GetProperty("requestId").GetString()!);
                        }
                        var error = p.GetProperty("errorText").GetString() ?? "";
                        // Our own refusals are reported as blocked; an abort is the page cancelling its own request.
                        if (url is not null && own && Uri.TryCreate(url, UriKind.Absolute, out var failed) && SameOrigin(failed)
                            && !error.Contains("BLOCKED_BY_CLIENT", StringComparison.Ordinal)
                            && !error.Contains("ERR_ABORTED", StringComparison.Ordinal))
                            Add(FindingKinds.Resource, Fields(("url", PathOf(failed)), ("error", error)));
                    }
                    break;
            }
        }

        private void OnException(JsonElement details)
        {
            var text = details.GetProperty("text").GetString() ?? "";
            var message = details.TryGetProperty("exception", out var exception)
                && exception.TryGetProperty("description", out var description)
                    ? description.GetString() ?? text
                    : exception.ValueKind == JsonValueKind.Object && exception.TryGetProperty("value", out var value)
                        ? value.ToString()
                        : text;
            message = message.Split('\n')[0];
            var kind = text.StartsWith("Uncaught (in promise)", StringComparison.Ordinal)
                ? FindingKinds.Rejection
                : FindingKinds.Uncaught;
            var fields = new List<(string, JsonNode?)> { ("message", message) };
            if (details.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var file) && SameOrigin(file))
                fields.Add(("file", PathOf(file)));
            fields.Add(("line", details.GetProperty("lineNumber").GetInt32() + 1));
            fields.Add(("column", details.GetProperty("columnNumber").GetInt32() + 1));
            Add(kind, Fields(fields.ToArray()));
        }

        private void OnRequestPaused(JsonElement p)
        {
            var requestId = p.GetProperty("requestId").GetString()!;
            var url = new Uri(p.GetProperty("request").GetProperty("url").GetString()!);
            if (SameOrigin(url) || url.Scheme is "data" or "blob" or "about")
            {
                cdp.Post("Fetch.continueRequest", new JsonObject { ["requestId"] = requestId }, session);
                return;
            }
            cdp.Post("Fetch.failRequest", new JsonObject { ["requestId"] = requestId, ["errorReason"] = "BlockedByClient" }, session);
            Add(FindingKinds.Blocked, Fields(("category", "network"), ("host", url.Host), ("url", url.GetLeftPart(UriPartial.Path))));
        }

        private bool SameOrigin(Uri url) =>
            string.Equals(url.Scheme, page.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(url.Host, page.Host, StringComparison.OrdinalIgnoreCase)
            && url.Port == page.Port;

        private string PathOf(Uri url) => SameOrigin(url) ? url.PathAndQuery : url.AbsoluteUri;

        private static string ConsoleText(JsonElement args) => string.Join(" ", args.EnumerateArray().Select(a =>
            a.TryGetProperty("value", out var v) ? v.ToString()
            : a.TryGetProperty("description", out var d) ? d.GetString()
            : a.GetProperty("type").GetString()));

        private static Dictionary<string, JsonNode?> Fields(params (string Name, JsonNode? Value)[] fields) =>
            fields.Where(f => f.Value is not null).ToDictionary(f => f.Name, f => f.Value);

        private void Add(string kind, Dictionary<string, JsonNode?> fields, Phase? phase = null)
        {
            lock (_gate)
            {
                var now = _clock.ElapsedMilliseconds;
                phase ??= kind is FindingKinds.Uncaught or FindingKinds.Rejection or FindingKinds.ConsoleError
                    ? _settledAtMs is null ? PageAssay.Phase.Load : PageAssay.Phase.Use
                    : null;
                var severity = options.Severities.TryGetValue(kind, out var s) ? s : FindingKinds.DefaultSeverity(kind);
                _found.Add((new Finding(kind, severity, FindingKinds.TextOf(kind, fields), phase, fields), now));
            }
        }

        private AssayReport Build(bool loaded, long? settledMs, RunContext context)
        {
            List<Finding> all;
            lock (_gate) all = _found.OrderBy(f => f.At).Select(f => f.Finding).ToList();
            var kept = new List<Finding>();
            var cut = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var finding in all)
            {
                if (kept.Count(k => k.Kind == finding.Kind) < options.PerKindLimit)
                    kept.Add(finding);
                else
                    cut[finding.Kind] = cut.GetValueOrDefault(finding.Kind) + 1;
            }
            foreach (var (kind, count) in cut)
            {
                var fields = Fields(("of", kind), ("count", count));
                kept.Add(new Finding(FindingKinds.More, Severity.Notice, FindingKinds.TextOf(FindingKinds.More, fields), null, fields));
            }
            var truncated = kept.Count > options.TotalLimit;
            if (truncated) kept = kept.Take(options.TotalLimit).ToList();
            return new AssayReport(PathOf(page), loaded, settledMs, kept, truncated, context);
        }

        private async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var deadline = _clock.ElapsedMilliseconds + (long)timeout.TotalMilliseconds;
            while (_clock.ElapsedMilliseconds < deadline)
            {
                lock (_gate) if (condition()) return true;
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }

        private Task<JsonElement> Send(string method, JsonObject? parameters, CancellationToken cancellationToken) =>
            cdp.SendAsync(method, parameters, session, cancellationToken);
    }
}
