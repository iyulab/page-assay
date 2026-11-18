using System.Text;
using AngleSharp.Dom;

namespace PageAssay.Static;

/// <summary>Names an element in the markup the way the driven half's look checks do, and gives it an identity that survives edits.</summary>
internal static class Selectors
{
    /// <summary>The nearest id, else up to four tag steps with <c>:nth-of-type</c> where a tag repeats among its siblings —
    /// the same shape the look checks report, so a static and a driven finding about one element name it alike.</summary>
    public static string Of(IElement element)
    {
        var parts = new List<string>();
        for (var e = element; e is not null && parts.Count < 4; e = e.ParentElement)
        {
            if (e.Id is { Length: > 0 } id)
            {
                parts.Insert(0, "#" + Escape(id));
                break;
            }
            var tag = e.LocalName;
            var same = e.ParentElement?.Children.Where(c => c.LocalName == tag).ToList() ?? [];
            parts.Insert(0, same.Count > 1 ? $"{tag}:nth-of-type({same.IndexOf(e) + 1})" : tag);
        }
        return string.Join(" > ", parts);
    }

    /// <summary>What identifies an element across two versions of a page: its id, else its label (<c>aria-label</c>,
    /// <c>title</c> or its text), else — only when it has neither — its selector, which an element inserted before it shifts.</summary>
    public static string AnchorOf(IElement element)
    {
        if (element.Id is { Length: > 0 } id) return "#" + Escape(id);
        var label = element.GetAttribute("aria-label") ?? element.GetAttribute("title") ?? Collapse(element.TextContent);
        if (string.IsNullOrWhiteSpace(label) && element.LocalName == "input") label = element.GetAttribute("value");
        return string.IsNullOrWhiteSpace(label) ? Of(element) : $"{element.LocalName} \"{Truncate(label.Trim(), 40)}\"";
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    // CSS.escape, for the characters an id may hold.
    private static string Escape(string id)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c > 0x7F)
            {
                if (char.IsAsciiDigit(c) && (i == 0 || (i == 1 && id[0] == '-')))
                    sb.Append('\\').Append(((int)c).ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                else
                    sb.Append(c);
            }
            else
            {
                sb.Append('\\').Append(c);
            }
        }
        return sb.ToString();
    }
}
