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

/// <summary>Where a finding stands when two versions of a page are compared.</summary>
public enum Comparison
{
    /// <summary>Only in the new version: this change caused it.</summary>
    New,

    /// <summary>In both versions.</summary>
    Kept,

    /// <summary>Only in the previous version: the change fixed it.</summary>
    Gone,
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
    /// <summary>The finding's identity across two versions of a page: built from what survives an edit elsewhere in the file
    /// (the kind and the name, URL, selector or colours it is about), never from a line number.</summary>
    public string Key { get; init; } = FindingKinds.KeyOf(Kind, Fields);

    /// <summary>True when the version compared broke the parse of more scripts than the previous one: the finding may be an
    /// effect of that, not a defect of its own.</summary>
    public bool Deferred { get; init; }

    /// <summary>Set when two versions were compared; null otherwise.</summary>
    public Comparison? Status { get; init; }

    /// <summary>The finding in the report's JSON shape: <c>kind</c>, <c>severity</c>, <c>text</c>, <c>phase</c> and the kind's fields, flat.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["kind"] = Kind,
            ["severity"] = Severity == Severity.Block ? "block" : "notice",
            ["text"] = Text,
        };
        json["key"] = Key;
        if (Status is { } status)
            json["status"] = status switch { Comparison.New => "new", Comparison.Kept => "kept", _ => "gone" };
        if (Deferred)
            json["deferred"] = true;
        if (Phase is { } phase)
            json["phase"] = phase == PageAssay.Phase.Load ? "load" : "use";
        foreach (var (name, value) in Fields)
            json[name] = value?.DeepClone();
        return json;
    }
}

/// <summary>A viewport size in CSS pixels.</summary>
public sealed record Viewport(int Width, int Height)
{
    /// <summary><c>1280x800</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
}

/// <summary>The context a run ran in: which halves ran, and the browser context of the driven half.</summary>
/// <remarks>A static-only run has no browser: <see cref="Engine"/> and <see cref="Origin"/> are null and the lists are empty.</remarks>
public sealed record RunContext(string? Engine, bool Headless, string? Origin, IReadOnlyList<Viewport> Viewports, IReadOnlyList<string> ColorSchemes)
{
    /// <summary>The static rules read the page's source.</summary>
    public bool Static { get; init; }

    /// <summary>The page was loaded in a browser.</summary>
    public bool Driven => Engine is not null;

    /// <summary>The context of a run that only read the source.</summary>
    public static RunContext StaticOnly { get; } = new(null, false, null, [], []) { Static = true };

    /// <summary>The context in the report's JSON shape; the browser's fields only when the page was driven.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["static"] = Static, ["driven"] = Driven };
        if (Driven)
        {
            json["engine"] = Engine;
            json["headless"] = Headless;
            json["origin"] = Origin;
            json["viewports"] = new JsonArray(Viewports.Select(v => (JsonNode)new JsonObject { ["width"] = v.Width, ["height"] = v.Height, ["scale"] = 1 }).ToArray());
            json["colorSchemes"] = new JsonArray(ColorSchemes.Select(s => (JsonNode)s).ToArray());
        }
        json["templates"] = FindingKinds.TemplatesVersion;
        return json;
    }
}

/// <summary>What a run returns. <see cref="Loaded"/> is null when the page was not driven (a static-only run).</summary>
public sealed record AssayReport(string Page, bool? Loaded, long? SettledMs, IReadOnlyList<Finding> Findings, bool Truncated, RunContext Context)
{
    /// <summary>True when any finding blocks.</summary>
    public bool Blocks => Findings.Any(f => f.Severity == Severity.Block);

