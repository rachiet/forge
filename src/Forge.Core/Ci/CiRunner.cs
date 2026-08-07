using System.Diagnostics;
using System.Text;

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

    /// <summary>
    /// Runs `dotnet build`, then `dotnet test`, in the workspace. Returns at the first failure.
    /// A workspace with no .sln or .csproj is a skip, not a failure.
    /// </summary>
    public static CiResult Run(string workspaceDir)
    {
        var buildable = Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workspaceDir, "*.csproj", SearchOption.AllDirectories))
            .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"));
        if (!buildable)
            return CiResult.Skip("no .sln/.csproj — nothing to build (docs-only or not yet scaffolded)");

        var build = Dotnet(workspaceDir, "build", "--nologo");
        if (!build.Passed) return build;

        return Dotnet(workspaceDir, "test", "--nologo");
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
