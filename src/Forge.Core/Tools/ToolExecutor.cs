using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Secrets;

namespace Forge.Core.Tools;

/// <summary>What one finished command produced.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Stdout">Standard output, secrets redacted.</param>
/// <param name="Stderr">Standard error, secrets redacted.</param>
/// <param name="TimedOut">Whether the harness killed it at the timeout.</param>
public sealed record ToolResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

/// <summary>Thrown when a tool call reaches outside its jail, its scope, or the binary allowlist.</summary>
public sealed class ToolJailViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// A process the harness started and left running, returned by <see cref="ToolExecutor.Start"/>.
/// Its output is captured line by line as it is written, so a server that never exits can still
/// be read from.
/// </summary>
public sealed class ServerHandle : IDisposable
{
    private readonly Process _process;
    private readonly Func<string, string> _redact;
    private readonly StringBuilder _output = new();

    /// <summary>Guards the buffer: output arrives on threadpool threads while a turn reads it.</summary>
    private readonly object _gate = new();

    /// <summary>How much of <see cref="_output"/> has already been shown, so each read is a delta.</summary>
    private int _shown;

    /// <summary>Private: instances come from <see cref="Launch"/>, which starts the process.</summary>
    private ServerHandle(string command, Process process, Func<string, string> redact) =>
        (Command, _process, _redact) = (command, process, redact);

