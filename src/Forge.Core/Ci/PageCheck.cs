using Forge.Core.Agents;
using Forge.Core.Qa;
using Forge.Core.Ui;

namespace Forge.Core.Ci;

/// <summary>
/// The CI step that opens the application in a browser. Starts the app the task just built,
/// renders its pages, and applies the generic health rules — so a page that does not load, spews
/// console errors, shows what it meant to hide or does not fit the window comes back to the
/// engineer as feedback rather than reaching trunk.
///
/// It runs only when the task actually touched the interface, and only when a browser is
/// available: everything else is a skip, never a failure.
/// </summary>
public static class PageCheck
{
    /// <summary>The path every application has, used when the contract declares no pages.</summary>
    private static readonly string[] DefaultPaths = ["/"];

    /// <summary>
    /// Runs the check over a workspace and returns what is wrong, or null when the page is
    /// healthy, the task changed nothing visible, or no browser could be installed.
    /// </summary>
    /// <param name="workspaceDir">The task's workspace, already built.</param>
    /// <param name="browsersDir">Where Chromium lives; shared across the machine.</param>
    /// <param name="changedFiles">
    /// The files this task changed. Empty means "run it anyway" — a caller with no diff to hand.
    /// </param>
    /// <param name="requirement">
    /// The requirement file this task is building, e.g. `01-clients.md`. A declared page carries
    /// the requirement it serves, so this is what says which pages are this task's to produce:
    /// a page it owns and did not build fails, and a page another task owns is left alone until
    /// that task runs. Null checks every page the application actually serves.
    /// </param>
    public static string? Check(
        string workspaceDir, string browsersDir,
        IReadOnlyList<string>? changedFiles = null, string? requirement = null)
    {
        if (changedFiles is { Count: > 0 } && !TouchesInterface(changedFiles)) return null;
        if (AgentToolset.Discover(workspaceDir) is not { } target) return null;

        using var app = AppHost.Start(workspaceDir, target.ProjectPath);
        if (app?.WaitForListeningUrl(AppHost.StartupTimeout) is not { } baseUrl)
            return "The application did not start, so its pages could not be checked. "
                 + "It must run with `dotnet run` and serve its pages before it can be reviewed.\n"
                 + (app?.Output ?? "");

        // The contract says which pages exist; a project that declares none still gets its root
        // rendered, since an app with an interface always has one.
        var declared = Design.ApiContract.Load(workspaceDir)?.Interface;
        var paths = declared?.Pages.Select(p => p.Path).ToArray() is { Length: > 0 } fromContract
            ? fromContract
            : DefaultPaths;

        try
        {
            var pages = PageProbe
                .CaptureAsync(baseUrl, paths, browsersDir, ScreenshotDir(workspaceDir))
                .GetAwaiter().GetResult();
            if (pages is null) return null;   // no browser on this machine: nothing to report

            // A page nothing serves yet belongs to a task that has not run. Judging it here
            // fails whichever task happens to be in flight for work it was never given.
            var missing = pages.Where(p => p.NotFound).ToList();
            var rendered = pages.Where(p => !p.NotFound).ToList();

            var problems = PageHealth.Problems(rendered)
                .Concat(MissingHandles(declared, rendered))
                .Concat(Unbuilt(declared, missing, requirement))
                .ToList();
            return problems.Count == 0
                ? null
                : "The page was opened in a browser and these are what it showed:\n\n"
                  + string.Join("\n", problems.Select(p => $"- {p}"));
        }
        finally
        {
            app.Stop();
        }
    }

    /// <summary>
    /// Declared pages this task was meant to build and did not. A page is this task's when the
    /// contract says it serves the requirement the task names; every other unserved page is
    /// another task's still-unwritten work and is passed over silently. With no requirement to
    /// go on, nothing is owed — the acceptance run at the end of the board is what finally
    /// insists every declared page exists.
    /// </summary>
    private static IEnumerable<string> Unbuilt(
        Design.InterfaceContract? declared, IReadOnlyList<PageSnapshot> missing, string? requirement)
    {
        if (declared is null || requirement is not { Length: > 0 }) yield break;

        foreach (var page in declared.Pages)
        {
            if (!page.Requirement.Equals(requirement, StringComparison.OrdinalIgnoreCase)) continue;
            if (missing.All(m => m.Path != page.Path)) continue;

            yield return $"{page.Path}: nothing is served at this path, but the contract declares "
                       + $"it as the page for {page.Requirement}, which is this task's requirement. "
                       + "Build the page, or move the declaration to the task that will.";
        }
    }

    /// <summary>
    /// Handles the contract declares that the rendered page does not carry. A set comparison,
    /// the same shape as the coverage gates: the contract is what QA will address the interface
    /// by, so a missing `data-testid` fails the task that owed it rather than surfacing later as
    /// a bug against a page nobody can test.
    /// </summary>
    internal static IEnumerable<string> MissingHandles(
        Design.InterfaceContract? declared, IReadOnlyList<PageSnapshot> rendered)
    {
        if (declared is null) yield break;

        foreach (var page in declared.Pages)
        {
            if (rendered.FirstOrDefault(r => r.Path == page.Path) is not { } shown) continue;

            var present = shown.Elements
                .Where(e => e.TestId.Length > 0)
                .Select(e => e.TestId)
                .ToHashSet(StringComparer.Ordinal);

            // A repeating handle is one per item, and CI renders the page against an empty
            // database — no rows is the correct state there, so its absence proves nothing.
            // The acceptance suite creates its own data and is where those are verified.
            foreach (var element in page.Elements.Where(e => !e.Repeats && !present.Contains(e.TestId)))
                yield return $"{page.Path}: the contract declares `{element.TestId}` ({element.Is}) "
                           + $"but no element on the page carries `data-testid=\"{element.TestId}\"`. "
                           + "Add it, or change the contract if the element is gone.";
        }
    }

    /// <summary>
    /// The files this task's branch changed against trunk, read from git. Empty when git cannot
    /// answer — a workspace with no history yet — which makes the check run rather than skip.
    /// </summary>
    public static IReadOnlyList<string> ChangedFiles(string workspaceDir)
    {
        var diff = Workspaces.Git.Run(workspaceDir, "diff", "--name-only",
            $"origin/{Workspaces.WorkspaceManager.TrunkBranch}...HEAD");
        return diff.ExitCode != 0
            ? []
            : diff.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Whether a change could alter what the page renders: its markup, styling, scripts, or the
    /// static files it serves. A change to storage or a domain class cannot, so it pays nothing.
    /// The UI kit is excluded, since the harness installs it into every task's workspace — a
    /// task that wrote no page would otherwise look like interface work on the strength of files
    /// it never touched.
    /// </summary>
    public static bool TouchesInterface(IEnumerable<string> changedFiles) =>
        changedFiles.Where(file => !Ui.UiKit.IsKitFile(file)).Any(file =>
            file.Contains("wwwroot/", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where screenshots are written: outside the repo tree, so a check never adds a file the
    /// engineer would commit.
    /// </summary>
    private static string ScreenshotDir(string workspaceDir) =>
        Path.Combine(Path.GetTempPath(), "forge-pages", Path.GetFileName(workspaceDir.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
}
