using System.Diagnostics;
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

        using var app = StartApp(workspaceDir, target.ProjectPath);
        if (app is null)
            return AcceptanceResult.NotRun("the application would not start, so the suite was not run");

        // The address is read from the app, never assumed: it is told to bind any free port
        // and prints the one it got.
        if (app.WaitForListeningUrl(StartupTimeout) is not { } baseUrl)
            return AcceptanceResult.NotRun(
                $"the application did not report a listening address within "
                + $"{StartupTimeout.TotalSeconds:0}s, so the suite was not run:\n{app.Output}");

        var result = Dotnet(workspaceDir, baseUrl, "test", ProjectFile, "--nologo", "--no-build");
        app.Stop();
        return result;
    }

    /// <summary>
    /// Starts the application on whatever port the OS gives it, or null if the process will not
    /// start at all. `--no-launch-profile` is what makes that true: `dotnet run` otherwise
    /// applies launchSettings.json, whose applicationUrl overrides ASPNETCORE_URLS and binds the
    /// developer's fixed port — which is already taken as often as not.
    /// </summary>
    private static RunningApp? StartApp(string workspaceDir, string project)
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
        psi.ArgumentList.Add("--no-launch-profile");
        // Port 0 is "any free one"; the app reports which, and that is what the suite is given.
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        var process = Process.Start(psi);
        return process is null ? null : new RunningApp(process);
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

    /// <summary>
    /// An application the harness started, killed on Stop or Dispose. Its output is drained as
    /// it arrives — a redirected pipe left unread fills and blocks the app — and watched for the
    /// address it bound, which is the only trustworthy source of that address.
    /// </summary>
    private sealed class RunningApp : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly TaskCompletionSource<string> _listening =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunningApp(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        /// <summary>Everything the app has printed so far, so a failure to start can be reported.</summary>
        public string Output
        {
            get { lock (_output) return _output.ToString().Trim(); }
        }

        /// <summary>
        /// The base URL the app reported listening on, or null if it exited or said nothing
        /// within <paramref name="timeout"/>.
        /// </summary>
        public string? WaitForListeningUrl(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_listening.Task.Wait(TimeSpan.FromMilliseconds(250)))
                    return _listening.Task.Result;
                // Exited without ever listening: waiting out the timeout would tell us nothing.
                if (_process.HasExited) return null;
            }
            return null;
        }

        /// <summary>Kills the process and its children if it is still alive.</summary>
        public void Stop()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        public void Dispose()
        {
            Stop();
            _process.Dispose();
        }

        private void Capture(string? line)
        {
            if (line is null) return;
            lock (_output) _output.AppendLine(line);
            if (ListeningPattern().Match(line) is { Success: true } match)
                _listening.TrySetResult(match.Groups[1].Value.TrimEnd('/'));
        }
    }

    /// <summary>Matches Kestrel's `Now listening on: http://127.0.0.1:1234` startup line.</summary>
    [GeneratedRegex(@"Now listening on:\s*(https?://\S+)")]
    private static partial Regex ListeningPattern();

    /// <summary>Matches `[Trait("operation", "<id>")]`, capturing the operationId.</summary>
    [GeneratedRegex($"""Trait\s*\(\s*"{OperationTrait}"\s*,\s*"([^"]+)"\s*\)""")]
    private static partial Regex OperationTraitPattern();
}
