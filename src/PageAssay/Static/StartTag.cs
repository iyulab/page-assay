using System.Text;
using AngleSharp.Dom;

namespace PageAssay.Static;

/// <summary>
/// An element's start tag as written in the file: where each attribute's value starts, and where the tag ends. The HTML parser
/// keeps the element's position but not its attributes', so the tag is scanned again from there.
/// </summary>
internal sealed class StartTag
{
    private readonly Dictionary<string, (int Offset, string Raw)> _values = new(StringComparer.Ordinal);

    private StartTag(int offset) => Offset = offset;

    /// <summary>The offset of the tag's <c>&lt;</c>.</summary>
    public int Offset { get; }

    /// <summary>The offset just past the tag's <c>&gt;</c>: where the element's content starts.</summary>
    public int End { get; private set; }

    /// <summary>The start tag of <paramref name="element"/>, or null when the parser made the element up (an implied
    /// <c>&lt;body&gt;</c>) or the position it kept does not point at a tag.</summary>
    public static StartTag? At(string text, IElement element)
    {
        var position = element.SourceReference?.Position.Position ?? 0;
        var offset = position - 1;
        if (offset < 0 || offset >= text.Length || text[offset] != '<') return null;
        var tag = new StartTag(offset);
        tag.Scan(text);
        return tag;
    }

    /// <summary>Where <paramref name="attribute"/>'s value is in the file; exact when the parser's (decoded) value is the text as written.</summary>
    public SourceSpan SpanOf(string attribute, string decoded) =>
        _values.TryGetValue(attribute, out var value)
            ? new SourceSpan(value.Offset, string.Equals(value.Raw, decoded, StringComparison.Ordinal))
            : new SourceSpan(Offset, false);

    private void Scan(string text)
    {
        var i = Offset + 1;
        while (i < text.Length && !IsSpace(text[i]) && text[i] is not ('>' or '/')) i++;
        while (i < text.Length)
        {
            while (i < text.Length && (IsSpace(text[i]) || text[i] == '/')) i++;
            if (i >= text.Length || text[i] == '>') break;
            var name = new StringBuilder();
            while (i < text.Length && !IsSpace(text[i]) && text[i] is not ('>' or '=') && !(text[i] == '/' && name.Length > 0))
                name.Append(char.ToLowerInvariant(text[i++]));
            while (i < text.Length && IsSpace(text[i])) i++;
            int valueStart;
            string raw;
            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && IsSpace(text[i])) i++;
                if (i < text.Length && text[i] is '"' or '\'')
                {
                    var quote = text[i++];
                    valueStart = i;
                    while (i < text.Length && text[i] != quote) i++;
                    raw = text[valueStart..i];
                    i++;
                }
                else
                {
                    valueStart = i;
                    while (i < text.Length && !IsSpace(text[i]) && text[i] != '>') i++;
                    raw = text[valueStart..i];
                }
            }
            else
            {
                valueStart = i;
                raw = "";
            }
            _values.TryAdd(name.ToString(), (valueStart, raw)); // a repeated attribute: the first one is the element's
        }
        End = Math.Min(i + 1, text.Length);
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\f';
}
