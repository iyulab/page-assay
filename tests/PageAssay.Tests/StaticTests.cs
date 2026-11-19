using System.Text.Json.Nodes;

namespace PageAssay.Tests;

/// <summary>The static rules: the page's source read without a browser.</summary>
public sealed class StaticTests
{
    private static readonly AssayOptions NoLimit = new() { PerKindLimit = 100 };

    private static AssayReport Run(string html, AssayOptions? options = null, Uri? origin = null) =>
        Assayer.RunStatic("index.html", html, origin, options);

    private static List<Finding> Of(AssayReport report, string kind) => report.Findings.Where(f => f.Kind == kind).ToList();

    private static int Int(Finding f, string field) => (int)f.Fields[field]!;

    private static string Str(Finding f, string field) => f.Fields[field]!.ToString();

    [Fact]
    public void A_clean_page_has_no_findings_and_states_that_only_the_static_half_ran()
    {
        var report = Run("""
            <!doctype html><title>t</title>
            <button id="save" onclick="save()">Save</button>
            <script>
            const items = JSON.parse(localStorage.getItem('items') || '[]');
            function save() { localStorage.setItem('items', JSON.stringify(items)); render(); }
            function render() { document.getElementById('save').textContent = 'Saved ' + items.length; }
            document.addEventListener('DOMContentLoaded', render);
            setTimeout(() => console.log(Math.max(1, 2)), 10);
            </script>
            """);
        Assert.Empty(report.Findings);
        Assert.Null(report.Loaded);
        var json = report.ToJson();
        Assert.False(json.ContainsKey("loaded"));
        Assert.True((bool)json["context"]!["static"]!);
        Assert.False((bool)json["context"]!["driven"]!);
        Assert.False(json["context"]!.AsObject().ContainsKey("engine"));
    }

    // ---- syntax ----