    /// <summary>The report in its JSON shape (<c>loaded</c> and <c>settledMs</c> only when the page was driven).</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["page"] = Page };
        if (Loaded is { } loaded)
        {
            json["loaded"] = loaded;
            json["settledMs"] = SettledMs;
        }
        json["findings"] = new JsonArray(Findings.Select(f => (JsonNode)f.ToJson()).ToArray());
        json["truncated"] = Truncated;
        json["context"] = Context.ToJson();
        return json;
    }
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
    /// <summary>A script or event-handler attribute does not parse.</summary>
    public const string Syntax = "syntax";
    /// <summary>A name is called but defined nowhere the call can reach.</summary>
    public const string UndefinedCall = "undefined-call";
    /// <summary>Code looks an element up by an id the markup does not have.</summary>
    public const string MissingId = "missing-id";
    /// <summary>Two or more elements in the markup carry the same id.</summary>
    public const string DuplicateId = "duplicate-id";
    /// <summary>Code or markup would reach another host.</summary>
    public const string OutsideReference = "outside-reference";
    /// <summary>The page reads a name while it starts, before the line that gives it a value.</summary>
    public const string ReadBeforeSet = "read-before-set";
    /// <summary>A table whose rows do not have as many cells as its header has columns.</summary>
    public const string TableShape = "table-shape";
    /// <summary>An element marked [hidden] is shown anyway and covers most of the window.</summary>
    public const string Covering = "covering";

    /// <summary>The version of the one-line text templates; it changes with any template's wording (a minor version of the package).</summary>
    public const string TemplatesVersion = "1";

    /// <summary>The page is wider than the viewport: a person has to scroll sideways.</summary>
    public const string Overflow = "overflow";
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
        ConsoleError or TextContrast or UnnamedField or Overflow or TableShape or DuplicateId or More => Severity.Notice,
        _ => Severity.Block,
    };

    // anchor: identity a finding has that is not one of its fields — the script (its content hash) or the handler attribute
    // (its element's id or label, and the event) a static finding is in, the host of an outside reference.
    internal static string KeyOf(string kind, IReadOnlyDictionary<string, JsonNode?> f, string? anchor = null)
    {
        string S(string name) => f.TryGetValue(name, out var v) && v is not null ? v.ToString() : "";
        var subject = kind switch
        {
            Uncaught or Rejection => $"{S("file")}|{S("message")}",
            ConsoleError => S("message"),
            ErrorOnScreen => $"{S("selector")}|{S("text")}",
            Resource => S("url"),
            Blocked => $"{S("category")}|{S("host")}",
            TextContrast => $"{S("foreground")}|{S("background")}|{S("needed")}",
            Overflow => S("viewport"),
            TableShape => f.TryGetValue("columns", out var cols) && cols is not null ? cols.ToJsonString() : S("selector"),
            Covering => S("selector"),
            UnnamedField => f.ContainsKey("nearText") ? "near:" + S("nearText") : S("selector"),
            More => S("of"),
            Syntax => anchor ?? S("source"),
            UndefinedCall => anchor is null ? $"{S("name")}|{S("reason")}" : $"{S("name")}|{S("reason")}|{anchor}",
            MissingId or DuplicateId => S("id"),
            OutsideReference => $"{S("rule")}|{anchor ?? S("url")}",
            ReadBeforeSet => $"{S("call")}|{S("name")}",
            _ => string.Join("|", f.Where(p => p.Key is not ("line" or "column" or "count")).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}")),
        };
        return kind + ":" + subject;
    }

    internal static string TextOf(string kind, IReadOnlyDictionary<string, JsonNode?> f)
    {
        string S(string name) => f.TryGetValue(name, out var v) && v is not null ? v.ToString() : "";
        string Lines() => f.TryGetValue("lines", out var l) && l is JsonArray a ? string.Join(", ", a.Select(n => n?.ToString())) : "";
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
            TextContrast => $"Text in {S("selector")} is hard to read ({S("scheme")}, {S("viewport")}): {S("foreground")} on {S("background")} has contrast {S("ratio")}:1, needs {S("needed")}:1 ({S("count")} element(s), e.g. \"{S("text")}\").",
            TableShape => $"The table {S("selector")} has {S("rows")} row(s) whose cell count ({S("cells")}) does not match its header's columns: values sit under the wrong headers.",
            Covering => $"{S("selector")} is marked hidden but shown ({S("display")}), covering {S("area")} of the window: nothing under it can be used.",
            Overflow => $"At {S("viewport")} ({S("scheme")}) the page is {S("by")} px wider than the window: a person has to scroll sideways.",
            UnnamedField => f.ContainsKey("nearText")
                ? $"The field {S("selector")} has no name a screen reader can read (the text beside it: \"{S("nearText")}\")."
                : $"The field {S("selector")} has no name a screen reader can read.",
            More => $"{S("count")} more {S("of")} findings were left out.",
            Syntax => f.ContainsKey("hides")
                ? $"The {S("source")} does not parse{At()}: {S("message")} ({S("hides")} call(s) to names it would define are not reported apart)."
                : $"The {S("source")} does not parse{At()}: {S("message")}",
            UndefinedCall => S("reason") == "not-global"
                ? $"{S("name")}() is called as a global{At()}, but it exists only inside a function or a module script."
                : $"{S("name")}() is called{At()}, but nothing defines it.",
            MissingId => $"The code looks up the id \"{S("id")}\"{At()}, but no element has it.",
            DuplicateId => $"{S("count")} elements have the id \"{S("id")}\" (lines {Lines()}): a lookup finds only the first.",
            OutsideReference => $"{S("url")}{At()} is on another host ({S("rule")}): the page must work without other hosts.",
            ReadBeforeSet => (f.ContainsKey("call") ? $"While {S("call")}() runs at start, " : "At start, ")
                + $"{S("name")} is read{At()} before line {S("setAt")} gives it a value: "
                + (S("throws") == "true" ? "the read throws." : "it is undefined there."),
            _ => kind,
        };
    }
}
