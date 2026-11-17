namespace PageAssay.Tests;

public sealed class LoadTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>, IDisposable
{
    private readonly TestSite _site = new();

    private Browser Browser
    {
        get
        {
            Assert.SkipWhen(fixture.Browser is null, "No Chrome, Edge or Chromium on this machine (set PAGE_ASSAY_BROWSER).");
            return fixture.Browser!;
        }
    }

    private Task<AssayReport> Run(Uri page, AssayOptions? options = null) =>
        Assayer.RunAsync(Browser, page, options, TestContext.Current.CancellationToken);

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task A_clean_page_loads_settles_and_finds_nothing()
    {
        var page = _site.Page("index.html", "<!doctype html><title>t</title><p>Hello</p>");
        var report = await Run(page);
        Assert.True(report.Loaded);
        Assert.NotNull(report.SettledMs);
        Assert.Empty(report.Findings);
        Assert.Equal("/index.html", report.Page);
        Assert.False(report.Blocks);
    }

    [Fact]
    public async Task An_uncaught_error_while_loading_is_reported_with_its_line_in_the_page_file_and_phase_load()
    {
        var page = _site.Page("index.html", "<!doctype html>\n<p>x</p>\n<script>\nlet a = 1;\nmissing();\n</script>");
        var report = await Run(page);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Uncaught, finding.Kind);
        Assert.Equal(Phase.Load, finding.Phase);
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.Equal(5, (int)finding.Fields["line"]!);
        Assert.Contains("missing is not defined", finding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_after_the_page_settled_is_phase_use()
    {
        var page = _site.Page("index.html", "<!doctype html><p>x</p><script>setTimeout(() => Promise.reject(new Error('late')), 1200);</script>");
        var report = await Run(page, new AssayOptions { UseWindow = TimeSpan.FromSeconds(2) });
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Rejection, finding.Kind);
        Assert.Equal(Phase.Use, finding.Phase);
    }

    [Fact]
    public async Task Console_errors_are_notices_and_cut_at_the_per_kind_limit_with_a_count()
    {
        var page = _site.Page("index.html", "<!doctype html><script>for (let i = 0; i < 5; i++) console.error('bad', i);</script>");
        var report = await Run(page);
        Assert.Equal(3, report.Findings.Count(f => f.Kind == FindingKinds.ConsoleError));
        var more = Assert.Single(report.Findings, f => f.Kind == FindingKinds.More);
        Assert.Equal(2, (int)more.Fields["count"]!);
        Assert.Equal("bad 0", report.Findings[0].Fields["message"]!.ToString());
        Assert.False(report.Blocks);
    }

    [Fact]
    public async Task A_missing_file_from_the_pages_own_origin_is_a_resource_finding()
    {
        var page = _site.Page("index.html", "<!doctype html><script src=\"app.js\"></script>");
        var report = await Run(page);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Resource, finding.Kind);
        Assert.Equal("/app.js", finding.Fields["url"]!.ToString());
        Assert.Equal(404, (int)finding.Fields["status"]!);
    }

    [Fact]
    public async Task A_request_to_another_host_is_refused_and_reported_as_blocked()
    {
        var page = _site.Page("index.html",
            "<!doctype html><script>fetch('https://example.com/data.json').catch(() => {});</script>");
        var report = await Run(page);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Blocked, finding.Kind);
        Assert.Equal("example.com", finding.Fields["host"]!.ToString());
    }

    [Fact]
    public async Task An_engine_error_the_page_printed_itself_is_found_on_screen()
    {
        var page = _site.Page("index.html", """
            <!doctype html><div id="status"></div>
            <script>
            try { use(); const x = 1; function use() { return x; } }
            catch (e) { document.getElementById('status').textContent = 'Failed: ' + e.message; }
            </script>
            """);
        var report = await Run(page);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.ErrorOnScreen, finding.Kind);
        Assert.Equal("#status", finding.Fields["selector"]!.ToString());
        Assert.Contains("before initialization", finding.Fields["text"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settling_waits_for_data_the_page_fetches_after_load()
    {
        _site.File("data.json", "{\"n\": 3}", "application/json");
        var page = _site.Page("index.html", """
            <!doctype html><ul id="list"></ul>
            <script>
            setTimeout(async () => {
              const data = await (await fetch('data.json')).json();
              for (let i = 0; i < data.n; i++) document.getElementById('list').append(document.createElement('li'));
            }, 400);
            </script>
            """);
        var report = await Run(page);
        Assert.Empty(report.Findings);
        Assert.True(report.SettledMs >= 400, $"settled at {report.SettledMs} ms");
    }

    [Fact]
    public async Task The_report_serializes_to_the_documented_shape()
    {
        var page = _site.Page("index.html", "<!doctype html><script>console.error('x')</script>");
        var json = (await Run(page)).ToJson();
        Assert.Equal("/index.html", json["page"]!.ToString());
        var finding = json["findings"]![0]!;
        Assert.Equal("console-error", finding["kind"]!.ToString());
        Assert.Equal("notice", finding["severity"]!.ToString());
        Assert.Equal("load", finding["phase"]!.ToString());
        Assert.Equal("x", finding["message"]!.ToString());
        Assert.Contains("Chrome", json["context"]!["engine"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lines_are_counted_in_the_pages_own_file_when_the_server_put_markup_in_front_of_it()
    {
        const string injected = "<script>/* a serving origin's own script */</script>\n<meta name=\"x\">\n";
        var page = _site.Page("index.html", injected + "<!doctype html>\n<script>\nmissing();\n</script>");
        var report = await Run(page, new AssayOptions { PageLineOffset = 2 });
        var finding = Assert.Single(report.Findings);
        Assert.Equal(3, (int)finding.Fields["line"]!);
        Assert.False(finding.Fields.ContainsKey("injected"));
    }
}
