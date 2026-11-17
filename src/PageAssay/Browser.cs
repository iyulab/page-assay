using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PageAssay;

/// <summary>How to start a browser for a run.</summary>
public sealed record BrowserLaunchOptions
{
    /// <summary>The browser's executable. When null, the environment variable <c>PAGE_ASSAY_BROWSER</c>, then the usual
    /// install places of Chrome, Edge and Chromium are tried.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Run without a window (default true).</summary>
    public bool Headless { get; init; } = true;

    /// <summary>How long the browser may take to open its DevTools endpoint.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// A browser that speaks the Chrome DevTools Protocol: one this library started (and stops), or an endpoint the caller already
/// owns (an embedded web view, a browser started elsewhere), which is never stopped here.
/// </summary>
public sealed partial class Browser : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly string? _profileDirectory;

    /// <summary>The browser-level DevTools WebSocket address.</summary>
    public Uri WebSocketUrl { get; }

    /// <summary>True when this library started the browser without a window.</summary>
    public bool Headless { get; }

    private Browser(Uri webSocketUrl, bool headless, Process? process, string? profileDirectory)
    {
        WebSocketUrl = webSocketUrl;
        Headless = headless;
        _process = process;
        _profileDirectory = profileDirectory;
    }

    /// <summary>Uses a browser the caller already owns, by its browser-level DevTools WebSocket address.</summary>
    public static Browser Attach(Uri webSocketUrl, bool headless = false)
    {
        ArgumentNullException.ThrowIfNull(webSocketUrl);
        return new Browser(webSocketUrl, headless, process: null, profileDirectory: null);
    }

    /// <summary>Finds the browser's executable the way <see cref="LaunchAsync"/> does, or null when none is installed.</summary>
    public static string? FindExecutable(string? preferred = null)
    {
        if (!string.IsNullOrEmpty(preferred))
            return File.Exists(preferred) ? preferred : null;
        var fromEnvironment = Environment.GetEnvironmentVariable("PAGE_ASSAY_BROWSER");
        if (!string.IsNullOrEmpty(fromEnvironment))
            return File.Exists(fromEnvironment) ? fromEnvironment : null;
        return Candidates().FirstOrDefault(File.Exists);
    }

    /// <summary>Starts a browser on a fresh, temporary profile and waits for its DevTools endpoint.</summary>
    /// <exception cref="InvalidOperationException">No browser was found, or it did not open its endpoint in time.</exception>
    public static async Task<Browser> LaunchAsync(BrowserLaunchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new BrowserLaunchOptions();
        var executable = FindExecutable(options.ExecutablePath)
            ?? throw new InvalidOperationException(
                "No Chrome, Edge or Chromium was found. Pass ExecutablePath or set PAGE_ASSAY_BROWSER.");

        var profile = Path.Combine(Path.GetTempPath(), "page-assay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (options.Headless)
            start.ArgumentList.Add("--headless=new");
        foreach (var argument in new[]
                 {
                     "--remote-debugging-port=0", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-background-networking", "--disable-sync", "--disable-component-update",
                     "--mute-audio", "about:blank",
                 })
            start.ArgumentList.Add(argument);

        var process = Process.Start(start) ?? throw new InvalidOperationException($"The browser at {executable} did not start.");
        var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var match = DevToolsLine().Match(e.Data);
            if (match.Success)
                endpoint.TrySetResult(new Uri(match.Groups[1].Value));
        };
        process.OutputDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.StartTimeout);
        try
        {
            var url = await endpoint.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new Browser(url, options.Headless, process, profile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Stop(process, profile);
            throw new InvalidOperationException($"The browser did not open its DevTools endpoint within {options.StartTimeout}.");
        }
        catch
        {
            Stop(process, profile);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_process is not null)
            Stop(_process, _profileDirectory);
        return ValueTask.CompletedTask;
    }

    private static void Stop(Process process, string? profile)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
        }
        if (profile is null) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }

    private static IEnumerable<string> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield break;
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            }
            yield break;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" })
            yield return Path.Combine(directory, name);
    }

    [GeneratedRegex(@"DevTools listening on (ws://\S+)")]
    private static partial Regex DevToolsLine();
}
