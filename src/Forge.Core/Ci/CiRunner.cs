using System.Diagnostics;
using System.Text;
using Forge.Core.Agents;

namespace Forge.Core.Ci;

/// <summary>
/// The outcome of one CI step: whether it passed, which step it was, and the process output.
/// Skipped means there was nothing to run, and counts as passed.
/// </summary>
public sealed record CiResult(bool Passed, string Step, string Output, bool Skipped = false)
{
    /// <summary>A passing result for a workspace with nothing to build.</summary>
    public static CiResult Skip(string why) => new(true, "detect", why, Skipped: true);

    /// <summary>One line for the log; the full output goes to the engineer as feedback.</summary>
    public string Summary => Skipped
        ? $"skipped: {Output}"
        : $"{Step}: {(Passed ? "passed" : "FAILED")}";
}

/// <summary>
/// Builds and tests a task's workspace, using no LLM tokens. Trusted harness code, so it runs
/// dotnet directly rather than through the agent's jailed executor, and the pass/fail comes
/// from the process exit code rather than from the agent's account of it.
/// </summary>
public static class CiRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Where QA's acceptance suite lives; it must never appear in the solution.</summary>
    private const string AcceptanceDirectory = "tests/acceptance";

    /// <summary>
    /// Runs `dotnet build`, then `dotnet test`, in the workspace. Returns at the first failure.
    /// A workspace with no .sln or .csproj is a skip, not a failure.
    /// </summary>
    public static CiResult Run(string workspaceDir)
    {
        // Ahead of the buildable gate: a repo of static files has nothing to compile and would
        // otherwise skip the one check that reads what it ships.
        if (StaticFileCheck.Check(workspaceDir) is { } unparseable)
            return new CiResult(false, "static", unparseable);

        var buildable = Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workspaceDir, "*.csproj", SearchOption.AllDirectories))
            .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));
        if (!buildable)
            return CiResult.Skip("no .sln/.csproj — nothing to build (docs-only or not yet scaffolded)");

        if (LayoutViolation(workspaceDir) is { } violation)
            return new CiResult(false, "layout", violation);

        var build = Dotnet(workspaceDir, "build", "--nologo");
        if (!build.Passed) return build;

        return Dotnet(workspaceDir, "test", "--nologo");
    }

    /// <summary>
    /// Why the repo's project layout is unacceptable, or null when it is fine. Two rules, both
    /// checked before the build so the engineer that broke one is the one told about it: a repo
    /// has exactly one runnable project, and QA's acceptance suite stays out of the solution.
    /// The message is what the engineer resumes with, so it names the offending paths.
    /// </summary>
    private static string? LayoutViolation(string workspaceDir)
    {
        if (AgentToolset.RunnableProjects(workspaceDir) is { Count: > 1 } runnable)
            return $"This repo has {runnable.Count} runnable projects: {string.Join(", ", runnable)}.\n"
                 + "A project serves its user interface itself, as static files under its own "
                 + "wwwroot/, so one `dotnet run` serves both the pages and the API on one port. "
                 + "Delete the extra project and move its files into the existing one, or make it "
                 + "a class library.";

        var solution = Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));
        // Solution files write paths with backslashes whatever wrote them, so compare on a
        // normalised copy rather than the separator this machine happens to use.
        if (solution is not null
            && File.ReadAllText(solution).Replace('\\', '/')
                .Contains(AcceptanceDirectory, StringComparison.OrdinalIgnoreCase))
            return $"{Path.GetFileName(solution)} lists the acceptance suite in {AcceptanceDirectory}.\n"
                 + "That suite is QA's, and only runs against a started application, so including it "
                 + "makes `dotnet test` fail for every task. Remove it with "
                 + $"`dotnet sln remove {AcceptanceDirectory}/…` and leave it out of the solution.";

        return null;
    }

    /// <summary>
    /// Runs one dotnet command in the workspace and captures its output. A command that
    /// outlives the timeout is killed with its process tree and reported as failed.
    /// </summary>
    private static CiResult Dotnet(string dir, string step, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(step);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet — is the .NET SDK on PATH?");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            return new CiResult(false, step, $"dotnet {step} timed out after {Timeout.TotalMinutes:0} minutes.");
        }

        var output = new StringBuilder(stdout.GetAwaiter().GetResult());
        var err = stderr.GetAwaiter().GetResult();
        if (err.Length > 0) output.Append('\n').Append(err);
        return new CiResult(process.ExitCode == 0, step, output.ToString().Trim());
    }
}
