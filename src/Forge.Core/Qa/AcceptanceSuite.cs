using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Agents;
using Forge.Core.Tools;

namespace Forge.Core.Qa;

/// <summary>One acceptance run.</summary>
/// <param name="Passed">Whether the suite's process exited zero.</param>
/// <param name="Ran">Whether it ran at all. False when there is no suite or no app to start.</param>
/// <param name="Output">The test output, or why nothing ran.</param>
public sealed record AcceptanceResult(bool Passed, bool Ran, string Output)
{
    /// <summary>A result for a suite that never executed. Not a pass.</summary>
    public static AcceptanceResult NotRun(string why) => new(false, false, why);
}

/// <summary>
/// Runs the project's acceptance suite — black-box tests QA writes against the OpenAPI
/// contract, living in the client repo under <see cref="Directory"/>. The harness starts the
/// application, runs the suite against it over HTTP, and the exit code is the verdict on the
/// finished project.
///
/// The suite is kept out of the solution file, so the engineers' CI never runs it, and reaches
/// the application only at <see cref="BaseUrlVariable"/>, so it cannot reference its assemblies.
/// </summary>
public static partial class AcceptanceSuite
{
    /// <summary>Repo-relative directory holding the suite.</summary>
    public const string Directory = "tests/acceptance";

    /// <summary>The environment variable carrying the running app's base URL.</summary>
    public const string BaseUrlVariable = "FORGE_BASE_URL";

    /// <summary>The xUnit trait naming the contract operation a test exercises.</summary>
    public const string OperationTrait = "operation";

    /// <summary>Where Playwright looks for its browsers; the harness installs them once per machine.</summary>
    public const string BrowsersVariable = "PLAYWRIGHT_BROWSERS_PATH";

    /// <summary>The Playwright version the suite is scaffolded with, pinned so a run is repeatable.</summary>
    private const string PlaywrightVersion = "1.62.0";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The contract operations the suite claims to cover, read from the `[Trait("operation",
    /// …)]` attributes in its source without building or running anything.
    /// </summary>
    public static IReadOnlyCollection<string> DeclaredOperations(string workspaceDir)
    {
        var dir = Path.Combine(workspaceDir, Directory.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.Directory.Exists(dir)) return [];

        return System.IO.Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => OperationTraitPattern().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups[1].Value.Trim())
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The suite's project file, whose name the harness fixes rather than QA choosing it.</summary>
    public const string ProjectFile = "tests/acceptance/AcceptanceTests.csproj";

    /// <summary>
    /// Deletes every project file in the suite directory other than <see cref="ProjectFile"/>,
    /// which the harness owns. A second .csproj beside it makes any directory-scoped dotnet
    /// command ambiguous, and only the fixed one is ever built.
    /// </summary>
    public static void PruneStrayProjects(string workspaceDir)
    {
        var dir = Path.Combine(workspaceDir, Directory.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.Directory.Exists(dir)) return;

        var keep = Path.GetFullPath(
            Path.Combine(workspaceDir, ProjectFile.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var project in System.IO.Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            if (!string.Equals(Path.GetFullPath(project), keep, StringComparison.Ordinal))
                File.Delete(project);
    }

    /// <summary>
    /// Creates the suite's project if it does not exist yet, with `dotnet new xunit` so its
    /// package versions come from the installed SDK rather than from a model's memory. QA then
    /// writes only test files. Returns null on success, or why the scaffold could not be built.
    /// </summary>
    public static string? EnsureScaffold(string workspaceDir, string? agentHome = null)
    {
        var dir = Path.Combine(workspaceDir, Directory.Replace('/', Path.DirectorySeparatorChar));
        PruneStrayProjects(workspaceDir);
        if (File.Exists(Path.Combine(workspaceDir, ProjectFile.Replace('/', Path.DirectorySeparatorChar))))
            return null;

        // Tests already written are kept and scaffolded around; a directory holding none is
        // leftover state and is cleared so the template lands in a clean folder.
        if (System.IO.Directory.Exists(dir)
            && !System.IO.Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Any())
            System.IO.Directory.Delete(dir, recursive: true);

        var created = Dotnet(workspaceDir, baseUrl: "", agentHome,
            "new", "xunit", "-o", Directory, "-n", "AcceptanceTests");
        if (!created.Passed) return "could not scaffold the acceptance suite:\n" + created.Output;

        // The template's placeholder test would be the only one covering nothing.
        var placeholder = Path.Combine(dir, "UnitTest1.cs");
        if (File.Exists(placeholder)) File.Delete(placeholder);

        File.WriteAllText(Path.Combine(dir, "Api.cs"), $$"""
            using System;
            using System.Net.Http;

            namespace AcceptanceTests;

            /// <summary>The running application, reached over HTTP at the address the harness started it on.</summary>
            public static class Api
            {
                /// <summary>The base URL of the instance under test.</summary>
                public static string BaseUrl =>
                    Environment.GetEnvironmentVariable("{{BaseUrlVariable}}")
                    ?? throw new InvalidOperationException(
                        "{{BaseUrlVariable}} is not set. The harness starts the application and sets it; "
                        + "run the suite through the harness rather than by hand.");

                /// <summary>A client pointed at that instance. Redirects are not followed, so a 302 is visible.</summary>
                public static HttpClient Client() =>
                    new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl) };
            }

            """);

        var addedBrowser = Dotnet(workspaceDir, baseUrl: "", agentHome,
            "add", ProjectFile, "package", "Microsoft.Playwright", "--version", PlaywrightVersion);
        if (!addedBrowser.Passed)
            return "could not add the browser package to the acceptance suite:\n" + addedBrowser.Output;

        File.WriteAllText(Path.Combine(dir, "Browser.cs"), BrowserHelper);
        return null;
    }

    /// <summary>
    /// The helper QA writes page tests against: it opens the running application in a headless
    /// browser and hands back what the browser computed — where things are, what colour they
    /// came out, whether they are visible at all. Written by the harness rather than by QA, so
    /// every project measures an interface the same way and a fix here improves them all.
    /// </summary>
    private static readonly string BrowserHelper = $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.Playwright;

        namespace AcceptanceTests;

        /// <summary>A page opened in a real browser, and the measurements a test can assert on.</summary>
        public sealed class Browser : IAsyncDisposable
        {
            private IPlaywright? _playwright;
            private IBrowser? _browser;

            /// <summary>The page under test, once <see cref="OpenAsync"/> has loaded it.</summary>
            public IPage Page { get; private set; } = null!;

            /// <summary>
            /// Opens a path of the running application at a desktop width. Use `data-testid`
            /// selectors: they are the handles the interface was built with.
            /// </summary>
            public static async Task<Browser> OpenAsync(string path = "/")
            {
                var browser = new Browser();
                browser._playwright = await Playwright.CreateAsync();
                browser._browser = await browser._playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions { Headless = true });
                var context = await browser._browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = {{PageWidth}}, Height = {{PageHeight}} },
                });
                browser.Page = await context.NewPageAsync();
                await browser.Page.GotoAsync(
                    Api.BaseUrl.TrimEnd('/') + (path.StartsWith("/") ? path : "/" + path),
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                return browser;
            }

