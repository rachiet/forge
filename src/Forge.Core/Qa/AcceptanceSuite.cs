using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Agents;

namespace Forge.Core.Qa;

/// <summary>One acceptance run: whether the suite passed, and what it printed.</summary>
/// <param name="Ran">
/// False when there was nothing to run — no suite, or no app to start. The distinction
/// matters: a suite that did not run is not a suite that passed.
/// </param>
public sealed record AcceptanceResult(bool Passed, bool Ran, string Output)
{
    public static AcceptanceResult NotRun(string why) => new(false, false, why);
}

/// <summary>
/// The project's acceptance suite: black-box tests QA writes against the OpenAPI contract,
/// living in the client repo and run by the harness rather than by an agent. The verdict on
/// a finished project is this suite's exit code.
/// </summary>
/// <remarks>
/// Two properties keep it honest. It is not in the solution file, so the engineer's CI —
/// which builds and tests the .sln — never runs it and never goes red on a half-built
/// feature. And it reaches the app only over HTTP at <see cref="BaseUrlVariable"/>, so it
/// cannot reference the application's own assemblies and quietly become a white-box test.
/// </remarks>
public static partial class AcceptanceSuite
{
    /// <summary>Repo-relative directory holding the suite.</summary>
    public const string Directory = "tests/acceptance";

    /// <summary>The environment variable carrying the running app's base URL.</summary>
    public const string BaseUrlVariable = "FORGE_BASE_URL";

    /// <summary>The xUnit trait naming the contract operation a test exercises.</summary>
    public const string OperationTrait = "operation";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Which contract operations the suite claims to cover, read from the source rather
    /// than by running anything — a coverage gap has to be reportable before the suite is
    /// trusted enough to execute.
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

    public static bool Exists(string workspaceDir) =>
        System.IO.Directory.Exists(
            Path.Combine(workspaceDir, Directory.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Starts the application, runs the suite against it, and stops it again. Trusted code
    /// like <see cref="Ci.CiRunner"/>: it runs dotnet directly, so what comes back is process
    /// output rather than an agent's account of it.
    /// </summary>
    public static AcceptanceResult Run(string workspaceDir)
    {
        if (!Exists(workspaceDir)) return AcceptanceResult.NotRun("no acceptance suite in this repo");
        if (AgentToolset.Discover(workspaceDir) is not { } target)
            return AcceptanceResult.NotRun("no runnable application to test");

        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using var app = StartApp(workspaceDir, target.ProjectPath, baseUrl);
        if (app is null || !WaitForPort(port))
            return AcceptanceResult.NotRun(
                $"the application did not start listening on {baseUrl} within "
                + $"{StartupTimeout.TotalSeconds:0}s, so the suite was not run");

        var result = Dotnet(workspaceDir, baseUrl, "test", Directory, "--nologo");
        app.Stop();
        return result;
    }

    /// <summary>A port the OS says is free right now; the app is told to bind exactly it.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static RunningApp? StartApp(string workspaceDir, string project, string baseUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(project);
        // ASPNETCORE_URLS rather than a launch profile: the profile is the engineer's to
        // change, and a port the harness did not choose is a port it cannot address.
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;

        var process = Process.Start(psi);
        return process is null ? null : new RunningApp(process);
    }

    private static bool WaitForPort(int port)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", port);
                if (probe.Connected) return true;
            }
            catch (SocketException) { /* not up yet */ }
            Thread.Sleep(250);
        }
        return false;
    }

    private static AcceptanceResult Dotnet(string dir, string baseUrl, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment[BaseUrlVariable] = baseUrl;

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

    /// <summary>Nothing the harness starts outlives the round, however the round ends.</summary>
    private sealed class RunningApp(Process process) : IDisposable
    {
        public void Stop()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        public void Dispose()
        {
            Stop();
            process.Dispose();
        }
    }

    [GeneratedRegex($"""Trait\s*\(\s*"{OperationTrait}"\s*,\s*"([^"]+)"\s*\)""")]
    private static partial Regex OperationTraitPattern();
}
