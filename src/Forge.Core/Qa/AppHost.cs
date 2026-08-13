using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core.Qa;

/// <summary>
/// Starts a generated application inside a workspace and reports the address it bound. Used by
/// everything that needs the real thing running — the acceptance suite and the browser page
/// check — so both start it the same way and neither guesses a port.
/// </summary>
public static partial class AppHost
{
    /// <summary>How long an application is given to report that it is listening.</summary>
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Starts the application on whatever port the OS gives it, or null if the process will not
    /// start at all. `--no-launch-profile` is what makes that true: `dotnet run` otherwise
    /// applies launchSettings.json, whose applicationUrl overrides ASPNETCORE_URLS and binds the
    /// developer's fixed port — which is already taken as often as not.
    /// </summary>
    public static RunningApp? Start(string workspaceDir, string project)
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
        // Port 0 is "any free one"; the app reports which, and that is what callers are given.
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        var process = Process.Start(psi);
        return process is null ? null : new RunningApp(process);
    }

    /// <summary>
    /// An application the harness started, killed on Stop or Dispose. Its output is drained as
    /// it arrives — a redirected pipe left unread fills and blocks the app — and watched for the
    /// address it bound, which is the only trustworthy source of that address.
    /// </summary>
    public sealed class RunningApp : IDisposable
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
}
