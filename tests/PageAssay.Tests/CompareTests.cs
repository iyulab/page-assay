namespace PageAssay.Tests;

public sealed class CompareTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>, IDisposable
{
    private readonly TestSite _site = new();

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task Comparing_two_versions_marks_what_the_change_broke_kept_and_fixed_matching_by_key_not_by_line()
    {
        Assert.SkipWhen(fixture.Browser is null, "No Chrome, Edge or Chromium on this machine (set PAGE_ASSAY_BROWSER).");
        var before = _site.Page("v1.html", """
            <!doctype html><script>
            console.error('old warning');
            missing();
            </script>
            """);
        var after = _site.Page("v2.html", """
            <!doctype html><script>
            // three new lines
            // move the error
            // further down
            console.error('new warning');
            missing();
            </script>
            """);
        var report = await Assayer.CompareAsync(fixture.Browser!, before, after, null, TestContext.Current.CancellationToken);

        var uncaught = Assert.Single(report.Findings, f => f.Kind == FindingKinds.Uncaught);
        Assert.Equal(Comparison.Kept, uncaught.Status);
        Assert.Equal(6, (int)uncaught.Fields["line"]!);
        var added = Assert.Single(report.Findings, f => f.Status == Comparison.New);
        Assert.Equal("new warning", added.Fields["message"]!.ToString());
        var fixedOne = Assert.Single(report.Findings, f => f.Status == Comparison.Gone);
        Assert.Equal("old warning", fixedOne.Fields["message"]!.ToString());
        Assert.Equal("/v2.html", report.Page);
        Assert.Equal("new", added.ToJson()["status"]!.ToString());
    }

    [Fact]
    public async Task A_plain_run_carries_keys_but_no_status()
    {
        Assert.SkipWhen(fixture.Browser is null, "No Chrome, Edge or Chromium on this machine (set PAGE_ASSAY_BROWSER).");
        var page = _site.Page("index.html", "<!doctype html><script>console.error('x')</script>");
        var report = await Assayer.RunAsync(fixture.Browser!, page, null, TestContext.Current.CancellationToken);
        var finding = Assert.Single(report.Findings);
        Assert.Null(finding.Status);
        Assert.Equal("console-error:x", finding.Key);
        Assert.False(finding.ToJson().ContainsKey("status"));
    }
}