    [Fact]
    public void A_script_that_does_not_parse_is_reported_at_its_line_and_column_in_the_file()
    {
        var report = Run("<!doctype html>\n<p>x</p>\n<script>\nlet a = 1;\nlet b = (;\n</script>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Syntax, finding.Kind);
        Assert.Equal(Severity.Block, finding.Severity);
        Assert.Equal("script 1", Str(finding, "source"));
        Assert.Equal(5, Int(finding, "line"));
        Assert.Equal(10, Int(finding, "column"));
        Assert.DoesNotContain("(5:", Str(finding, "message"), StringComparison.Ordinal);
        Assert.StartsWith("The script 1 does not parse at line 5, column 10: ", finding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_on_the_scripts_first_line_counts_from_the_tag_and_crlf_line_breaks_count_once()
    {
        var report = Run("<!doctype html>\r\n<div>\r\n  <script>let = ;</script>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(3, Int(finding, "line"));
        Assert.Equal(17, Int(finding, "column")); // «  <script>» is 10 characters, «let = » 6 more: «;» is column 17
    }

    [Fact]
    public void A_module_script_parses_as_a_module_and_other_script_types_are_not_read()
    {
        var report = Run("""
            <script type="module">import { a } from './a.js'; export const b = a; await Promise.resolve();</script>
            <script type="application/json">{ not: javascript }</script>
            <script type="text/template"><div>{{ x }}</div></script>
            <script src="app.js">this is not run ( </script>
            """);
        Assert.Empty(Of(report, FindingKinds.Syntax));
    }

    [Fact]
    public void A_handler_attribute_that_does_not_parse_names_its_attribute_and_element()
    {
        var report = Run("<!doctype html>\n<button id=\"go\" onclick=\"start(\">Go</button>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.Syntax, finding.Kind);
        Assert.Equal("onclick attribute of #go", Str(finding, "source"));
        Assert.Equal(2, Int(finding, "line"));
        Assert.Equal("syntax:handler:#go:onclick", finding.Key);
    }

    [Fact]
    public void A_handler_may_return_and_names_its_element_reaches()
    {
        var report = Run("""
            <form><input name="q"><button onclick="if (!q.value) return false; submit(); focus()">Go</button></form>
            """);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void A_broken_script_is_reported_once_and_the_calls_to_names_it_would_define_are_counted_not_reported()
    {
        var report = Run("""
            <!doctype html>
            <button onclick="save()">Save</button>
            <button onclick="restore()">Restore</button>
            <script>
            function save() { store(); }
            function restore() { return 1 +; }
            </script>
            <script>save(); never();</script>
            """);
        var syntax = Assert.Single(Of(report, FindingKinds.Syntax));
        Assert.Equal(3, Int(syntax, "hides")); // save() and restore() from the handlers, save() from the script
        Assert.Contains("3 call(s) to names it would define", syntax.Text, StringComparison.Ordinal);
        var call = Assert.Single(Of(report, FindingKinds.UndefinedCall));
        Assert.Equal("never", Str(call, "name"));
    }

    // ---- undefined-call ----

    [Fact]
    public void A_name_called_but_defined_nowhere_is_undefined()
    {
        var report = Run("<!doctype html>\n<button onclick=\"savee()\">Save</button>\n<script>function save() {}</script>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.UndefinedCall, finding.Kind);
        Assert.Equal("savee", Str(finding, "name"));
        Assert.Equal("undefined", Str(finding, "reason"));
        Assert.Equal(2, Int(finding, "line"));
        Assert.Equal(18, Int(finding, "column"));
        Assert.Equal("savee() is called at line 2, column 18, but nothing defines it.", finding.Text);
    }

    [Fact]
    public void A_handler_calling_a_function_that_exists_only_inside_a_closure_is_not_global()
    {
        var report = Run("""
            <button id="add" onclick="addItem()">Add</button>
            <script>
            (function () {
              function addItem() { console.log('added'); }
              document.getElementById('add').title = 'Add';
            })();
            </script>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("not-global", Str(finding, "reason"));
        Assert.Equal("undefined-call:addItem|not-global|handler:#add:onclick", finding.Key);
    }

    [Fact]
    public void A_module_scripts_top_level_names_are_not_globals()
    {
        var report = Run("""
            <button onclick="toggle()">Toggle</button>
            <script type="module">export function toggle() {} toggle();</script>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("toggle", Str(finding, "name"));
        Assert.Equal("not-global", Str(finding, "reason"));
    }

    [Fact]
    public void Globals_come_from_any_classic_script_window_assignments_and_browser_names()
    {
        var report = Run("""
            <button onclick="a(); b(); c(); d(); alert('x'); fetchLater2 && 1">x</button>
            <script>const a = () => {};</script>
            <script>window.b = function () {}; globalThis['c'] = () => {}; d = () => {};</script>
            """);
        Assert.Empty(Of(report, FindingKinds.UndefinedCall));
    }

    [Fact]
    public void A_name_the_page_tests_with_typeof_or_a_page_that_loads_script_files_is_not_reported()
    {
        Assert.Empty(Run("<script>if (typeof maybe === 'function') maybe();</script>").Findings);
        Assert.Empty(Of(Run("<script src=\"lib.js\"></script><script>fromLibrary();</script>"), FindingKinds.UndefinedCall));
    }

    [Fact]
    public void A_module_that_imports_a_file_or_a_name_read_through_window_turns_undefined_call_off_for_it()
    {
        Assert.Empty(Run("<button onclick=\"save()\">Save</button><script type=\"module\">import './app.js';</script>").Findings);
        Assert.Empty(Run("<script>if (window.analytics) analytics();</script>").Findings);
    }

    [Fact]
    public void A_parameter_of_the_same_name_elsewhere_does_not_make_a_call_not_global()
    {
        var finding = Assert.Single(Run("<script>function run(render) { return render; } render();</script>").Findings);
        Assert.Equal("undefined", Str(finding, "reason"));
    }

    [Fact]
    public void A_byte_order_mark_is_not_a_column()
    {
        var finding = Assert.Single(Run("\uFEFF<script>nope();</script>").Findings);
        Assert.Equal(9, Int(finding, "column"));
    }

    [Fact]
    public void Parameters_and_locals_shadow_and_a_call_through_a_member_is_not_judged()
    {
        Assert.Empty(Run("""
            <script>
            function run(callback) { callback(); const inner = () => 1; inner(); }
            const api = {}; api.missing?.();
            try { risky(); } catch (e) { e.toString(); }
            function risky() {}
            </script>
            """).Findings);
    }

    // ---- missing-id / duplicate-id ----

    [Fact]
    public void Looking_up_an_id_the_markup_does_not_have_is_missing_id()
    {
        var report = Run("""
            <!doctype html><div id="list"></div>
            <script>
            document.getElementById('list');
            document.getElementById('lsit');
            document.querySelector('#total > span');
            document.querySelectorAll('#list li');
            </script>
            """);
        var missing = Of(report, FindingKinds.MissingId);
        Assert.Equal(["lsit", "total"], missing.Select(f => Str(f, "id")));
        Assert.Equal(4, Int(missing[0], "line"));
        Assert.Equal(25, Int(missing[0], "column"));
        Assert.Equal("missing-id:lsit", missing[0].Key);
    }

    [Fact]
    public void Ids_the_code_creates_are_not_missing()
    {
        Assert.DoesNotContain(Run("""
            <ul id="list"></ul>
            <script>
            list.innerHTML = '<li id="first">a</li>';
            const row = document.createElement('div'); row.id = 'row-' + 3; document.body.append(row);
            const t = `<span id="total"></span>`;
            document.getElementById('first'); document.getElementById('row-3'); document.querySelector('#total');
            </script>
            """).Findings, f => f.Kind == FindingKinds.MissingId);
    }

    [Fact]
    public void Ids_inside_a_template_are_there_once_the_code_clones_it()
    {
        Assert.Empty(Run("""
            <template id="row"><li><span id="count"></span></li></template><ul id="list"></ul>
            <script>
            list.append(document.getElementById('row').content.cloneNode(true));
            document.getElementById('count').textContent = '1';
            </script>
            """).Findings);
    }

    [Fact]
    public void An_id_given_from_an_expression_the_source_does_not_spell_out_turns_the_rule_off()
    {
        Assert.Empty(Run("<script>const el = document.createElement('p'); el.id = name(); document.getElementById('x'); function name() { return 'x'; }</script>").Findings);
    }

    [Fact]
    public void An_id_carried_twice_is_a_duplicate_id_notice()
    {
        var report = Run("<!doctype html>\n<p id=\"a\">1</p>\n<p id=\"b\">2</p>\n<p id=\"a\">3</p>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.DuplicateId, finding.Kind);
        Assert.Equal(Severity.Notice, finding.Severity);
        Assert.Equal(2, Int(finding, "count"));
        Assert.Equal([2, 4], finding.Fields["lines"]!.AsArray().Select(n => (int)n!));
        Assert.Equal("2 elements have the id \"a\" (lines 2, 4): a lookup finds only the first.", finding.Text);
        Assert.False(report.Blocks);
    }

    // ---- outside-reference ----

    [Fact]
    public void Code_that_reaches_another_host_is_an_outside_reference_with_code_true()
    {
        var report = Run("""
            <!doctype html>
            <script src="https://cdn.example.com/lib.js"></script>
            <script type="module">
            import { x } from 'https://esm.example.net/x.js';
            const y = await import("//esm.example.org/y.js");
            fetch(`https://api.example.com/items/${x}`);
            fetch('data.json'); fetch('/api'); import('./local.js');
            </script>
            """, NoLimit);
        var outside = Of(report, FindingKinds.OutsideReference);
        Assert.Equal(["script-src", "import", "import", "fetch"], outside.Select(f => Str(f, "rule")));
        Assert.All(outside, f => Assert.True((bool)f.Fields["code"]!));
        Assert.Equal("https://api.example.com/items/", Str(outside[3], "url"));
        Assert.Equal("outside-reference:fetch|api.example.com", outside[3].Key);
        Assert.Equal(2, Int(outside[0], "line"));
    }

    [Fact]
    public void Files_and_css_from_another_host_are_outside_references_with_code_false()
    {
        var report = Run("""
            <!doctype html>
            <link rel="stylesheet" href="https://fonts.example.com/css">
            <link rel="modulepreload" href="https://esm.example.com/m.js">
            <link rel="canonical" href="https://example.com/page">
            <style>
            @import url("https://css.example.com/a.css");
            @import 'https://css2.example.com/b.css';
            body { background: url(https://img.example.com/bg.png); }
            /* url(https://commented.example.com/x.png) */
            </style>
            <div style="background-image: url('https://inline.example.com/x.png')"></div>
            <img src="https://img.example.com/a.png" srcset="https://srcset.example.com/a2.png 2x">
            <video poster="https://media.example.com/p.jpg"><source src="https://media.example.com/v.mp4"><track src="https://subs.example.com/t.vtt"></video>
            <iframe src="https://frame.example.com/"></iframe>
            <object data="https://obj.example.com/a.pdf"></object>
            <form action="https://forms.example.com/submit"></form>
            <img src="data:image/png;base64,AAAA"><img src="#x"><a href="https://example.com/">a link is not fetched</a>
            """, NoLimit);
        var outside = Of(report, FindingKinds.OutsideReference);
        Assert.Equal(
            ["link", "link", "css-import", "css-import", "css-url", "css-url", "img", "img", "media", "media", "frame", "object", "form-action"],
            outside.Select(f => Str(f, "rule")));
        Assert.True((bool)outside[1].Fields["code"]!); // a module preload is code
        Assert.All(outside.Where(f => f != outside[1]), f => Assert.False((bool)f.Fields["code"]!));
        Assert.Equal(6, Int(outside[2], "line"));
        Assert.Equal(14, Int(outside[2], "column"));
    }

    [Fact]
    public void Seventeen_images_from_one_host_are_one_finding_and_the_page_origin_is_not_outside()
    {
        var images = string.Concat(Enumerable.Range(1, 17).Select(i => $"<img src=\"https://cdn.example.com/{i}.png\">\n"));
        var report = Run(images + "<img src=\"http://127.0.0.1:8080/own.png\"><script>fetch('http://127.0.0.1:8080/api')</script>",
            origin: new Uri("http://127.0.0.1:8080/"));
        var finding = Assert.Single(report.Findings);
        Assert.Equal("https://cdn.example.com/1.png", Str(finding, "url"));
        Assert.Equal(1, Int(finding, "line"));
    }

    // ---- read-before-set ----

    [Fact]
    public void A_function_run_at_start_that_reads_a_const_declared_below_the_call_throws()
    {
        var report = Run("""
            <!doctype html>
            <script>
            init();
            const items = [];
            function init() { render(); }
            function render() { return items.length; }
            </script>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.ReadBeforeSet, finding.Kind);
        Assert.Equal("items", Str(finding, "name"));
        Assert.Equal("init", Str(finding, "call"));
        Assert.Equal(6, Int(finding, "line"));
        Assert.Equal(4, Int(finding, "setAt"));
        Assert.True((bool)finding.Fields["throws"]!);
        Assert.Equal("read-before-set:init|items", finding.Key);
        Assert.Equal("While init() runs at start, items is read at line 6, column 28 before line 4 gives it a value: the read throws.", finding.Text);
    }

    [Fact]
    public void Under_var_the_read_is_undefined_not_a_throw()
    {
        var report = Run("<script>\nshow();\nvar title = 'x';\nfunction show() { return title.length; }\n</script>");
        var finding = Assert.Single(report.Findings);
        Assert.False((bool)finding.Fields["throws"]!);
        Assert.EndsWith("it is undefined there.", finding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_try_around_the_read_does_not_hide_it()
    {
        // The same page the driven half sees printing «Cannot access 'x' before initialization».
        var report = Run("<script>\ntry { use(); const x = 1; function use() { return x; } }\ncatch (e) { console.log(e.message); }\n</script>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal("x", Str(finding, "name"));
        Assert.Equal("use", Str(finding, "call"));
        Assert.True((bool)finding.Fields["throws"]!);
    }

    [Fact]
    public void An_iife_runs_at_once_and_its_own_order_counts()
    {
        var report = Run("""
            <script>
            (() => {
              draw();
              let size = 3;
              function draw() { return size * 2; }
            })();
            </script>
            """);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("size", Str(finding, "name"));
        Assert.Equal("draw", Str(finding, "call"));
    }

    [Fact]
    public void Listeners_for_dom_content_loaded_and_load_run_after_every_script()
    {
        Assert.Empty(Run("""
            <script>
            document.addEventListener('DOMContentLoaded', () => start(config));
            window.addEventListener('load', () => start());
            window.onload = () => start(config);
            </script>
            <script>
            const config = { a: 1 };
            function start(c) { return c ?? config; }
            </script>
            """).Findings);
    }

    [Fact]
    public void Calling_at_start_a_function_a_later_script_declares_throws()
    {
        var report = Run("<script>\nboot();\n</script>\n<script>\nfunction boot() {}\n</script>");
        var finding = Assert.Single(report.Findings);
        Assert.Equal(FindingKinds.ReadBeforeSet, finding.Kind);
        Assert.Equal("boot", Str(finding, "name"));
        Assert.False(finding.Fields.ContainsKey("call"));
        Assert.Equal(5, Int(finding, "setAt"));
        Assert.True((bool)finding.Fields["throws"]!);
    }

    [Fact]
    public void Functions_defined_later_but_only_called_later_and_callbacks_are_not_reported()
    {
        Assert.Empty(Run("""
            <script>
            function later() { return data.length; }      // declared first, called only after data has a value
            const data = [1, 2];
            later();
            setTimeout(() => more.push(1), 0);           // a callback runs later
            button?.addEventListener?.('click', () => more.push(2));
            const more = [];
            var cache = cache || {};                      // a var reading itself on purpose
            if (typeof settings !== 'undefined') use(settings);
            var settings = {};
            function use(s) { return s; }
            async function load() { await fetch('x.json'); return items.length; }
            load();
            const items = [];
            </script>
            """).Findings);
    }

    [Fact]
    public void A_body_onload_handler_runs_after_every_script()
    {
        var report = Run("<body onload=\"start()\">\n<script>\nfunction start() { return ready.length; }\nvar ready;\n</script>");
        Assert.Empty(report.Findings);
        // It is followed: what it calls runs, in its own order.
        var early = Assert.Single(Run("<body onload=\"start()\">\n<script>\nfunction start() { use(); const v = 1; function use() { return v; } }\n</script>").Findings);
        Assert.Equal("v", Str(early, "name"));
        Assert.Equal("start", Str(early, "call"));
    }

    [Fact]
    public void A_ready_state_test_is_a_guard_whose_branches_are_not_followed()
    {
        Assert.Empty(Run("""
            <script>
            if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
            const items = [];
            function init() { return items.length; }
            </script>
            """).Findings);
    }

    // ---- keys, limits, comparison ----

    [Fact]
    public void A_script_inserted_above_another_keeps_the_latters_keys()
    {
        const string broken = "<script>function a( {</script>\n";
        const string other = "<button onclick=\"nope()\">x</button>\n<script>document.getElementById('gone');</script>\n";
        var before = Run(broken + other);
        var after = Run("<script>// a new first script\nconst z = 1;</script>\n\n\n" + broken + other);
        Assert.Equal(before.Findings.Select(f => f.Key), after.Findings.Select(f => f.Key));
        Assert.Equal("script 1", Str(before.Findings[0], "source"));
        Assert.Equal("script 2", Str(after.Findings[0], "source"));
        Assert.StartsWith("syntax:script:", before.Findings[0].Key, StringComparison.Ordinal);
        Assert.Contains("undefined-call:nope|undefined|handler:button \"x\":onclick", before.Findings.Select(f => f.Key));
    }

    [Fact]
    public void Static_comparison_marks_new_kept_and_gone_without_a_browser()
    {
        var report = Assayer.CompareStatic("index.html",
            "<script>document.getElementById('a'); oldCall();</script>",
            "<script>\n\n// moved down\ndocument.getElementById('a'); newCall();</script>");
        Assert.Equal(Comparison.Kept, Assert.Single(report.Findings, f => f.Kind == FindingKinds.MissingId).Status);
        Assert.Equal("newCall", Str(Assert.Single(report.Findings, f => f.Status == Comparison.New), "name"));
        Assert.Equal("oldCall", Str(Assert.Single(report.Findings, f => f.Status == Comparison.Gone), "name"));
        Assert.DoesNotContain(report.Findings, f => f.Deferred);
    }

    [Fact]
    public void A_version_with_more_scripts_that_do_not_parse_defers_every_other_finding()
    {
        var report = Assayer.CompareStatic("index.html",
            "<p id=\"x\"></p><p id=\"x\"></p><script>ok();function ok(){}</script>",
            "<p id=\"x\"></p><p id=\"x\"></p><script>ok(;</script>");
        Assert.False(Assert.Single(report.Findings, f => f.Kind == FindingKinds.Syntax).Deferred);
        Assert.True(Assert.Single(report.Findings, f => f.Kind == FindingKinds.DuplicateId).Deferred);
    }

    [Fact]
    public void Static_findings_are_cut_at_the_per_kind_limit_and_take_severity_overrides()
    {
        var calls = string.Concat(Enumerable.Range(1, 5).Select(i => $"missing{i}();"));
        var report = Run($"<script>{calls}</script><p id=a></p><p id=a></p>", new AssayOptions
        {
            Severities = new Dictionary<string, Severity> { [FindingKinds.DuplicateId] = Severity.Block },
        });
        Assert.Equal(3, Of(report, FindingKinds.UndefinedCall).Count);
        Assert.Equal(2, Int(Assert.Single(report.Findings, f => f.Kind == FindingKinds.More), "count"));
        Assert.Equal(Severity.Block, Assert.Single(Of(report, FindingKinds.DuplicateId)).Severity);
    }

    [Fact]
    public void A_finding_serializes_with_its_fields_flat_and_no_null_fields()
    {
        var json = Run("<script>init();\nlet n = 1;\nfunction init() { return n; }</script>").Findings[0].ToJson();
        Assert.Equal("read-before-set", json["kind"]!.ToString());
        Assert.Equal("init", json["call"]!.ToString());
        Assert.Equal(JsonValueKind(true), json["throws"]!.GetValueKind());
        Assert.DoesNotContain(json, p => p.Value is null);
    }

    private static System.Text.Json.JsonValueKind JsonValueKind(bool value) =>
        value ? System.Text.Json.JsonValueKind.True : System.Text.Json.JsonValueKind.False;
}

public sealed class StaticRobustnessTests
{
    [Fact]
    public void Current_syntax_parses_clean_and_produces_no_findings()
    {
        var report = Assayer.RunStatic("index.html", """
            <!doctype html><main id="app"></main>
            <script>
            class Store { #items = []; static count = 0; static { Store.count = 1; } get size() { return this.#items.length; }
              add(x) { this.#items.push(x); return this; } }
            const re = /(?<year>\d{4})-(?<=a)b/u, re2 = /[\p{L}--\p{Ll}]/v;
            const { a = 1, ...rest } = { b: 2 }; const [p, , q = p] = [1, 2];
            label: for (const [k, v] of Object.entries(rest)) { if (!k) continue label; else break label; }
            for (let i = 0, j = 1; i < j; i++) {}
            switch (a) { case 1: { let s = 1; break; } default: }
            const tag = (s, ...v) => s.raw.join('') + v; tag`x${a}y`;
            const big = 10n ** 3n, sep = 1_000, opt = rest?.b ?? null; let n = 0; n ||= 1; n &&= 2; n ??= 3;
            async function* gen() { yield* [1]; for await (const x of gen()) { void x; } }
            function outer() { return arguments.length + new.target; }
            new Store().add(1).size; outer(); gen(); re.test(''); re2.test(''); big + sep + opt + q;
            document.getElementById('app').append(new Store().size);
            </script>
            """);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Odd_markup_never_throws()
    {
        foreach (var html in new[] { "", "<script>", "<script></script>", "<p id=>", "<img srcset=\",,\">", "<style>url(", "<style>@import",
                     "<div style=\"background:url('\">", "<button onclick=\"\">x</button>", "<script type=module>import(</script>",
                     "<p id=\"a&amp;b\" onclick=\"go(&quot;x&quot;); bad(\">", "<template><script>bad(</script><p id=t></p></template>" })
            _ = Assayer.RunStatic("index.html", html);
    }
}