            /// <summary>An element by its test id.</summary>
            public ILocator ByTestId(string testId) => Page.Locator($"[data-testid='{testId}']");

            /// <summary>Every element carrying a test id that starts with this prefix, in page order.</summary>
            public ILocator AllByTestIdPrefix(string prefix) => Page.Locator($"[data-testid^='{prefix}']");

            /// <summary>Where an element rendered. Throws if it is not visible, which is itself the answer.</summary>
            public async Task<Rect> BoxAsync(ILocator locator)
            {
                var box = await locator.BoundingBoxAsync()
                    ?? throw new InvalidOperationException("The element is not visible, so it has no position.");
                return new Rect(box.X, box.Y, box.Width, box.Height);
            }

            /// <summary>A computed CSS property of an element, e.g. `background-color` or `display`.</summary>
            public Task<string> StyleAsync(ILocator locator, string property) =>
                locator.EvaluateAsync<string>($"e => getComputedStyle(e).getPropertyValue('{property}')");

            /// <summary>
            /// Whether these elements are laid out in a row: same top edge, left to right. This is
            /// how "side by side" is asserted — the markup cannot tell you.
            /// </summary>
            public async Task<bool> AreSideBySideAsync(params ILocator[] locators)
            {
                var boxes = new List<Rect>();
                foreach (var locator in locators) boxes.Add(await BoxAsync(locator));
                for (var i = 1; i < boxes.Count; i++)
                {
                    if (Math.Abs(boxes[i].Y - boxes[0].Y) > 4) return false;
                    if (boxes[i].X <= boxes[i - 1].X) return false;
                }
                return true;
            }

            /// <summary>
            /// Whether two computed colours are visibly different, on a 0..1 scale of mean channel
            /// difference. 0.02 is about the smallest difference a person notices on a large area.
            /// </summary>
            public static bool AreDifferentColours(string a, string b, double minimum = 0.02)
            {
                var (x, y) = (Channels(a), Channels(b));
                if (x is null || y is null) return false;
                var mean = (Math.Abs(x[0] - y[0]) + Math.Abs(x[1] - y[1]) + Math.Abs(x[2] - y[2])) / 3.0;
                return mean >= minimum;
            }