    /// <summary>Starts the process and begins capturing its output.</summary>
    internal static ServerHandle Launch(string command, ProcessStartInfo psi, Func<string, string> redact)
    {
        var process = new Process { StartInfo = psi };
        var handle = new ServerHandle(command, process, redact);

        // Event-driven, so each line is available while the process is still running.
        process.OutputDataReceived += (_, e) => handle.Capture(e.Data);
        process.ErrorDataReceived += (_, e) => handle.Capture(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return handle;
    }

    /// <summary>The command as the agent typed it.</summary>
    public string Command { get; }

    /// <summary>Whether the process has ended.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Meaningful only once <see cref="HasExited"/>; -1 while it is still running.</summary>
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    /// <summary>Everything the process has written so far, secrets redacted.</summary>
    public string Output
    {
        get { lock (_gate) return _redact(_output.ToString()); }
    }

    /// <summary>Output written since the previous call, and marks it consumed.</summary>
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

    /// <summary>Appends one captured output line to the buffer.</summary>
    private void Capture(string? line)
    {
        if (line is null) return;
        lock (_gate) _output.AppendLine(line);
    }

    /// <summary>
    /// Kills the process and its whole tree — `dotnet run` is a launcher whose child holds the
    /// port — and waits briefly for it to exit.
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
/// Runs agent-requested commands under supervision: no shell, allowlisted binaries only, the
/// working directory jailed to the task workspace, a per-command timeout, an environment built
/// from an allowlist rather than inherited, and {{secret:NAME}} substituted at exec time.
/// Secret values are redacted from captured output.
/// </summary>
public sealed partial class ToolExecutor(
    string jailRoot,
    IReadOnlyCollection<string> allowedBinaries,
    SecretsVault vault,
    TimeSpan? defaultTimeout = null,
    IReadOnlyDictionary<string, string>? environment = null,
    string? agentHome = null)
{
    private readonly PathJail _jail = new(jailRoot);
    private readonly TimeSpan _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>
    /// HOME for child processes. Defaults to a directory beside the jail rather than inside it,
    /// because a toolchain writing its caches into the jail puts them in the agent's checkout.
    /// </summary>
    private readonly string _agentHome = agentHome ??
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jailRoot))!, "agent-home");

    /// <summary>Extra variables to hand child processes, on top of the allowlist.</summary>
    private readonly IReadOnlyDictionary<string, string> _environment =
        environment ?? new Dictionary<string, string>();

    /// <summary>The workspace every path in a command is resolved against.</summary>
    public PathJail Jail => _jail;

    /// <summary>Matches a `{{secret:NAME}}` placeholder, capturing the name.</summary>
    [GeneratedRegex(@"\{\{secret:([A-Za-z0-9_]+)\}\}")]
    private static partial Regex SecretRef();

    /// <summary>
    /// Runs one command to completion and returns its exit code and output. A command that
    /// outlives the timeout is killed with its process tree and reported as timed out.
    /// </summary>
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
    /// Starts a process and returns a live handle rather than waiting for it to exit, for
    /// servers. Same allowlist, jail, environment and secret handling as <see cref="RunAsync"/>;
    /// the caller owns the handle's lifetime, and an undisposed handle is a leaked process.
    /// </summary>
    public ServerHandle Start(string command, string? workingSubdir = null)
    {
        var (psi, secretsUsed) = Prepare(command, workingSubdir);
        return ServerHandle.Launch(command, psi, text => Redact(text, secretsUsed));
    }

    /// <summary>
    /// Builds the ProcessStartInfo both start paths use: checks the binary against the
    /// allowlist, resolves the working directory inside the jail, builds the environment, and
    /// substitutes any {{secret:NAME}} placeholders. Returns the secrets used, for redaction.
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
    /// Replaces the child's environment with one built from an allowlist instead of inherited,
    /// so Forge's own provider keys are never visible to it, and points HOME at the agent home.
    /// Raises the bar; it is not a sandbox.
    /// </summary>
    private void ScrubEnvironment(ProcessStartInfo psi)
    {
        var inherited = psi.Environment;
        var passThrough = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThroughNames)
            if (inherited.TryGetValue(name, out var value) && value is not null)
                passThrough[name] = value;

        // DOTNET_/NUGET_ pass through so toolchain caches are shared across tasks.
        foreach (var (name, value) in inherited)
            if (value is not null && (name.StartsWith("DOTNET_", StringComparison.Ordinal) ||
                                      name.StartsWith("NUGET_", StringComparison.Ordinal)))
                passThrough[name] = value;

        psi.Environment.Clear();
        foreach (var (name, value) in passThrough) psi.Environment[name] = value;
        foreach (var (name, value) in _environment) psi.Environment[name] = value;
        // Created here rather than at construction: a child process needs it to exist, and by
        // then the jail's parent directory certainly does.
        Directory.CreateDirectory(_agentHome);
        psi.Environment["HOME"] = _agentHome;
        psi.Environment["USERPROFILE"] = _agentHome;
    }

    /// <summary>Environment variables a child process may inherit.</summary>
    private static readonly string[] PassThroughNames =
        ["PATH", "TMPDIR", "TEMP", "TMP", "LANG", "LC_ALL", "TERM", "SystemRoot", "ComSpec"];

    /// <summary>Resolves the command's working directory inside the jail; throws if it is missing.</summary>
    private string ResolveWorkingDir(string? subdir)
    {
        var dir = subdir is null ? _jail.Root : _jail.Resolve(subdir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Working directory '{dir}' does not exist.");
        return dir;
    }

    /// <summary>
    /// Refuses an argument that points outside the jail — an absolute path, a `~`, or a
    /// `..` escape. Flags in --name=value form are checked on their value.
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

    /// <summary>Replaces {{secret:NAME}} with its value, recording which secrets were used.</summary>
    private string SubstituteSecrets(string arg, Dictionary<string, string> secretsUsed) =>
        SecretRef().Replace(arg, m =>
        {
            var name = m.Groups[1].Value;
            var value = vault.Get(name);
            secretsUsed[name] = value;
            return value;
        });

    /// <summary>Replaces any secret value in captured output with its {{secret:NAME}} placeholder.</summary>
    private static string Redact(string output, Dictionary<string, string> secretsUsed)
    {
        foreach (var (name, value) in secretsUsed)
            output = output.Replace(value, $"{{{{secret:{name}}}}}");
        return output;
    }

    /// <summary>
    /// Splits a command into argv, honouring single and double quotes and backslash escapes.
    /// Shell operators are refused rather than passed to the binary as literal arguments.
    /// </summary>
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
