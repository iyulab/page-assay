using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace PageAssay.Static;

/// <summary>
/// The static half: reads a page's source without running it and reports what is wrong with its scripts, ids and references.
/// Inline scripts and handler attributes only — a script loaded from a file is not read.
/// </summary>
internal static partial class StaticRules
{
    private static readonly HashSet<string> BrowserGlobals = Names("browser-globals.txt");
    private static readonly HashSet<string> HandlerScope = Names("handler-scope.txt");

    private sealed record Draft(string Kind, Dictionary<string, JsonNode?> Fields, string? Anchor, (int Line, int Column) At);

    /// <summary>The findings of a page's source, in the order they appear in the file. <paramref name="origin"/> is the origin the
    /// page is served on, when known: URLs on it are not another host.</summary>
    public static List<Finding> Run(string html, Uri? origin, AssayOptions options)
    {
        var page = PageSource.Parse(html);
        var drafts = new List<Draft>();
        var units = new List<CodeUnit>();
        var broken = new List<(Draft Syntax, HashSet<string> Names)>();

        foreach (var script in page.Scripts)
        {
            try
            {
                var parser = new Parser();
                Node ast = script.Module ? parser.ParseModule(script.Code) : parser.ParseScript(script.Code);
                units.Add(new CodeUnit(units.Count, ast, script.Span, script, null));
            }
            catch (ParseErrorException e)
            {
                var syntax = SyntaxDraft(page, script.Span, e, $"script {script.Number}", "script:" + script.Hash);
                drafts.Add(syntax);
                broken.Add((syntax, WouldDefine(script.Code)));
            }
        }
        foreach (var handler in page.Handlers)
        {
            try
            {
                var ast = new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(handler.Code);
                units.Add(new CodeUnit(units.Count, ast, handler.Span, null, handler));
            }
            catch (ParseErrorException e)
            {
                drafts.Add(SyntaxDraft(page, handler.Span, e, $"{handler.Event} attribute of {handler.Selector}", HandlerAnchor(handler)));
            }
        }

        var model = new ScopeModel(units);
        var hidden = UndefinedCalls(page, model, drafts, broken);
        foreach (var (syntax, _) in broken)
            if (hidden.GetValueOrDefault(syntax) is var count and > 0)
                syntax.Fields["hides"] = count;
        MissingIds(page, model, drafts);
        DuplicateIds(page, drafts);
        OutsideReferences(page, model, origin, drafts);
        ReadsBeforeSet(page, model, units, drafts);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<Finding>();
        foreach (var draft in drafts.OrderBy(d => d.At.Line).ThenBy(d => d.At.Column))
        {
            var finding = new Finding(draft.Kind, options.Severities.TryGetValue(draft.Kind, out var s) ? s : FindingKinds.DefaultSeverity(draft.Kind),
                    FindingKinds.TextOf(draft.Kind, draft.Fields), null, draft.Fields)
                { Key = FindingKinds.KeyOf(draft.Kind, draft.Fields, draft.Anchor) };
            if (seen.Add(finding.Key)) findings.Add(finding); // the same defect again: reported where first seen
        }
        return findings;
    }

    private static Draft SyntaxDraft(PageSource page, SourceSpan span, ParseErrorException e, string source, string anchor)
    {
        var at = page.Locate(span, e.LineNumber, e.Column);
        return new Draft(FindingKinds.Syntax, Fields(("source", source), ("line", at.Line), ("column", at.Column), ("message", e.Description)),
            anchor, at);
    }

    private static string HandlerAnchor(HandlerAttribute handler) => $"handler:{handler.Anchor}:{handler.Event}";

