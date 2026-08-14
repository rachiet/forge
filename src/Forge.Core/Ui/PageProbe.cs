using System.Text.Json;
using Microsoft.Playwright;

namespace Forge.Core.Ui;

/// <summary>
/// Loads a running application in a headless browser and reports what it rendered. This is the
/// only way to answer questions about an interface — markup does not say whether an element is
/// visible, where it sits, or what colour it resolved to — and it is entirely mechanical, so it
/// costs no tokens and gives the same answer every run.
///
/// A machine with no browser installed is a skip, never a failure: <see cref="CaptureAsync"/>
/// returns null and the caller carries on, the same rule as a workspace with nothing to build.
/// </summary>
public static class PageProbe
{
    /// <summary>The width pages are rendered at: a desktop window, where a three-column layout is expected to fit.</summary>
    public const int ViewportWidth = 1280;

    /// <summary>And its height. Only the width decides layout; the height decides the screenshot.</summary>
    public const int ViewportHeight = 900;

    /// <summary>How long a page is given to load before the probe gives up on it.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Installs Chromium into <paramref name="browsersDir"/> unless it is already there, and
    /// returns whether a browser is available. The download is ~100MB and happens once per
    /// machine; every later call is a directory check.
    /// </summary>
    public static bool EnsureBrowser(string browsersDir)
    {
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersDir);
        if (Directory.Exists(browsersDir) &&
            Directory.EnumerateDirectories(browsersDir, "chromium*").Any()) return true;

        Directory.CreateDirectory(browsersDir);
        try
        {
            // Playwright's own installer, called in-process: no shell, no npm, no global tool.
            return Program.Main(["install", "chromium"]) == 0;
        }
        catch (Exception)
        {
            // No network, no permission, no supported platform: the caller skips its check.
            return false;
        }
    }

    /// <summary>
    /// Renders each path of a running application and reports it. Null when no browser could be
    /// installed. A path that will not load still yields a snapshot, carrying the console errors
    /// and failed requests that explain why.
    /// </summary>
    /// <param name="baseUrl">Where the application is listening, e.g. http://127.0.0.1:5001.</param>
    /// <param name="paths">Paths to visit, e.g. ["/"].</param>
    /// <param name="browsersDir">Where Chromium is installed.</param>
    /// <param name="screenshotDir">Where to write one PNG per path, or null for no screenshots.</param>
    public static async Task<IReadOnlyList<PageSnapshot>?> CaptureAsync(
        string baseUrl,
        IReadOnlyList<string> paths,
        string browsersDir,
        string? screenshotDir = null,
        CancellationToken ct = default)
    {
        if (!EnsureBrowser(browsersDir)) return null;

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium
            .LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        }).ConfigureAwait(false);

        List<PageSnapshot> snapshots = [];
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            snapshots.Add(await CapturePageAsync(context, baseUrl, path, screenshotDir).ConfigureAwait(false));
        }
        return snapshots;
    }

    /// <summary>Renders one path and reads back everything the health rules and QA need.</summary>
    private static async Task<PageSnapshot> CapturePageAsync(
        IBrowserContext context, string baseUrl, string path, string? screenshotDir)
    {
        var page = await context.NewPageAsync().ConfigureAwait(false);
        List<string> errors = [], failures = [];

        page.Console += (_, message) =>
        {
            if (message.Type == "error") errors.Add(Trim(message.Text));
        };
        page.PageError += (_, error) => errors.Add(Trim(error));
        page.RequestFailed += (_, request) => failures.Add($"failed {request.Url}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400) failures.Add($"{response.Status} {response.Url}");
        };

        var url = baseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
        int? status = null;
        try
        {
            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = (float)LoadTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            status = response?.Status;
        }
        catch (PlaywrightException ex)
        {
            errors.Add($"the page did not finish loading: {Trim(ex.Message)}");
        }

        string? shot = null;
        if (screenshotDir is { Length: > 0 })
        {
            Directory.CreateDirectory(screenshotDir);
            shot = Path.Combine(screenshotDir, $"{Slug(path)}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, FullPage = true })
                .ConfigureAwait(false);
        }

        // Read as JSON and parsed here: Playwright's own converter cannot construct a record
        // with no parameterless constructor, which every type in this file is.
        var json = await page.EvaluateAsync<JsonElement>(MeasureScript).ConfigureAwait(false);
        var measured = JsonSerializer.Deserialize<PageMeasurement>(json, MeasurementJson)!;
        await page.CloseAsync().ConfigureAwait(false);

        return new PageSnapshot(
            path, measured.Title, ViewportWidth, measured.ScrollWidth,
            measured.Elements.Select(e => new PageElement(
                e.TestId, e.Tag, e.Role, e.Text, e.Classes,
                new PageBox(e.X, e.Y, e.Width, e.Height),
                e.Visible, e.MarkedHidden, e.Background, e.Ink)).ToList(),
            errors, failures, shot, status);
    }

    /// <summary>
    /// Reads the rendered page in the browser: every element that carries a handle, a role or
    /// text, with its box and computed colours. Runs in the page, so `visible` means what the
    /// browser decided after the whole cascade — including an element the `hidden` attribute
    /// asked to hide and a class then displayed anyway.
    /// </summary>
    private const string MeasureScript = """
        () => {
          const seen = [];
          const all = document.querySelectorAll('body *');
          for (const el of all) {
            const style = getComputedStyle(el);
            const box = el.getBoundingClientRect();
            const text = (el.childElementCount === 0 ? el.textContent : '').trim();
            const testId = el.getAttribute('data-testid') || '';
            const role = el.getAttribute('role') || '';
            if (!testId && !role && !text && box.width === 0) continue;
            seen.push({
              testId, role, text: text.slice(0, 120),
              tag: el.tagName.toLowerCase(),
              classes: el.getAttribute('class') || '',
              x: box.x + window.scrollX, y: box.y + window.scrollY,
              width: box.width, height: box.height,
              visible: style.display !== 'none' && style.visibility !== 'hidden'
                       && style.opacity !== '0' && box.width > 0 && box.height > 0,
              markedHidden: el.hasAttribute('hidden') || el.getAttribute('aria-hidden') === 'true',
              background: style.backgroundColor,
              ink: style.color,
            });
          }
          return {
            title: document.title,
            scrollWidth: document.documentElement.scrollWidth,
            elements: seen,
          };
        }
        """;

    /// <summary>How the page script's result is read: its keys are JavaScript's camelCase.</summary>
    private static readonly JsonSerializerOptions MeasurementJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The shape <see cref="MeasureScript"/> returns, parsed from the page's JSON.</summary>
    private sealed record PageMeasurement(string Title, int ScrollWidth, List<MeasuredElement> Elements);

    /// <summary>One element as the page script reports it, before it becomes a PageElement.</summary>
    private sealed record MeasuredElement(
        string TestId, string Tag, string Role, string Text, string Classes,
        double X, double Y, double Width, double Height,
        bool Visible, bool MarkedHidden, string Background, string Ink);

    /// <summary>A file-name-safe form of a path: `/` becomes `home`, `/cards/new` becomes `cards-new`.</summary>
    private static string Slug(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return "home";
        return string.Concat(trimmed.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
    }

    /// <summary>One line, short enough for a report.</summary>
    private static string Trim(string text) =>
        text.ReplaceLineEndings(" ").Trim() is { Length: > 200 } long_ ? long_[..200] + "…" : text.ReplaceLineEndings(" ").Trim();
}
