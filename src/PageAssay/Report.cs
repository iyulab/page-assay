using System.Globalization;
using System.Text.Json.Nodes;

namespace PageAssay;

/// <summary>Whether a finding makes the page unusable for a person, or is worth repairing without being a reason to refuse it.</summary>
public enum Severity
{
    /// <summary>Worth repairing; not a reason to refuse the page.</summary>
    Notice,

    /// <summary>The page is broken for a person: it does not run, a control does nothing, text cannot be read.</summary>
    Block,
}

/// <summary>When a finding happened: before the page settled, or after.</summary>
public enum Phase
{
    /// <summary>Before the page settled — it may not have started at all.</summary>
    Load,

    /// <summary>After it settled — one thing it does is broken.</summary>
    Use,
}

/// <summary>
/// One thing a run found: a stable <see cref="Kind"/> code, the fields that kind needs, a severity, and one English line made
/// from the fields by a fixed template. The line is a convenience for a repair loop; the contract is the kind and its fields.
/// </summary>
public sealed record Finding(string Kind, Severity Severity, string Text, Phase? Phase, IReadOnlyDictionary<string, JsonNode?> Fields)
{
    /// <summary>The finding in the report's JSON shape: <c>kind</c>, <c>severity</c>, <c>text</c>, <c>phase</c> and the kind's fields, flat.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["kind"] = Kind,
            ["severity"] = Severity == Severity.Block ? "block" : "notice",
            ["text"] = Text,
        };
        if (Phase is { } phase)
            json["phase"] = phase == PageAssay.Phase.Load ? "load" : "use";
        foreach (var (name, value) in Fields)
            json[name] = value?.DeepClone();
        return json;
    }
}

/// <summary>The browser context a run ran in.</summary>
public sealed record RunContext(string Engine, bool Headless, string Origin, int ViewportWidth, int ViewportHeight)
{
    /// <summary>The context in the report's JSON shape.</summary>
    public JsonObject ToJson() => new()
    {
        ["engine"] = Engine,
        ["headless"] = Headless,
        ["origin"] = Origin,
        ["viewports"] = new JsonArray(new JsonObject { ["width"] = ViewportWidth, ["height"] = ViewportHeight, ["scale"] = 1 }),
    };
}

/// <summary>What a run returns.</summary>
public sealed record AssayReport(string Page, bool Loaded, long? SettledMs, IReadOnlyList<Finding> Findings, bool Truncated, RunContext Context)
{
    /// <summary>True when any finding blocks.</summary>
    public bool Blocks => Findings.Any(f => f.Severity == Severity.Block);

    /// <summary>The report in its JSON shape.</summary>
    public JsonObject ToJson() => new()
    {
        ["page"] = Page,
        ["loaded"] = Loaded,
        ["settledMs"] = SettledMs,
        ["findings"] = new JsonArray(Findings.Select(f => (JsonNode)f.ToJson()).ToArray()),
        ["truncated"] = Truncated,
        ["context"] = Context.ToJson(),
    };
}

/// <summary>The kinds this library reports, with their default severity and the template of their one-line text.</summary>
public static class FindingKinds
{
    /// <summary>The page's own code threw and nothing caught it.</summary>
    public const string Uncaught = "uncaught";
    /// <summary>A promise the page made was rejected and nothing handled it.</summary>
    public const string Rejection = "rejection";
    /// <summary>The page reported an error itself with <c>console.error</c>.</summary>
    public const string ConsoleError = "console-error";
    /// <summary>The visible text holds an engine's error sentence the page printed itself.</summary>
    public const string ErrorOnScreen = "error-on-screen";
    /// <summary>Visible text does not stand out enough from what is behind it (WCAG 2.x).</summary>
    public const string TextContrast = "text-contrast";
    /// <summary>A visible field has no name a screen reader can read.</summary>
    public const string UnnamedField = "unnamed-field";
    /// <summary>A file the page asked for from its own origin did not arrive.</summary>
    public const string Resource = "resource";
    /// <summary>A request was refused: another host, which the run does not reach.</summary>
    public const string Blocked = "blocked";
    /// <summary>A count of findings of one kind that the per-kind limit left out.</summary>
    public const string More = "more";

    /// <summary>The default severity of a kind; a caller overrides it per kind in <see cref="AssayOptions.Severities"/>.</summary>
    public static Severity DefaultSeverity(string kind) => kind switch
    {
        ConsoleError or TextContrast or UnnamedField or More => Severity.Notice,
        _ => Severity.Block,
    };

    internal static string TextOf(string kind, IReadOnlyDictionary<string, JsonNode?> f)
    {
        string S(string name) => f.TryGetValue(name, out var v) && v is not null ? v.ToString() : "";
        string At() => f.ContainsKey("line")
            ? string.Create(CultureInfo.InvariantCulture, $" at line {S("line")}, column {S("column")}")
            : "";
        return kind switch
        {
            Uncaught => $"Uncaught error{At()}: {S("message")}",
            Rejection => $"Unhandled promise rejection{At()}: {S("message")}",
            ConsoleError => $"The page logged an error: {S("message")}",
            ErrorOnScreen => $"The page shows an error message in {S("selector")}: {S("text")}",
            Resource => f.ContainsKey("status")
                ? $"The file {S("url")} did not arrive (status {S("status")})."
                : $"The file {S("url")} did not arrive.",
            Blocked => $"A request to {S("host")} was refused: the page must work without other hosts.",
            TextContrast => $"Text in {S("selector")} is hard to read: {S("foreground")} on {S("background")} has contrast {S("ratio")}:1, needs {S("needed")}:1 ({S("count")} element(s), e.g. \"{S("text")}\").",
            UnnamedField => f.ContainsKey("nearText")
                ? $"The field {S("selector")} has no name a screen reader can read (the text beside it: \"{S("nearText")}\")."
                : $"The field {S("selector")} has no name a screen reader can read.",
            More => $"{S("count")} more {S("of")} findings were left out.",
            _ => kind,
        };
    }
}
