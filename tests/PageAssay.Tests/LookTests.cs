namespace PageAssay.Tests;

public sealed class LookTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>, IDisposable
{
    private readonly TestSite _site = new();

    public void Dispose() => _site.Dispose();

    private async Task<AssayReport> Run(string html)
    {
        Assert.SkipWhen(fixture.Browser is null, "No Chrome, Edge or Chromium on this machine (set PAGE_ASSAY_BROWSER).");
        return await Assayer.RunAsync(fixture.Browser!, _site.Page("index.html", html), null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pale_text_is_one_contrast_finding_per_colour_pair_with_a_count()
    {
        var report = await Run("""
            <!doctype html><body style="background:#fff">
            <p style="color:#999">one</p><p style="color:#999">two</p><p style="color:#999">three</p>
            <p style="color:#000">fine</p>
            </body>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.TextContrast, finding.Kind);
        Assert.Equal(Severity.Notice, finding.Severity);
        Assert.Equal("#999999", finding.Fields["foreground"]!.ToString());
        Assert.Equal("#ffffff", finding.Fields["background"]!.ToString());
        Assert.Equal(3, (int)finding.Fields["count"]!);
        Assert.Equal(4.5, (double)finding.Fields["needed"]!);
        Assert.InRange((double)finding.Fields["ratio"]!, 2.8, 2.9);
    }

    [Fact]
    public async Task Large_text_needs_three_to_one_and_a_translucent_background_is_composited()
    {
        // #767676 on white is 4.54:1 — enough at any size; #949494 is 3.03:1 — enough only for large text.
        var report = await Run("""
            <!doctype html><body>
            <h1 style="font-size:32px;color:#949494">Large</h1>
            <div style="background:rgba(0,0,0,0.7)"><span style="color:#fff">on a 70%-black layer</span></div>
            </body>
            """);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task White_text_on_a_half_black_layer_is_measured_on_the_composited_grey()
    {
        var report = await Run("""<!doctype html><div style="background:rgba(0,0,0,0.5)"><span style="color:#fff">grey</span></div>""");
        var finding = Assert.Single(report.Findings);
        Assert.Equal("#7f7f7f", finding.Fields["background"]!.ToString());
    }

    [Fact]
    public async Task Transparent_text_and_text_over_a_picture_are_skipped_not_guessed()
    {
        var report = await Run("""
            <!doctype html><body>
            <p style="color:transparent">hidden text</p>
            <div style="background-image:linear-gradient(#fff,#eee)"><p style="color:#ddd">over a gradient</p></div>
            </body>
            """);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task An_empty_fields_placeholder_is_measured_in_its_own_colour()
    {
        var report = await Run("""
            <!doctype html><style>input::placeholder { color: #ccc }</style>
            <label>Name <input placeholder="Your name"></label>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.TextContrast, finding.Kind);
        Assert.Equal("Your name", finding.Fields["text"]!.ToString());
    }

    [Fact]
    public async Task A_field_without_a_name_is_reported_with_the_text_beside_it()
    {
        var report = await Run("""
            <!doctype html>
            <div><span>Amount</span> <input id="amount" type="number"></div>
            <label for="a">A</label><input id="a">
            <span id="t">Total</span><input aria-labelledby="t">
            <input aria-label="Search">
            <select title="Currency"><option>USD</option></select>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.UnnamedField, finding.Kind);
        Assert.Equal("#amount", finding.Fields["selector"]!.ToString());
        Assert.Equal("Amount", finding.Fields["nearText"]!.ToString());
    }

    [Fact]
    public async Task Text_unreadable_only_in_the_dark_scheme_is_found_with_the_scheme_it_was_seen_in()
    {
        var report = await Run("""
            <!doctype html><meta name="color-scheme" content="light dark">
            <style>
              body { background: #fff; color: #222 }
              @media (prefers-color-scheme: dark) { body { background: #111; color: #333 } }
            </style>
            <p>Night text</p>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.TextContrast, finding.Kind);
        Assert.Equal("dark", finding.Fields["scheme"]!.ToString());
        Assert.Equal("#111111", finding.Fields["background"]!.ToString());
    }

    [Fact]
    public async Task A_layout_wider_than_a_phone_is_an_overflow_at_that_width_only()
    {
        var report = await Run("""<!doctype html><meta name="viewport" content="width=device-width"><div style="width:900px">wide</div>""");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Overflow, finding.Kind);
        Assert.Equal("390x844", finding.Fields["viewport"]!.ToString());
        Assert.True((int)finding.Fields["by"]! > 400);
    }

    [Fact]
    public async Task A_table_whose_rows_do_not_match_its_header_is_a_table_shape_finding()
    {
        var report = await Run("""
            <!doctype html>
            <table id="t"><thead><tr><th>Date</th><th>Item</th><th>Amount</th></tr></thead>
            <tbody><tr><td>1</td><td>a</td><td>3</td></tr><tr><td>2</td><td>b</td></tr><tr><td colspan="2">x</td><td>4</td></tr></tbody></table>
            <table><tr><th>A</th></tr><tr><td rowspan="2">skipped</td></tr><tr></tr></table>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.TableShape, finding.Kind);
        Assert.Equal("#t", finding.Fields["selector"]!.ToString());
        Assert.Equal(1, (int)finding.Fields["rows"]!);
        Assert.Equal("[\"Date\",\"Item\",\"Amount\"]", finding.Fields["columns"]!.ToJsonString());
        Assert.Equal("[2]", finding.Fields["cells"]!.ToJsonString());
    }

    [Fact]
    public async Task A_hidden_overlay_shown_anyway_covers_the_page_but_a_full_page_app_root_does_not()
    {
        var report = await Run("""
            <!doctype html><style>.modal { display: flex; position: fixed; inset: 0; background: rgba(0,0,0,.4) }</style>
            <div id="app" style="position:fixed;inset:0">the app</div>
            <div id="dialog" class="modal" hidden><p style="color:#fff;background:#000">Saved</p></div>
            <div hidden>really hidden</div>
            """);
        var finding = Assert.Single(report.Findings, f => f.Kind == FindingKinds.Covering);
        Assert.Equal("#dialog", finding.Fields["selector"]!.ToString());
        Assert.Equal("flex", finding.Fields["display"]!.ToString());
        Assert.Equal(Severity.Block, finding.Severity);
    }

    [Fact]
    public async Task The_context_names_the_template_version()
    {
        var report = await Run("<!doctype html><p>x</p>");
        Assert.Equal(FindingKinds.TemplatesVersion, report.ToJson()["context"]!["templates"]!.ToString());
    }
}
