using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Agents;

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

    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);

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
    public static string? EnsureScaffold(string workspaceDir)
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

        var created = Dotnet(workspaceDir, baseUrl: "",
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
        return null;
    }

    /// <summary>
    /// Compiles the suite, separately from running it. A suite that does not build has tested
    /// nothing, and must never be reported as a suite that failed: a failed run becomes a bug
    /// against the product, and rejecting that bug completes the project.
    /// </summary>
    public static AcceptanceResult Build(string workspaceDir) =>
        Dotnet(workspaceDir, baseUrl: "", "build", ProjectFile, "--nologo");

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
    public static AcceptanceResult Run(string workspaceDir, bool alreadyBuilt = false)
    {
        if (!Exists(workspaceDir)) return AcceptanceResult.NotRun("no acceptance suite in this repo");
        if (AgentToolset.Discover(workspaceDir) is not { } target)
            return AcceptanceResult.NotRun("no runnable application to test");

        if (!alreadyBuilt && Build(workspaceDir) is { Passed: false } build)
            return AcceptanceResult.NotRun(
                "the acceptance suite does not compile, so no test ran:\n" + build.Output);

        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        using var app = StartApp(workspaceDir, target.ProjectPath, baseUrl);
        if (app is null || !WaitForPort(port))
            return AcceptanceResult.NotRun(
                $"the application did not start listening on {baseUrl} within "
                + $"{StartupTimeout.TotalSeconds:0}s, so the suite was not run");

        var result = Dotnet(workspaceDir, baseUrl, "test", ProjectFile, "--nologo", "--no-build");
        app.Stop();
        return result;
    }

    /// <summary>A loopback port the OS reports as free.</summary>
    private static int FreePort()
    {
        using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Starts the application bound to the given base URL, or null if it will not start.</summary>
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
        // Bind the port the harness chose, ignoring whatever the launch profile says.
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;

        var process = Process.Start(psi);
        return process is null ? null : new RunningApp(process);
    }

    /// <summary>Polls the port until something accepts a connection, or the startup timeout passes.</summary>
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

    /// <summary>
    /// Runs one dotnet command with the base URL in the environment and captures its output.
    /// A command that outlives the timeout is killed with its process tree and reported as failed.
    /// </summary>
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

    /// <summary>An application the harness started, killed on Stop or Dispose.</summary>
    private sealed class RunningApp(Process process) : IDisposable
    {
        /// <summary>Kills the process and its children if it is still alive.</summary>
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

    /// <summary>Matches `[Trait("operation", "<id>")]`, capturing the operationId.</summary>
    [GeneratedRegex($"""Trait\s*\(\s*"{OperationTrait}"\s*,\s*"([^"]+)"\s*\)""")]
    private static partial Regex OperationTraitPattern();
}
