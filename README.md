# page-assay
Checks a web page without AI: static rules over its source, drives it over the Chrome DevTools Protocol, and returns a typed report.

The report it returns: [docs/report.md](docs/report.md).

## State

Work in progress. The driven half runs: launch a headless Chrome, Edge or Chromium (or attach to an endpoint the caller owns), load a
page in a fresh browser context, wait until it settles, and report uncaught errors, unhandled rejections, console errors, files that did
not arrive, requests to other hosts (refused), engine error text the page printed itself, text that does not stand out from what is behind it (WCAG contrast, composited colours, placeholders included) fields with no name, and a layout wider than the window — in every viewport (a desktop and a phone width by default) and colour scheme (light and dark). The static rules read the page's source without running it: scripts and handler attributes that do not parse, names called but defined nowhere, ids looked up but absent, ids used twice, names read while the page starts before they have a value, and code, files or styles from another host. A driven run applies them to the document it loaded; a caller that only needs them asks for them alone, with no browser. Two versions of a page can be compared, in either half: every finding has a key that survives edits elsewhere in the file, and is marked new, kept or gone. Interaction steps are next.

```csharp
await using var browser = await Browser.LaunchAsync();
var report = await Assayer.RunAsync(browser, new Uri("http://127.0.0.1:8080/index.html"));
Console.WriteLine(report.ToJson());

// The static rules alone: no browser, nothing run.
var source = Assayer.RunStatic("index.html", File.ReadAllText("index.html"));
```

Tests launch a local browser and are skipped when none is installed (`PAGE_ASSAY_BROWSER` names one explicitly).
