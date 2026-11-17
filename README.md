# page-assay
Checks a web page without AI: static rules over its source, drives it over the Chrome DevTools Protocol, and returns a typed report.

The report it returns: [docs/report.md](docs/report.md) (draft).

## State

Work in progress. The driven half runs: launch a headless Chrome, Edge or Chromium (or attach to an endpoint the caller owns), load a
page in a fresh browser context, wait until it settles, and report uncaught errors, unhandled rejections, console errors, files that did
not arrive, requests to other hosts (refused) and engine error text the page printed itself. Static rules and the look checks are next.

```csharp
await using var browser = await Browser.LaunchAsync();
var report = await Assayer.RunAsync(browser, new Uri("http://127.0.0.1:8080/index.html"));
Console.WriteLine(report.ToJson());
```

Tests launch a local browser and are skipped when none is installed (`PAGE_ASSAY_BROWSER` names one explicitly).