    // Names a script that does not parse would have defined, read from its text: calls to them are effects of the parse failure.
    private static HashSet<string> WouldDefine(string code) =>
        new(Definitions().Matches(code).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value), StringComparer.Ordinal);

    [GeneratedRegex(@"\bfunction\s*\*?\s*([A-Za-z_$][\w$]*)|\bclass\s+([A-Za-z_$][\w$]*)|\b(?:var|let|const)\s+([A-Za-z_$][\w$]*)|\b(?:window|globalThis|self)\.([A-Za-z_$][\w$]*)\s*=(?!=)")]
    private static partial Regex Definitions();

    private static Dictionary<Draft, int> UndefinedCalls(PageSource page, ScopeModel model, List<Draft> drafts,
        List<(Draft Syntax, HashSet<string> Names)> broken)
    {
        var hidden = new Dictionary<Draft, int>();
        // A script read from a file, eval or with: what a name refers to cannot be known from the inline source.
        if (page.HasScriptFiles || model.Dynamic) return hidden;
        var declared = model.Bindings.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in model.References)
        {
            if ((reference.Use & ReferenceUse.Call) == 0 || reference.Binding is not null) continue;
            var name = reference.Node.Name;
            if (BrowserGlobals.Contains(name) || model.AssignedGlobals.Contains(name) || model.TypeOfNames.Contains(name)
                || (reference.Unit.Handler is not null && HandlerScope.Contains(name)))
                continue;
            var anchor = reference.Unit.Handler is { } handler ? HandlerAnchor(handler) : null;
            var reason = declared.Contains(name) ? "not-global" : "undefined";
            if (broken.FirstOrDefault(b => b.Names.Contains(name)).Syntax is { } cause)
            {
                if (counted.Add($"{name}|{reason}|{anchor}")) hidden[cause] = hidden.GetValueOrDefault(cause) + 1;
                continue;
            }
            var at = At(page, reference.Unit, reference.Node);
            drafts.Add(new Draft(FindingKinds.UndefinedCall,
                Fields(("name", name), ("reason", reason), ("line", at.Line), ("column", at.Column)), anchor, at));
        }
        return hidden;
    }

    private static void MissingIds(PageSource page, ScopeModel model, List<Draft> drafts)
    {
        if (model.Ids.Unknown) return;
        var ids = page.Ids.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var call in model.Calls)
        {
            if (call.Callee is not MemberExpression { Computed: false, Object: Identifier { Name: "document" } document, Property: Identifier method }
                || call.Scope.Lookup(document.Name) is not null
                || call.Arguments is not [StringLiteral literal, ..])
                continue;
            var id = method.Name switch
            {
                "getElementById" => literal.Value,
                "querySelector" or "querySelectorAll" => LeadingId(literal.Value),
                _ => null,
            };
            if (string.IsNullOrEmpty(id) || ids.Contains(id) || model.Ids.MayExist(id)) continue;
            var at = At(page, call.Unit, literal);
            drafts.Add(new Draft(FindingKinds.MissingId, Fields(("id", id), ("line", at.Line), ("column", at.Column)), null, at));
        }
    }

    // The id a selector starts with (#x, #x .y, #x > li); null for anything else, an escaped or listed selector included.
    private static string? LeadingId(string selector)
    {
        selector = selector.Trim();
        if (!selector.StartsWith('#') || selector.Contains(',', StringComparison.Ordinal) || selector.Contains('\\', StringComparison.Ordinal))
            return null;
        var end = 1;
        while (end < selector.Length && (char.IsLetterOrDigit(selector[end]) || selector[end] is '-' or '_' || selector[end] > 0x7F)) end++;
        return end > 1 ? selector[1..end] : null;
    }

    private static void DuplicateIds(PageSource page, List<Draft> drafts)
    {
        foreach (var group in page.Ids.GroupBy(i => i.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var lines = group.Select(i => page.Locate(i.Offset).Line).ToList();
            var at = page.Locate(group.First().Offset);
            drafts.Add(new Draft(FindingKinds.DuplicateId, Fields(("id", group.Key), ("count", lines.Count),
                ("lines", new JsonArray(lines.Select(l => (JsonNode)l).ToArray()))), null, at));
        }
    }

    private static void OutsideReferences(PageSource page, ScopeModel model, Uri? origin, List<Draft> drafts)
    {
        void Add(string rule, string url, bool code, (int Line, int Column) at)
        {
            if (OutsideHost(url, origin) is { } host)
                drafts.Add(new Draft(FindingKinds.OutsideReference,
                    Fields(("rule", rule), ("url", url), ("code", code), ("line", at.Line), ("column", at.Column)), host, at));
        }

        foreach (var url in page.Urls) Add(url.Rule, url.Url, url.Code, page.Locate(url.Offset));
        foreach (var style in page.Styles)
            foreach (var (rule, url, offset) in CssUrls.Scan(style.Css))
                Add(rule, url, false, page.Locate(style.Span.Exact ? style.Span.Offset + offset : style.Span.Offset));
        foreach (var (source, specifier, unit) in model.Imports)
            Add("import", specifier, true, At(page, unit, source));
        foreach (var call in model.Calls)
        {
            var isFetch = call.Callee switch
            {
                Identifier { Name: "fetch" } id => call.Scope.Lookup(id.Name) is null,
                MemberExpression { Computed: false, Object: Identifier { Name: "window" or "globalThis" or "self" }, Property: Identifier { Name: "fetch" } } => true,
                _ => false,
            };
            if (!isFetch || call.Arguments.Count == 0) continue;
            var url = call.Arguments[0] switch
            {
                StringLiteral s => s.Value,
                TemplateLiteral t => t.Quasis[0].Value.Cooked,
                _ => null,
            };
            if (url is not null) Add("fetch", url, true, At(page, call.Unit, call.Arguments[0]));
        }
    }

    // The host of an absolute or protocol-relative URL that is not the page's own origin; null for everything else (a relative
    // URL, data:, blob:, a fragment, mailto: and the like).
    private static string? OutsideHost(string url, Uri? origin)
    {
        url = url.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal)) url = (origin?.Scheme ?? "https") + ":" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss") || uri.Host.Length == 0)
            return null;
        var sameOrigin = origin is not null && string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == origin.Port && Secure(uri.Scheme) == Secure(origin.Scheme);
        return sameOrigin ? null : uri.Host;
    }

    private static bool Secure(string scheme) => scheme is "https" or "wss";

    private static void ReadsBeforeSet(PageSource page, ScopeModel model, List<CodeUnit> units, List<Draft> drafts)
    {
        foreach (var read in StartOrder.Follow(model, units))
        {
            var at = At(page, read.Unit, read.Read);
            var set = read.Binding.SetAt!.Value;
            var fields = Fields(("name", read.Binding.Name), ("call", read.Call), ("line", at.Line), ("column", at.Column),
                ("setAt", At(page, set.Unit, set.Node).Line), ("throws", read.Throws));
            drafts.Add(new Draft(FindingKinds.ReadBeforeSet, fields, null, at));
        }
    }

    private static (int Line, int Column) At(PageSource page, CodeUnit unit, Node node) =>
        page.Locate(unit.Span, node.Location.Start.Line, node.Location.Start.Column);

    private static Dictionary<string, JsonNode?> Fields(params (string Name, JsonNode? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    private static HashSet<string> Names(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PageAssay.Static." + resource)
            ?? throw new InvalidOperationException($"The embedded list {resource} is missing.");
        using var reader = new StreamReader(stream);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') names.Add(line.Trim());
        return names;
    }
}
