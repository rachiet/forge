using System.Diagnostics;

namespace Forge.Core.Workspaces;

/// <summary>What one git command produced.</summary>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Whether the command exited zero.</summary>
    public bool Ok => ExitCode == 0;
    /// <summary>Standard output, or standard error when it wrote nothing.</summary>
    public string Output => string.IsNullOrWhiteSpace(Stdout) ? Stderr.Trim() : Stdout.Trim();
}

/// <summary>Thrown when a git command the harness required exits non-zero.</summary>
public sealed class GitException(string command, GitResult result)
    : InvalidOperationException($"git {command} failed ({result.ExitCode}): {result.Stderr.Trim()}");

/// <summary>
/// Runs git for the harness, separately from the agent's jailed run() tool. Merge state and
/// branch state are read from here, never from an agent's claim.
/// </summary>
public static class Git
{
    /// <summary>
    /// The identity every harness commit is made under, set explicitly rather than inherited
    /// from the machine's git config, which may be absent.
    /// </summary>
    private static readonly string[] Identity =
    [
        "-c", "user.name=Forge",
        "-c", "user.email=forge@localhost",
        "-c", "commit.gpgsign=false",
    ];

    /// <summary>Runs a git command and returns its result, whether or not it succeeded.</summary>
    public static GitResult Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in Identity) psi.ArgumentList.Add(arg);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start git — is it installed and on PATH?");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>Runs a git command and throws unless it exits zero.</summary>
    public static GitResult Require(string workingDir, params string[] args)
    {
        var result = Run(workingDir, args);
        return result.Ok ? result : throw new GitException(string.Join(' ', args), result);
    }
}
