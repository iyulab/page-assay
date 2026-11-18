using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PageAssay.Static;

/// <summary>Where a piece of code or text taken out of the markup sits in the page's file.</summary>
/// <param name="Offset">The offset of its first character in the (line-break normalised) file.</param>
/// <param name="Exact">False when the markup changed the text on the way (an entity in an attribute value): every position inside
/// it is then reported at its start, since a position counted in the decoded text would point at the wrong column.</param>
internal readonly record struct SourceSpan(int Offset, bool Exact);

/// <summary>An inline script the browser runs: a JavaScript type, no <c>src</c>.</summary>
internal sealed record InlineScript(int Number, string Code, bool Module, SourceSpan Span)
{
    /// <summary>The script's identity across versions: a hash of its text, never its position among the page's scripts.</summary>
    public string Hash { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Code)))[..12];
}

/// <summary>An event-handler attribute (<c>onclick="…"</c>): its name, its code, its element's selector (in the shape the driven
/// half uses), what identifies the element across versions (its id, else its label, else its selector), and where the code is.</summary>
internal sealed record HandlerAttribute(string Event, string Code, string Selector, string Anchor, SourceSpan Span);

/// <summary>A URL the markup asks for (an attribute or a CSS reference), with the rule it falls under.</summary>
internal sealed record MarkupUrl(string Rule, string Url, bool Code, int Offset);

/// <summary>An element id in the markup.</summary>
internal sealed record ElementId(string Id, int Offset);

/// <summary>CSS the page carries: a <c>&lt;style&gt;</c> block or a <c>style</c> attribute.</summary>
internal sealed record StyleText(string Css, SourceSpan Span);

