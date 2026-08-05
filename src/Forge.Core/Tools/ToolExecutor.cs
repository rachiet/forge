using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Secrets;

namespace Forge.Core.Tools;

public sealed record ToolResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed class ToolJailViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// A process still running under the harness's supervision — what <see cref="ToolExecutor.Start"/>
/// returns. Output is captured continuously into a buffer rather than read at the end, because the
/// interesting part of a server's log (the port it bound, the stack trace behind a 500) is written
/// while it runs, and a process that never exits would otherwise never surrender a line of it.
/// </summary>
public sealed class ServerHandle : IDisposable
{
    private readonly Process _process;
    private readonly Func<string, string> _redact;
    private readonly StringBuilder _output = new();

    /// <summary>Output arrives on threadpool threads while the agent's turn reads it.</summary>
    private readonly object _gate = new();

    /// <summary>How much of <see cref="_output"/> has already been shown, so each read is a delta.</summary>
    private int _shown;

    private ServerHandle(string command, Process process, Func<string, string> redact) =>
        (Command, _process, _redact) = (command, process, redact);

    internal static ServerHandle Launch(string command, ProcessStartInfo psi, Func<string, string> redact)
    {
        var process = new Process { StartInfo = psi };
        var handle = new ServerHandle(command, process, redact);

        // Event-driven rather than ReadToEnd: those calls only complete at exit, which for a
        // server is never. This delivers each line as it is written, which is what makes
        // "wait until it says it is listening" possible.
        process.OutputDataReceived += (_, e) => handle.Capture(e.Data);
        process.ErrorDataReceived += (_, e) => handle.Capture(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return handle;
    }

    /// <summary>The command as the agent typed it — what how_to_run is checked against.</summary>
    public string Command { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>Meaningful only once <see cref="HasExited"/>; -1 while it is still running.</summary>
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    /// <summary>Everything the process has written so far, secrets redacted.</summary>
    public string Output
    {
        get { lock (_gate) return _redact(_output.ToString()); }
    }

    /// <summary>
    /// Output written since the last call — the server's side of whatever just happened.
    /// Consuming it is what keeps a long-lived log from being re-shown on every request.
    /// </summary>
    public string OutputSinceLastRead()
    {
        lock (_gate)
        {
            if (_shown >= _output.Length) return "";
            var text = _output.ToString(_shown, _output.Length - _shown);
            _shown = _output.Length;
            return _redact(text);
        }
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        lock (_gate) _output.AppendLine(line);
    }

    /// <summary>
    /// Kills the whole tree, not just the process we started: `dotnet run` is a launcher whose
    /// child holds the port, so killing the parent alone leaves the socket bound and the next
    /// serve() fails on an address already in use.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(milliseconds: 5_000);
            }
        }
        catch (InvalidOperationException) { /* already gone — nothing to kill */ }
        finally { _process.Dispose(); }
    }
}

