using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PageAssay.Cdp;
using PageAssay.Static;

namespace PageAssay;

/// <summary>What a run does and how far it waits.</summary>
public sealed record AssayOptions
{
    /// <summary>The viewports the page is looked at in. The page loads in the first; the look checks run in each.
    /// The default is a desktop window and a phone-width one — a layout broken only at a narrower width is common.</summary>
    public IReadOnlyList<Viewport> Viewports { get; init; } = [new(1280, 800), new(390, 844)];

    /// <summary>The colour schemes (<c>light</c>, <c>dark</c>) the page is looked at in, by emulating <c>prefers-color-scheme</c>.
    /// The page loads in the first.</summary>
    public IReadOnlyList<string> ColorSchemes { get; init; } = ["light", "dark"];

    /// <summary>How long the page may take to fire its load event.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long after load the page may take to settle; past it, <see cref="AssayReport.SettledMs"/> is null.</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The quiet time — no pending request, no change to the document — that counts as settled.</summary>
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the run keeps watching after the page settled, for errors that happen in use.</summary>
    public TimeSpan UseWindow { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How many lines the serving origin puts in front of the page's own first line (markup or scripts it injects).
    /// Lines in the page's own file are reported minus this, so a finding at line 40 can be repaired at line 40 of the file.</summary>
    public int PageLineOffset { get; init; }

    /// <summary>At most this many findings of one kind; the rest are counted in one <c>more</c> finding.</summary>
    public int PerKindLimit { get; init; } = 3;

    /// <summary>Per-kind limits that differ from <see cref="PerKindLimit"/> (default: <c>table-shape</c> 2).</summary>
    public IReadOnlyDictionary<string, int> PerKindLimits { get; init; } = new Dictionary<string, int> { [FindingKinds.TableShape] = 2 };

    /// <summary>At most this many findings in all; past it the report is truncated.</summary>
    public int TotalLimit { get; init; } = 50;

    /// <summary>Severity per kind, overriding <see cref="FindingKinds.DefaultSeverity"/>.</summary>
    public IReadOnlyDictionary<string, Severity> Severities { get; init; } = new Dictionary<string, Severity>();

    /// <summary>Whether a driven run also reads the page's source with the static rules (default true). The source is the
    /// document the browser received, minus the <see cref="PageLineOffset"/> lines the serving origin put in front of it.</summary>
    public bool StaticRules { get; init; } = true;
}

/// <summary>Checks a page and reports what is wrong: by reading its source (the static rules), and by running it in a browser.</summary>
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
        Validate(options);
        return Limit(await RunAllAsync(browser, page, options, cancellationToken).ConfigureAwait(false), options);
    }

    /// <summary>
    /// Runs the previous and the new version of a page in the same context and reports the new version's findings, each marked
    /// <see cref="Comparison.New"/> (only in the new version) or <see cref="Comparison.Kept"/> (in both), plus the previous version's
    /// findings the change fixed, marked <see cref="Comparison.Gone"/>. Findings are matched by <see cref="Finding.Key"/>, never by line.
    /// </summary>
    public static async Task<AssayReport> CompareAsync(Browser browser, Uri before, Uri after, AssayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        options ??= new AssayOptions();
        Validate(options);
        var previous = await RunAllAsync(browser, before, options, cancellationToken).ConfigureAwait(false);
        var current = await RunAllAsync(browser, after, options, cancellationToken).ConfigureAwait(false);
        return Limit(Compare(previous, current), options);
    }

    /// <summary>
    /// Reads a page's source with the static rules only — no browser, nothing run. <paramref name="source"/> is the page's HTML as
    /// it is in its file; lines and columns are counted in it. <paramref name="page"/> is the caller's name for the page.
    /// <paramref name="origin"/>, when given, is the origin the page will be served on: absolute URLs on it are not another host.
    /// </summary>
    public static AssayReport RunStatic(string page, string source, Uri? origin = null, AssayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(source);
        options ??= new AssayOptions();
        return Limit(Static(page, source, origin, options), options);
    }

    /// <summary>
    /// Compares two versions of a page's source with the static rules only: the findings of <paramref name="after"/> marked
    /// <see cref="Comparison.New"/> or <see cref="Comparison.Kept"/>, plus those of <paramref name="before"/> the change fixed,
    /// marked <see cref="Comparison.Gone"/> — the same comparison <see cref="CompareAsync"/> makes, without a browser.
    /// </summary>
    public static AssayReport CompareStatic(string page, string before, string after, Uri? origin = null, AssayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        options ??= new AssayOptions();
        return Limit(Compare(Static(page, before, origin, options), Static(page, after, origin, options)), options);
    }

    private static AssayReport Static(string page, string source, Uri? origin, AssayOptions options) =>
        new(page, null, null, StaticRules.Run(source, origin, options), false, RunContext.StaticOnly);

    // Marks the current version's findings new or kept and adds the previous version's that are gone, matching by key.
    private static AssayReport Compare(AssayReport previous, AssayReport current)
    {
        var unmatched = previous.Findings.GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<Finding>(g), StringComparer.Ordinal);
        var marked = new List<Finding>();
        foreach (var finding in current.Findings)
        {
            var kept = unmatched.TryGetValue(finding.Key, out var same) && same.TryDequeue(out _);
            marked.Add(finding with { Status = kept ? Comparison.Kept : Comparison.New });
        }
        marked.AddRange(unmatched.Values.SelectMany(q => q).Select(f => f with { Status = Comparison.Gone }));
        // A change that breaks the parse of more scripts than before: nothing else measured on that version can be trusted, so every
        // other finding is marked deferred — still reported with its status, for a caller that drops or shows them.
        if (current.Findings.Count(f => f.Kind == FindingKinds.Syntax) > previous.Findings.Count(f => f.Kind == FindingKinds.Syntax))
            marked = marked.Select(f => f.Kind == FindingKinds.Syntax ? f : f with { Deferred = true }).ToList();
        return current with { Findings = marked };
    }

    private static void Validate(AssayOptions options)
    {
        if (options.Viewports.Count == 0 || options.ColorSchemes.Count == 0)
            throw new ArgumentException("A run needs at least one viewport and one colour scheme.", nameof(options));
    }

    // Keeps at most PerKindLimit findings per kind (and per comparison status), counting the rest in one «more» finding each.
    private static AssayReport Limit(AssayReport all, AssayOptions options)
    {
        var kept = new List<Finding>();
        var counts = new Dictionary<(string, Comparison?), int>();
        var cut = new Dictionary<(string Kind, Comparison? Status), int>();
        foreach (var finding in all.Findings)
        {
            var group = (finding.Kind, finding.Status);
            var seen = counts.GetValueOrDefault(group);
            if (seen < options.PerKindLimits.GetValueOrDefault(finding.Kind, options.PerKindLimit)) kept.Add(finding);
            else cut[group] = cut.GetValueOrDefault(group) + 1;
            counts[group] = seen + 1;
        }
        foreach (var ((kind, status), count) in cut)
        {
            var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["of"] = kind, ["count"] = count };
            kept.Add(new Finding(FindingKinds.More, Severity.Notice, FindingKinds.TextOf(FindingKinds.More, fields), null, fields)
                { Status = status });
        }
        var truncated = kept.Count > options.TotalLimit;
        if (truncated) kept = kept.Take(options.TotalLimit).ToList();
        return all with { Findings = kept, Truncated = truncated };
    }

    private static async Task<AssayReport> RunAllAsync(Browser browser, Uri page, AssayOptions options, CancellationToken cancellationToken)
    {

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
        private string? _documentRequest;
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
            await EmulateAsync(options.Viewports[0], options.ColorSchemes[0], cancellationToken).ConfigureAwait(false);
            await Send("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = QuietTracker, ["worldName"] = World },
                cancellationToken).ConfigureAwait(false);

            _clock.Start();
            var navigation = await Send("Page.navigate", new JsonObject { ["url"] = page.AbsoluteUri }, cancellationToken)
                .ConfigureAwait(false);
            var context = new RunContext(engine, headless, page.Scheme == Uri.UriSchemeFile ? "file" : "http",
                options.Viewports, options.ColorSchemes);
            if (navigation.TryGetProperty("errorText", out var navigationError))
            {
                Add(FindingKinds.Resource, Fields(("url", PathOf(page)), ("error", navigationError.GetString())));
                return Build(loaded: false, settledMs: null, context, []);
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
                var first = true;
                foreach (var scheme in options.ColorSchemes)
                foreach (var viewport in options.Viewports)
                {
                    if (!first)
                    {
                        await EmulateAsync(viewport, scheme, cancellationToken).ConfigureAwait(false);
                        await WaitQuietAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    }
                    await LookAsync(first, scheme, viewport, cancellationToken).ConfigureAwait(false);
                    first = false;
                }
            }
            cdp.EventReceived = null;
            var found = loaded && options.StaticRules ? await StaticAsync(cancellationToken).ConfigureAwait(false) : null;
            return Build(loaded, settledMs, context with { Static = found is not null }, found ?? []);
        }

        // The static rules over the document the browser received, minus what the serving origin put in front of the page.
        private async Task<List<Finding>?> StaticAsync(CancellationToken cancellationToken)
        {
            string? request;
            lock (_gate) request = _documentRequest;
            if (request is null) return null;
            JsonElement body;
            try
            {
                body = await Send("Network.getResponseBody", new JsonObject { ["requestId"] = request }, cancellationToken).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                return null; // the browser no longer holds the body; the report states that the static rules did not run
            }
            var text = body.GetProperty("body").GetString() ?? "";
            if (body.GetProperty("base64Encoded").GetBoolean())
                text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text));
            for (var skip = 0; skip < options.PageLineOffset; skip++)
            {
                var newline = text.IndexOf('\n', StringComparison.Ordinal);
                text = newline < 0 ? "" : text[(newline + 1)..];
            }
            return StaticRules.Run(text, page.IsFile ? null : new Uri(page.GetLeftPart(UriPartial.Authority)), options);
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

        private async Task EmulateAsync(Viewport viewport, string scheme, CancellationToken cancellationToken)
        {
            await Send("Emulation.setDeviceMetricsOverride", new JsonObject
            {
                ["width"] = viewport.Width, ["height"] = viewport.Height, ["deviceScaleFactor"] = 1, ["mobile"] = false,
            }, cancellationToken).ConfigureAwait(false);
            await Send("Emulation.setEmulatedMedia", new JsonObject
            {
                ["features"] = new JsonArray(new JsonObject { ["name"] = "prefers-color-scheme", ["value"] = scheme }),
            }, cancellationToken).ConfigureAwait(false);
        }

        // After a change of viewport or scheme: the page may re-render (a media query, a resize handler) before it is looked at.
        private async Task WaitQuietAsync(TimeSpan limit, CancellationToken cancellationToken)
        {
            var deadline = _clock.ElapsedMilliseconds + (long)limit.TotalMilliseconds;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            while (_clock.ElapsedMilliseconds < deadline && !await IsQuietAsync(cancellationToken).ConfigureAwait(false))
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        private async Task LookAsync(bool first, string scheme, Viewport viewport, CancellationToken cancellationToken)
        {
            if (await EvaluateAsync(LookProbe.Script, cancellationToken).ConfigureAwait(false) is not { ValueKind: JsonValueKind.Object } look)
                return;
            var seenIn = new (string, JsonNode?)[] { ("scheme", scheme), ("viewport", viewport.ToString()) };
            if (look.GetProperty("overflow").GetInt32() is var overflow and > 0)
                AddOnce(FindingKinds.Overflow, Fields([("by", overflow), .. seenIn]));
            foreach (var item in look.GetProperty("contrast").EnumerateArray())
                AddOnce(FindingKinds.TextContrast, Fields([
                    ("selector", item.GetProperty("selector").GetString()), ("text", item.GetProperty("text").GetString()),
                    ("foreground", item.GetProperty("foreground").GetString()), ("background", item.GetProperty("background").GetString()),
                    ("ratio", item.GetProperty("ratio").GetDouble()), ("needed", item.GetProperty("needed").GetDouble()),
                    ("count", item.GetProperty("count").GetInt32()), .. seenIn]));
            // What follows does not depend on the scheme or the width: looked at once.
            if (!first) return;
            foreach (var item in look.GetProperty("tables").EnumerateArray())
                Add(FindingKinds.TableShape, Fields(("selector", item.GetProperty("selector").GetString()),
                    ("columns", JsonNode.Parse(item.GetProperty("columns").GetRawText())), ("rows", item.GetProperty("rows").GetInt32()),
                    ("cells", JsonNode.Parse(item.GetProperty("cells").GetRawText()))));
            foreach (var item in look.GetProperty("covering").EnumerateArray())
                Add(FindingKinds.Covering, Fields(("selector", item.GetProperty("selector").GetString()),
                    ("display", item.GetProperty("display").GetString()), ("area", item.GetProperty("area").GetDouble())));
            // The page caught an exception and printed its message: no error event and no console line exist, only the text.
            foreach (var item in look.GetProperty("errorText").EnumerateArray())
                Add(FindingKinds.ErrorOnScreen, Fields(("selector", item.GetProperty("selector").GetString()),
                    ("text", item.GetProperty("text").GetString())), PageAssay.Phase.Use);
            foreach (var item in look.GetProperty("unnamed").EnumerateArray())
                Add(FindingKinds.UnnamedField, Fields(("selector", item.GetProperty("selector").GetString()),
                    ("nearText", item.TryGetProperty("nearText", out var near) ? near.GetString() : null)));
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
                        if (p.TryGetProperty("type", out var documentType) && documentType.GetString() == "Document"
                            && p.TryGetProperty("frameId", out var requestFrame) && requestFrame.GetString() == frameId)
                            _documentRequest = p.GetProperty("requestId").GetString();
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
            var inPage = true;
            if (details.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var file) && SameOrigin(file)
                && file.AbsolutePath != page.AbsolutePath) // the page's own file goes unnamed, so a renamed version still matches
            {
                fields.Add(("file", PathOf(file)));
                inPage = false;
            }
            var line = details.GetProperty("lineNumber").GetInt32() + 1;
            if (inPage && options.PageLineOffset > 0)
            {
                // Inside what the server put in front of the page: no line of the page's file to point at.
                if (line <= options.PageLineOffset) fields.Add(("injected", true));
                else line -= options.PageLineOffset;
            }
            fields.Add(("line", line));
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

        // A look finding seen again in another viewport or scheme is the same finding (its key): reported where first seen.
        private void AddOnce(string kind, Dictionary<string, JsonNode?> fields)
        {
            var key = FindingKinds.KeyOf(kind, fields);
            lock (_gate)
                if (_found.Any(f => f.Finding.Key == key)) return;
            Add(kind, fields);
        }

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

        // The static findings first (they are about the file, before anything ran), then the driven ones in the order they happened.
        private AssayReport Build(bool loaded, long? settledMs, RunContext context, List<Finding> fromSource)
        {
            List<Finding> all;
            lock (_gate) all = [.. fromSource, .. _found.OrderBy(f => f.At).Select(f => f.Finding)];
            return new AssayReport(PathOf(page), loaded, settledMs, all, Truncated: false, context);
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