/// <summary>The parts of a page's markup the static rules read, each with its place in the file.</summary>
internal sealed class PageSource
{
    private static readonly HashSet<string> JavaScriptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "module", "text/javascript", "application/javascript", "text/ecmascript", "application/ecmascript",
        "application/x-ecmascript", "application/x-javascript", "text/javascript1.0", "text/javascript1.1", "text/javascript1.2",
        "text/javascript1.3", "text/javascript1.4", "text/javascript1.5", "text/jscript", "text/livescript", "text/x-ecmascript",
        "text/x-javascript",
    };

    private readonly int[] _lineStarts;

    private PageSource(string text)
    {
        Text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = [.. starts];
    }

    /// <summary>The page's file with every line break as <c>\n</c> (what an HTML parser reads; lines are unchanged).</summary>
    public string Text { get; }

    public List<InlineScript> Scripts { get; } = [];
    public List<HandlerAttribute> Handlers { get; } = [];
    public List<MarkupUrl> Urls { get; } = [];
    public List<ElementId> Ids { get; } = [];
    public List<StyleText> Styles { get; } = [];

    /// <summary>The page loads a script from a file, which the static rules do not read: the names it defines are unknown.</summary>
    public bool HasScriptFiles { get; private set; }

    /// <summary>The 1-based line and column of an offset in <see cref="Text"/>.</summary>
    public (int Line, int Column) Locate(int offset)
    {
        var index = Array.BinarySearch(_lineStarts, offset);
        if (index < 0) index = ~index - 1;
        return (index + 1, offset - _lineStarts[index] + 1);
    }

    /// <summary>The file position of a position inside a span: <paramref name="line"/> 1-based, <paramref name="column"/> 0-based,
    /// counted in the span's own text (what a JavaScript parser reports).</summary>
    public (int Line, int Column) Locate(SourceSpan span, int line, int column)
    {
        var (startLine, startColumn) = Locate(span.Offset);
        if (!span.Exact || line < 1) return (startLine, startColumn);
        return line == 1 ? (startLine, startColumn + column) : (startLine + line - 1, column + 1);
    }

    public static PageSource Parse(string html)
    {
        var text = html.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var page = new PageSource(text);
        var document = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true, IsScripting = true })
            .ParseDocument(text);
        var scriptNumber = 0;
        foreach (var element in document.All)
        {
            var tag = StartTag.At(text, element);
            if (element.LocalName == "script")
                page.ReadScript(element, tag, ++scriptNumber);
            if (element.LocalName == "style" && tag is not null)
                page.Styles.Add(new StyleText(element.TextContent, ContentSpan(text, tag, element.TextContent)));
            if (element.Id is { Length: > 0 } id)
                page.Ids.Add(new ElementId(id, tag?.Offset ?? 0));
            foreach (var attribute in element.Attributes)
            {
                var name = attribute.LocalName;
                var span = tag?.SpanOf(name, attribute.Value) ?? new SourceSpan(tag?.Offset ?? 0, false);
                if (name.StartsWith("on", StringComparison.Ordinal) && name.Length > 2)
                    page.Handlers.Add(new HandlerAttribute(name, attribute.Value, Selectors.Of(element), Selectors.AnchorOf(element), span));
                else if (name == "style")
                    page.Styles.Add(new StyleText(attribute.Value, span));
                else if (RuleOf(element, name) is { } rule)
                    page.AddUrls(rule, name, attribute.Value, span);
            }
        }
        return page;
    }

    private void ReadScript(IElement element, StartTag? tag, int number)
    {
        if (element.HasAttribute("src"))
        {
            HasScriptFiles = true;
            return;
        }
        var type = (element.GetAttribute("type") ?? "").Trim();
        if (!JavaScriptTypes.Contains(type) || tag is null) return;
        var code = element.TextContent;
        Scripts.Add(new InlineScript(number, code, type.Equals("module", StringComparison.OrdinalIgnoreCase), ContentSpan(Text, tag, code)));
    }

    private static SourceSpan ContentSpan(string text, StartTag tag, string content) =>
        new(tag.End, string.CompareOrdinal(text, tag.End, content, 0, content.Length) == 0);

    // The rule an attribute's URL falls under, and whether what it fetches is code the page runs.
    private static (string Rule, bool Code)? RuleOf(IElement element, string attribute) => (element.LocalName, attribute) switch
    {
        ("script", "src") => ("script-src", true),
        ("link", "href") => LinkRule(element),
        ("form", "action") or ("button" or "input", "formaction") => ("form-action", false),
        ("img", "src" or "srcset") => ("img", false),
        ("input", "src") when string.Equals(element.GetAttribute("type"), "image", StringComparison.OrdinalIgnoreCase) => ("img", false),
        ("video", "src" or "poster") or ("audio", "src") or ("source", "src" or "srcset") or ("track", "src") => ("media", false),
        ("iframe" or "frame", "src") => ("frame", false),
        ("object", "data") or ("embed", "src") => ("object", false),
        _ => null,
    };

    // Only the relations that fetch something. A stylesheet is not code; a module preload, or a preload of a script, is.
    private static (string, bool)? LinkRule(IElement link)
    {
        var rel = (link.GetAttribute("rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var preloadsScript = (link.GetAttribute("as") ?? "").ToLowerInvariant() is "script" or "worker" or "sharedworker";
        if (rel.Contains("modulepreload") || (rel.Intersect(["preload", "prefetch"]).Any() && preloadsScript)) return ("link", true);
        if (rel.Intersect(["stylesheet", "preload", "prefetch", "icon", "apple-touch-icon", "manifest"]).Any()) return ("link", false);
        return null;
    }

    private void AddUrls((string Rule, bool Code) rule, string attribute, string value, SourceSpan span)
    {
        if (attribute != "srcset")
        {
            Urls.Add(new MarkupUrl(rule.Rule, value.Trim(), rule.Code, span.Offset));
            return;
        }
        // A srcset is a comma-separated list of «url [descriptor]»; each URL is fetched on its own.
        var at = 0;
        foreach (var candidate in value.Split(','))
        {
            var url = candidate.Trim().Split(' ', 2)[0];
            if (url.Length > 0)
                Urls.Add(new MarkupUrl(rule.Rule, url, rule.Code, span.Exact ? span.Offset + value.IndexOf(url, at, StringComparison.Ordinal) : span.Offset));
            at += candidate.Length + 1;
        }
    }
}