/// <summary>
/// Executes agent-requested commands under mechanical supervision (spec §11):
/// no shell, allowlisted binaries only, working directory jailed to the task
/// workspace, per-command timeout, {{secret:NAME}} substituted at exec time.
/// Secret values are redacted from captured output so they never reach the
/// model, the DB, or logs.
/// </summary>
public sealed partial class ToolExecutor(
    string jailRoot,
    IReadOnlyCollection<string> allowedBinaries,
    SecretsVault vault,
    TimeSpan? defaultTimeout = null,
    IReadOnlyDictionary<string, string>? environment = null)
{
    private readonly PathJail _jail = new(jailRoot);
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>Extra variables to hand child processes, on top of the allowlist.</summary>
    private readonly IReadOnlyDictionary<string, string> _environment =
        environment ?? new Dictionary<string, string>();

    public PathJail Jail => _jail;

    [GeneratedRegex(@"\{\{secret:([A-Za-z0-9_]+)\}\}")]
    private static partial Regex SecretRef();

    public async Task<ToolResult> RunAsync(
        string command,
        string? workingSubdir = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var (psi, secretsUsed) = Prepare(command, workingSubdir);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timedOut = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout ?? _defaultTimeout);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var stdout = Redact(await stdoutTask.ConfigureAwait(false), secretsUsed);
        var stderr = Redact(await stderrTask.ConfigureAwait(false), secretsUsed);
        return new ToolResult(timedOut ? -1 : process.ExitCode, stdout, stderr, timedOut);
    }

    /// <summary>
    /// Starts a process and hands back a live handle instead of waiting for it to exit —
    /// the one thing <see cref="RunAsync"/> structurally cannot do, since its timeout kills
    /// whatever is still alive. A server has to outlive the call that started it to be worth
    /// starting at all, so testing one through its own port needs this.
    /// </summary>
    /// <remarks>
    /// Every guarantee RunAsync makes still holds: same allowlist, same jail, same scrubbed
    /// environment, same secret substitution and redaction — because both go through
    /// <see cref="Prepare"/>. The difference is only who decides when the process dies, and
    /// that is the caller's job now: an undisposed handle is a leaked process.
    /// </remarks>
    public ServerHandle Start(string command, string? workingSubdir = null)
    {
        var (psi, secretsUsed) = Prepare(command, workingSubdir);
        return ServerHandle.Launch(command, psi, text => Redact(text, secretsUsed));
    }

    /// <summary>
    /// Everything that has to be true before a child process starts: the binary is on the
    /// allowlist, no argument points outside the jail, the environment is built rather than
    /// inherited, and {{secret:NAME}} is resolved at exec time. Shared by both start paths so
    /// a second one cannot quietly acquire weaker supervision than the first.
    /// </summary>
    private (ProcessStartInfo Psi, Dictionary<string, string> SecretsUsed) Prepare(
        string command, string? workingSubdir)
    {
        var argv = Tokenize(command);
        if (argv.Count == 0)
            throw new ArgumentException("Empty command.", nameof(command));

        var binary = argv[0];
        if (binary.Contains('/') || binary.Contains('\\') ||
            !allowedBinaries.Contains(binary, StringComparer.Ordinal))
        {
            throw new ToolJailViolationException(
                $"Binary '{binary}' is not on the allowlist ({string.Join(", ", allowedBinaries)}).");
        }

        var workingDir = ResolveWorkingDir(workingSubdir);
        foreach (var arg in argv.Skip(1))
            RejectJailEscape(arg, workingDir);

        var secretsUsed = new Dictionary<string, string>();
        var finalArgv = argv.Select(a => SubstituteSecrets(a, secretsUsed)).ToList();

        var psi = new ProcessStartInfo
        {
            FileName = finalArgv[0],
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in finalArgv.Skip(1)) psi.ArgumentList.Add(arg);
        ScrubEnvironment(psi);
        return (psi, secretsUsed);
    }

    /// <summary>
    /// Child processes would otherwise inherit Forge's whole environment — which
    /// holds the harness's own provider keys. An agent's `dotnet run` on code it
    /// just wrote is arbitrary code execution, so inheritance is a credential leak.
    /// Build the environment from an allowlist instead: a key added to forge_env
    /// tomorrow is invisible to agents by default, with nothing to remember.
    ///
    /// HOME points at the workspace so a child cannot read ~/forge_env either.
    /// This raises the bar; it is not a sandbox. Real isolation is a container,
    /// and that is a later problem than M1.
    /// </summary>
    private void ScrubEnvironment(ProcessStartInfo psi)
    {
        var inherited = psi.Environment;
        var passThrough = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThroughNames)
            if (inherited.TryGetValue(name, out var value) && value is not null)
                passThrough[name] = value;

        // Toolchain caches are shared on purpose: re-downloading NuGet packages
        // for every task would make the jail expensive rather than safe.
        foreach (var (name, value) in inherited)
            if (value is not null && (name.StartsWith("DOTNET_", StringComparison.Ordinal) ||
                                      name.StartsWith("NUGET_", StringComparison.Ordinal)))
                passThrough[name] = value;

        psi.Environment.Clear();
        foreach (var (name, value) in passThrough) psi.Environment[name] = value;
        foreach (var (name, value) in _environment) psi.Environment[name] = value;
        psi.Environment["HOME"] = _jail.Root;
        psi.Environment["USERPROFILE"] = _jail.Root;
    }

    private static readonly string[] PassThroughNames =
        ["PATH", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "TERM", "SystemRoot", "ComSpec"];

    private string ResolveWorkingDir(string? subdir)
    {
        var dir = subdir is null ? _jail.Root : _jail.Resolve(subdir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Working directory '{dir}' does not exist.");
        return dir;
    }

    /// <summary>
    /// Heuristic path guard on arguments: anything that syntactically points
    /// outside the jail (absolute paths, ~, or ..-escapes) is refused. Flags in
    /// --name=value form are checked on their value part.
    /// </summary>
    private void RejectJailEscape(string arg, string workingDir)
    {
        var candidate = arg;
        var eq = arg.IndexOf('=');
        if (arg.StartsWith('-') && eq >= 0) candidate = arg[(eq + 1)..];

        if (candidate.StartsWith('~'))
            throw new ToolJailViolationException($"Argument '{arg}' references the home directory.");

        var looksAbsolute = Path.IsPathRooted(candidate);
        var hasDotDot = candidate.Split('/', '\\').Contains("..");
        if (!looksAbsolute && !hasDotDot) return;

        var resolved = Path.GetFullPath(looksAbsolute ? candidate : Path.Combine(workingDir, candidate));
        if (!_jail.Contains(resolved))
            throw new ToolJailViolationException(
                $"Argument '{arg}' resolves outside the task workspace ({resolved}).");
    }

    private string SubstituteSecrets(string arg, Dictionary<string, string> secretsUsed) =>
        SecretRef().Replace(arg, m =>
        {
            var name = m.Groups[1].Value;
            var value = vault.Get(name);
            secretsUsed[name] = value;
            return value;
        });

    private static string Redact(string output, Dictionary<string, string> secretsUsed)
    {
        foreach (var (name, value) in secretsUsed)
            output = output.Replace(value, $"{{{{secret:{name}}}}}");
        return output;
    }

    /// <summary>Quote-aware tokenizer. No shell is involved, so shell operators are refused loudly
    /// rather than silently passed to the binary as literal arguments.</summary>
    internal static List<string> Tokenize(string command)
    {
        List<string> tokens = [];
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (inSingle)
            {
                if (c == '\'') inSingle = false; else current.Append(c);
            }
            else if (inDouble)
            {
                if (c == '"') inDouble = false;
                else if (c == '\\' && i + 1 < command.Length && command[i + 1] is '"' or '\\')
                    current.Append(command[++i]);
                else current.Append(c);
            }
            else if (c == '\'') { inSingle = true; hasToken = true; }
            else if (c == '"') { inDouble = true; hasToken = true; }
            else if (c == '\\' && i + 1 < command.Length) { current.Append(command[++i]); hasToken = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
            }
            else if (c is '|' or '&' or ';' or '<' or '>' or '`' or '$' or '(' or ')')
            {
                throw new ArgumentException(
                    $"Shell operator '{c}' is not supported: commands run without a shell. " +
                    "Run one binary per command.", nameof(command));
            }
            else { current.Append(c); hasToken = true; }
        }

        if (inSingle || inDouble)
            throw new ArgumentException("Unbalanced quote in command.", nameof(command));
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }
}