            /// <summary>Saves a PNG of the page, for a failure worth looking at.</summary>
            public Task ScreenshotAsync(string path) =>
                Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });

            /// <summary>An element's position and size, in CSS pixels.</summary>
            public sealed record Rect(double X, double Y, double Width, double Height);

            private static double[]? Channels(string colour)
            {
                var numbers = System.Text.RegularExpressions.Regex
                    .Matches(colour ?? "", @"[\d.]+")
                    .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
                    .ToArray();
                return numbers.Length >= 3 ? [numbers[0] / 255.0, numbers[1] / 255.0, numbers[2] / 255.0] : null;
            }

            public async ValueTask DisposeAsync()
            {
                if (_browser is not null) await _browser.CloseAsync();
                _playwright?.Dispose();
            }
        }

        """;

    /// <summary>The width page tests render at, matching the harness's own probe.</summary>
    private const int PageWidth = Ui.PageProbe.ViewportWidth;

    /// <summary>And the height.</summary>
    private const int PageHeight = Ui.PageProbe.ViewportHeight;

    /// <summary>
    /// Compiles the suite, separately from running it. A suite that does not build has tested
    /// nothing, and must never be reported as a suite that failed: a failed run becomes a bug
    /// against the product, and rejecting that bug completes the project.
    /// </summary>
    public static AcceptanceResult Build(string workspaceDir, string? agentHome = null) =>
        Dotnet(workspaceDir, baseUrl: "", agentHome, "build", ProjectFile, "--nologo");

    /// <summary>Whether this repo has an acceptance suite directory.</summary>
    public static bool Exists(string workspaceDir) =>
        System.IO.Directory.Exists(
            Path.Combine(workspaceDir, Directory.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Starts the application on a free port, runs the suite against it, and stops it again.
    /// Returns a not-run result when there is no suite, no runnable app, or the app never
    /// starts listening.
    /// </summary>
    /// <param name="alreadyBuilt">
    /// True when the caller has just compiled the suite, so it is not built twice.
    /// </param>
    /// <param name="agentHome">
    /// HOME for the suite and the application it tests; see <see cref="ChildProcess.Create"/>.
    /// </param>
    public static AcceptanceResult Run(
        string workspaceDir, bool alreadyBuilt = false, string? agentHome = null)
    {
        if (!Exists(workspaceDir)) return AcceptanceResult.NotRun("no acceptance suite in this repo");
        if (AgentToolset.Discover(workspaceDir) is not { } target)
            return AcceptanceResult.NotRun("no runnable application to test");

        if (!alreadyBuilt && Build(workspaceDir, agentHome) is { Passed: false } build)
            return AcceptanceResult.NotRun(
                "the acceptance suite does not compile, so no test ran:\n" + build.Output);

        using var app = AppHost.Start(workspaceDir, target.ProjectPath, agentHome);
        if (app is null)
            return AcceptanceResult.NotRun("the application would not start, so the suite was not run");

        // The address is read from the app, never assumed: it is told to bind any free port
        // and prints the one it got.
        if (app.WaitForListeningUrl(AppHost.StartupTimeout) is not { } baseUrl)
            return AcceptanceResult.NotRun(
                $"the application did not report a listening address within "
                + $"{AppHost.StartupTimeout.TotalSeconds:0}s, so the suite was not run:\n{app.Output}");

        var result = Dotnet(workspaceDir, baseUrl, agentHome, "test", ProjectFile, "--nologo", "--no-build");
        app.Stop();
        return result;
    }

    /// <summary>
    /// Runs one dotnet command with the base URL in the environment and captures its output.
    /// The browser location travels with it, so page tests find the Chromium the harness
    /// installed rather than trying to download their own inside a workspace.
    /// A command that outlives the timeout is killed with its process tree and reported as failed.
    /// </summary>
    private static AcceptanceResult Dotnet(
        string dir, string baseUrl, string? agentHome, params string[] args)
    {
        // The suite is code QA wrote, so it starts through the shared factory with Forge's keys
        // scrubbed out; the two variables it does need are set on top of that.
        var extra = new Dictionary<string, string>
        {
            // The address the harness started the application on; the suite reads it from here.
            [BaseUrlVariable] = baseUrl,
        };
        // Where the harness installed Chromium, so a page test does not download its own.
        if (Environment.GetEnvironmentVariable(BrowsersVariable) is { Length: > 0 } browsers)
            extra[BrowsersVariable] = browsers;

        var psi = ChildProcess.Create("dotnet", dir, agentHome, extra);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet — is the .NET SDK on PATH?");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TestTimeout))
        {
            process.Kill(entireProcessTree: true);
            return new AcceptanceResult(false, true,
                $"the acceptance suite timed out after {TestTimeout.TotalMinutes:0} minutes.");
        }

        var output = new StringBuilder(stdout.GetAwaiter().GetResult());
        var err = stderr.GetAwaiter().GetResult();
        if (err.Length > 0) output.Append('\n').Append(err);
        return new AcceptanceResult(process.ExitCode == 0, true, output.ToString().Trim());
    }

    /// <summary>Matches `[Trait("operation", "<id>")]`, capturing the operationId.</summary>
    [GeneratedRegex($"""Trait\s*\(\s*"{OperationTrait}"\s*,\s*"([^"]+)"\s*\)""")]
    private static partial Regex OperationTraitPattern();
}
