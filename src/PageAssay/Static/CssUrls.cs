namespace PageAssay.Static;

/// <summary>The URLs a piece of CSS asks for: <c>url(…)</c> references and <c>@import</c> rules, comments and strings skipped.
/// A scanner rather than a CSS parser — only the two forms that fetch are needed.</summary>
internal static class CssUrls
{
    /// <summary>Each reference with its rule (<c>css-url</c> or <c>css-import</c>), the URL, and its offset in <paramref name="css"/>.</summary>
    public static IEnumerable<(string Rule, string Url, int Offset)> Scan(string css)
    {
        var i = 0;
        while (i < css.Length)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
            }
            else if (c is '"' or '\'')
            {
                i = SkipString(css, i);
            }
            else if (c == '@' && string.Compare(css, i, "@import", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var j = i + 7;
                while (j < css.Length && char.IsWhiteSpace(css[j])) j++;
                if (j < css.Length && css[j] is '"' or '\'')
                {
                    var end = SkipString(css, j);
                    yield return ("css-import", css[(j + 1)..Math.Max(j + 1, end - 1)], j + 1);
                    i = end;
                }
                else if (UrlAt(css, j) is { } url)
                {
                    yield return ("css-import", url.Url, url.Offset);
                    i = url.End;
                }
                else
                {
                    i = j;
                }
            }
            else if (UrlAt(css, i) is { } url && (i == 0 || !IsNameChar(css[i - 1])))
            {
                yield return ("css-url", url.Url, url.Offset);
                i = url.End;
            }
            else
            {
                i++;
            }
        }
    }

    private static (string Url, int Offset, int End)? UrlAt(string css, int i)
    {
        if (string.Compare(css, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) != 0) return null;
        var j = i + 4;
        while (j < css.Length && char.IsWhiteSpace(css[j])) j++;
        if (j < css.Length && css[j] is '"' or '\'')
        {
            var end = SkipString(css, j);
            var close = css.IndexOf(')', Math.Min(end, css.Length));
            return (css[(j + 1)..Math.Max(j + 1, end - 1)], j + 1, close < 0 ? css.Length : close + 1);
        }
        var stop = css.IndexOf(')', j);
        if (stop < 0) stop = css.Length;
        return (css[j..stop].Trim(), j, Math.Min(stop + 1, css.Length));
    }

    // The index just past the string's closing quote.
    private static int SkipString(string css, int start)
    {
        var quote = css[start];
        var i = start + 1;
        while (i < css.Length && css[i] != quote && css[i] != '\n')
            i += css[i] == '\\' ? 2 : 1;
        return Math.Min(i + 1, css.Length);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}
