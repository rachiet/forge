using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>
/// What QA should start, read out of the repo: the startup project and, when the project
/// says so, the URL it will serve on.
/// </summary>
/// <param name="ProjectPath">Repo-relative path of the .csproj to run.</param>
/// <param name="Url">The http URL from launchSettings, or null when the project does not say.</param>
/// <param name="Alternatives">
/// The other runnable roots when the repo has more than one. Empty in the normal case; a
/// non-empty list means the choice was genuinely ambiguous and someone should decide it.
/// </param>
public sealed record RunTarget(string ProjectPath, string? Url, IReadOnlyList<string> Alternatives);

/// <summary>
/// The tools QA needs and no other role has: start the app and keep it running, then talk to
/// it over its own port.
///
/// Why they exist at all: QA is the only role whose job is to observe a program from the
/// outside while it runs. Every other role either reads the repo or hands work to CI, and
/// run() serves both — it starts a binary, waits for it to exit, and kills whatever is still
/// alive at the timeout. A server has no exit; run() can therefore only ever report that it
/// killed one, which is exactly what happened the first time QA met a web project. It could
/// not reach the app, had no way to say so, and passed the project by filing nothing.
///
/// So the pair here is deliberate and minimal: serve() owns a process's lifetime, http() is
/// the request half that run() cannot express (no shell, and curl is not on any allowlist —
/// nor should it be, since a request the harness makes itself is a request it can capture
/// verbatim as evidence). Both feed the same file_bug evidence trail as run(): what QA
/// reports is what the harness watched happen, never what the model recalls.
/// </summary>
public sealed partial class AgentToolset : IDisposable
{
    /// <summary>How long serve() waits for a server to bind before giving up on it.</summary>
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a single http() request may take before it is abandoned.</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private const int ReadyPollMs = 250;
    private const int MaxBodyChars = 4_000;
    private const int MaxStartupOutputChars = 3_000;

    /// <summary>
    /// Servers this instance started, in start order — the id an agent names is the index + 1.
    /// A list rather than a single slot because a project may legitimately serve an API and a
    /// front end from two processes, and because a failed first attempt should not have to be
    /// stopped before a second can be tried.
    /// </summary>
    private readonly List<RunningServer> _servers = [];

    /// <summary>One served process and the base URL the harness confirmed it is listening on.</summary>
    private sealed record RunningServer(int Id, ServerHandle Handle, string BaseUrl);

    /// <summary>
    /// Shared by every http() call so connections are pooled and a dev server's self-signed
    /// certificate does not fail the request. Certificate validation is off because the only
    /// hosts reachable here are loopback ones we started ourselves — there is no identity to
    /// establish, and a cert error would read to the model as a defect in the app.
    /// </summary>
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AllowAutoRedirect = false,
    })
    { Timeout = HttpTimeout };

    /// <summary>
    /// Documentation for the QA-only tools, kept beside their implementation so the two cannot
    /// drift. Merged into <see cref="Catalogue"/>; a property rather than a field because static
    /// field initialisation order across partial files is not something to bet a null on.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> QaCatalogue =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serve"] = "serve(command, [port], [cwd], [ready_timeout]) — start a server and LEAVE it "
                      + "running. Use this, not run(), for anything that does not exit on its own: run() "
                      + "waits for the process to finish and kills it at the timeout, so it can never host "
                      + "something you then send requests to. The harness waits until the server is really "
                      + "listening and returns its base URL, then stops it when your run ends. Give port if "
                      + "the server does not print its URL at startup; ready_timeout is in seconds.",
            ["stop_server"] = "stop_server([server]) — stop a server started with serve (all of them if you "
                            + "name none) and see its final output. Not required at the end of your run; the "
                            + "harness stops them for you.",
            ["http"] = "http(url, [method], [body], [headers], [content_type]) — send ONE request to a server "
                     + "you started with serve, and see the real status line, response headers and body. url "
                     + "may be just a path (/api/things) to use the server's own base URL. method defaults to "
                     + "GET; headers is one `Name: value` per line; a body defaults to application/json. "
                     + "Anything the server logged meanwhile comes back with the response, so a 500 arrives "
                     + "with its stack trace. Loopback addresses only — this tests your app, not the internet.",
        };

    /// <summary>
    /// The QA tools' dispatch arm, called for any name the main switch does not handle, so every
    /// QA-only tool name lives in this file. An unknown name still ends where it always did.
    /// </summary>
    private async Task<ToolOutcome> QaToolAsync(ToolCall call, CancellationToken ct) => call.Name switch
    {
        "serve" => await ServeAsync(call, ct).ConfigureAwait(false),
        "stop_server" => StopServer(call),
        "http" => await HttpAsync(call, ct).ConfigureAwait(false),
        _ => new ToolOutcome($"ERROR: tool '{call.Name}' is not implemented."),
    };

    /// <summary>
    /// Starts a server and does not return until it is provably accepting connections — the
    /// distinction that matters, because "the process is alive" and "the app is up" are minutes
    /// apart for a .NET web project and only the second one makes the next http() meaningful.
    /// </summary>
    /// <remarks>
    /// Readiness is read from the server itself: Kestrel and friends announce `Now listening on:
    /// http://localhost:5161`, which is ground truth for the port AND the scheme, neither of
    /// which the harness could otherwise know. A server that announces nothing is still testable
    /// by passing an explicit port, which is then confirmed by opening a socket to it.
    /// </remarks>
    private async Task<ToolOutcome> ServeAsync(ToolCall call, CancellationToken ct)
    {
        var command = call.Arg("command");
        var port = call.OptionalInt("port");
        var timeout = call.OptionalInt("ready_timeout") is { } seconds and > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultReadyTimeout;

        var handle = executor.Start(command, call.Optional("cwd"));
        var deadline = DateTime.UtcNow + timeout;
        string? baseUrl = null;

        while (DateTime.UtcNow < deadline)
        {
            // A server that dies during startup is the most informative failure there is: the
            // reason is in the output it just produced. Keep it as evidence and stop waiting.
            if (handle.HasExited)
            {
                var exitTrace = Trace(
                    $"$ {command}   (started with serve)",
                    $"exited during startup with code {handle.ExitCode}",
                    handle.Output);
                handle.Dispose();
                // Evidence, but NOT a command how_to_run may quote: it is the one thing we know
                // does not start the app, and the client would be told to type it.
                RecordEvidence(exitTrace, command: null);
                return new ToolOutcome(Truncate(
                    exitTrace + "\n\nThe server did not stay up, so there is nothing to send requests to. "
                    + "Fix the cause or file a bug — this output is the evidence." + StartupHint()));
            }

            if (AnnouncedUrl(handle.Output, port) is { } announced) { baseUrl = announced; break; }
            if (port is { } p && await PortAcceptsAsync(p, ct).ConfigureAwait(false))
            {
                baseUrl = $"http://127.0.0.1:{p}";
                break;
            }

            await Task.Delay(ReadyPollMs, ct).ConfigureAwait(false);
        }

        if (baseUrl is null)
        {
            // Still alive but unreachable. Killing it is the honest outcome: a process we cannot
            // address is one nothing will ever stop, and leaking it would bind the port against
            // the retry the agent is about to make.
            var stalled = handle.Output;
            handle.Dispose();
            return new ToolOutcome(Truncate(
                $"ERROR: `{command}` did not start listening within {timeout.TotalSeconds:0}s, so it was "
                + "stopped. If it takes longer, raise ready_timeout; if it does not print the URL it binds, "
                + "pass port so the harness can check the socket directly.\n\n--- output so far ---\n"
                + Tail(stalled, MaxStartupOutputChars)));
        }

        var server = new RunningServer(_servers.Count + 1, handle, baseUrl);
        _servers.Add(server);

        // Same evidence trail as run(): a started server is something the harness watched happen,
        // and how_to_run checks the client's instructions against commands actually executed.
        RecordEvidence(Trace($"$ {command}   (started with serve)",
            $"listening on {baseUrl}", handle.OutputSinceLastRead()), command);

        return new ToolOutcome(
            $"Server {server.Id} is up: {baseUrl} (started with `{command}`). "
            + "Send requests with http(); it keeps running until you stop it or your run ends.");
    }

    /// <summary>
    /// What the repo says is actually startable, appended to a failed serve. A wrong path fails
    /// as a build error, which reads like a broken project rather than a bad argument — so the
    /// refusal has to carry the correction, or the agent has no way back.
    /// </summary>
    private string StartupHint()
    {
        if (Discover(_jail.Root) is not { } target) return "";
        return $"\n\nThe startup project in this checkout is `{target.ProjectPath}` — "
             + $"serve(command: \"dotnet run --project {target.ProjectPath}\")"
             + (target.Url is { } url ? $", expected on {url}." : ".");
    }

    /// <summary>Stops one server, or all of them, and hands back what it printed on the way out.</summary>
    private ToolOutcome StopServer(ToolCall call)
    {
        if (_servers.Count == 0) return new ToolOutcome("ERROR: no server is running; serve() starts one.");

        var targets = call.OptionalInt("server") is { } id
            ? _servers.Where(s => s.Id == id).ToList()
            : [.. _servers];
        if (targets.Count == 0)
            return new ToolOutcome($"ERROR: no server {call.OptionalInt("server")}. Running: " +
                string.Join(", ", _servers.Select(s => s.Id)));

        var report = new StringBuilder();
        foreach (var server in targets)
        {
            var remaining = server.Handle.OutputSinceLastRead();
            server.Handle.Dispose();
            _servers.Remove(server);
            report.AppendLine($"Server {server.Id} ({server.BaseUrl}) stopped.");
            if (remaining.Length > 0)
                report.AppendLine("--- final output ---").AppendLine(Tail(remaining, MaxStartupOutputChars));
        }
        return new ToolOutcome(Truncate(report.ToString().TrimEnd()));
    }

    /// <summary>
    /// One HTTP request, made by the harness rather than by a program the agent wrote. That is the
    /// point: the request and the response are captured verbatim by trusted code, so a bug filed
    /// from them carries a trace nobody could have narrated — the same rule run() already enforces.
    /// </summary>
    private async Task<ToolOutcome> HttpAsync(ToolCall call, CancellationToken ct)
    {
        // No live server means the failure is QA's, not the app's, and it must not become
        // evidence: "the API did not answer" is only a defect if something was there to answer.
        if (_servers.Count == 0)
            return new ToolOutcome(
                "ERROR: no server is running, so there is nothing to send a request to. Start the app "
                + "with serve() first — a request that fails because you never started it is not a defect.");

        var target = _servers[^1];
        if (ResolveUrl(call.Arg("url"), target.BaseUrl) is not { } uri)
            return new ToolOutcome(
                $"ERROR: '{call.Arg("url")}' is not a path or an http(s) address on this machine. Use a path "
                + $"like /api/things against {target.BaseUrl}, or that URL in full. http() reaches only "
                + "servers you started here; it is for testing the project, not for calling the internet.");

        var method = new HttpMethod((call.Optional("method") ?? "GET").Trim().ToUpperInvariant());
        var body = call.Optional("body");
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, call.Optional("content_type") ?? "application/json");
        if (ApplyHeaders(request, call.Optional("headers")) is { } headerError)
            return new ToolOutcome($"ERROR: {headerError}");

        var sent = new StringBuilder($"> {method} {uri}");
        if (body is not null) sent.Append('\n').Append("> ").Append(Collapse(body));

        var clock = Stopwatch.StartNew();
        string received;
        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var head = new StringBuilder($"< HTTP {(int)response.StatusCode} {response.ReasonPhrase} ({clock.ElapsedMilliseconds} ms)");
            // Headers in full: the architecture asks the Principal to expose otherwise-invisible
            // behaviour through response headers (an X-Cache, say), so they are often the contract
            // under test rather than noise around it.
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                head.Append($"\n< {name}: {string.Join(", ", values)}");
            received = head.Append("\n\n").Append(Tail(text, MaxBodyChars)).ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            received = $"< no response within {HttpTimeout.TotalSeconds:0}s — the request timed out.";
        }
        catch (HttpRequestException ex)
        {
            // A live server that refuses or drops the connection is real observed behaviour, so it
            // is recorded like any other response rather than treated as the tool failing.
            received = $"< request failed: {ex.Message}";
        }

        var serverLog = target.Handle.OutputSinceLastRead();
        var trace = Trace(sent.ToString(), received,
            serverLog.Length > 0 ? $"--- server {target.Id} log during this request ---\n{serverLog.TrimEnd()}" : null);

        // Exchange first, evidence second: file_bug attaches exactly what is shown here.
        RecordEvidence(trace, command: null);
        if (target.Handle.HasExited)
            trace += $"\n\n(Server {target.Id} is no longer running — it exited with code {target.Handle.ExitCode}.)";
        return new ToolOutcome(Truncate(trace));
    }

    /// <summary>
    /// The base URL a server announced, preferring plain http and honouring an explicitly
    /// requested port when the server binds more than one.
    /// </summary>
    private static string? AnnouncedUrl(string output, int? port)
    {
        var urls = ListeningUrl().Matches(output)
            .Select(m => (
                // 0.0.0.0 means "every interface", which is not an address anything can be sent
                // to. Rewrite it to loopback, or every later request would be refused for
                // pointing at a non-loopback host the server never meant to name.
                Url: m.Value.TrimEnd('/').Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal),
                Port: int.Parse(m.Groups[2].Value),
                Secure: m.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (urls.Count == 0) return null;

        var matching = port is { } p ? urls.Where(u => u.Port == p).ToList() : urls;
        if (matching.Count == 0) return null;
        // http over https: a dev server usually offers both, and the plain one cannot fail on a
        // certificate. Deliberate, not incidental — QA is testing the app, not its TLS setup.
        return matching.FirstOrDefault(u => !u.Secure, matching[0]).Url;
    }

    /// <summary>Whether anything is accepting connections on a loopback port yet.</summary>
    private static async Task<bool> PortAcceptsAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync("127.0.0.1", port, ct).ConfigureAwait(false);
            return probe.Connected;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SocketException) { return false; }
    }

    /// <summary>A path against the running server, or an absolute URL — loopback either way.</summary>
    /// <remarks>
    /// The scheme decides what counts as absolute, not the shape: on Unix a rooted path like
    /// `/api/things` — the form QA will use most — parses perfectly well as an absolute
    /// `file://` URI, so a shape test would send every request to the filesystem.
    /// </remarks>
    private static Uri? ResolveUrl(string url, string baseUrl)
    {
        var text = url.Trim();
        var resolved = Uri.TryCreate(text, UriKind.Absolute, out var parsed) && IsWeb(parsed)
            ? parsed
            : Uri.TryCreate(new Uri(baseUrl), text, out var relative) ? relative : null;
        return resolved is { IsLoopback: true } && IsWeb(resolved) ? resolved : null;
    }

    private static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https";

    /// <summary>
    /// `Name: value` per line. Request and content headers are two different collections in
    /// HttpClient and a model has no reason to know which is which, so try one then the other.
    /// </summary>
    private static string? ApplyHeaders(HttpRequestMessage request, string? headers)
    {
        foreach (var line in (headers ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                return $"header '{line}' is not in `Name: value` form (one header per line).";

            var (name, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
            if (request.Headers.TryAddWithoutValidation(name, value)) continue;
            if (request.Content?.Headers.TryAddWithoutValidation(name, value) is true) continue;
            return $"header '{name}' was rejected — a content header needs a body to attach to.";
        }
        return null;
    }

    /// <summary>
    /// Makes an observation the harness captured usable as proof: file_bug attaches this verbatim,
    /// and how_to_run accepts only commands that were really executed.
    /// </summary>
    private void RecordEvidence(string trace, string? command)
    {
        _lastRunTrace = trace;
        if (command is not null) _ranCommands.Add(command);
    }

    private static string Trace(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p))).TrimEnd();

    /// <summary>Keeps the END of long output: a stack trace's cause is at the bottom, not the top.</summary>
    private static string Tail(string text, int max)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length <= max
            ? trimmed
            : $"... [{trimmed.Length - max} earlier chars omitted]\n" + trimmed[^max..];
    }

    /// <summary>
    /// Works out which project QA should start, by reading the repo rather than letting the
    /// model guess a path. Returns null when the checkout holds nothing runnable.
    /// </summary>
    /// <remarks>
    /// A guess is not merely unreliable, it is undetectable: `dotnet run --project` on a path
    /// that does not exist fails with a build error, which reads like a broken project rather
    /// than a bad argument. There is no fixed layout to fall back on either — one project here
    /// is `src/BillSplitter.Web`, another is `src/Weatherboard` — so the answer has to be derived.
    ///
    /// Three signals, in the order a person would use them:
    /// 1. **Runnable, not a test.** A Web/BlazorWebAssembly SDK, or OutputType Exe. A test
    ///    project can be Exe too, so Microsoft.NET.Test.Sdk excludes it — running it would run
    ///    the tests, not the product.
    /// 2. **Nothing depends on it.** The startup project is the root of the reference graph.
    ///    References from TEST projects are ignored on purpose: the test project references the
    ///    web app, so counting them would make the web app look depended-upon and leave no root
    ///    at all — the one subtlety that decides whether this works.
    /// 3. **launchSettings for the URL.** `.csproj` cannot say which port the app binds;
    ///    Properties/launchSettings.json can, and it is what `dotnet run` itself reads.
    /// </remarks>
    public static RunTarget? Discover(string checkoutDir)
    {
        if (!Directory.Exists(checkoutDir)) return null;

        var projects = Directory.EnumerateFiles(checkoutDir, "*.csproj", SearchOption.AllDirectories)
            .Select(Describe)
            .ToList();

        var candidates = projects.Where(p => p.Runnable && !p.IsTest).ToList();
        if (candidates.Count == 0) return null;

        // Only non-test projects can disqualify a candidate from being the root.
        var dependedUpon = projects
            .Where(p => !p.IsTest)
            .SelectMany(p => p.References)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roots = candidates.Where(p => !dependedUpon.Contains(p.Path)).ToList();

        // No root at all means a reference cycle or an unusual layout; fall back to the plain
        // candidate list rather than refusing to answer. Shortest path breaks any remaining tie.
        var ranked = (roots.Count > 0 ? roots : candidates)
            .OrderBy(p => p.Path.Length)
            .ToList();

        var chosen = ranked[0];
        return new RunTarget(
            Relative(checkoutDir, chosen.Path),
            UrlFromLaunchSettings(chosen.Path),
            [.. ranked.Skip(1).Select(p => Relative(checkoutDir, p.Path))]);
    }

    /// <summary>One project's three facts. A file that will not parse is simply not runnable.</summary>
    private static (string Path, bool Runnable, bool IsTest, List<string> References) Describe(string csproj)
    {
        XDocument doc;
        try { doc = XDocument.Load(csproj); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return (csproj, false, false, []);
        }

        var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
        var web = sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase)
               || sdk.Contains("Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);
        var exe = string.Equals(
            doc.Descendants("OutputType").FirstOrDefault()?.Value, "Exe", StringComparison.OrdinalIgnoreCase);

        var isTest = doc.Descendants("PackageReference")
            .Select(p => p.Attribute("Include")?.Value ?? "")
            .Any(id => id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

        // Include paths are written with Windows separators by every template; resolve them to
        // absolute paths so the graph compares like with like whatever wrote the file.
        var dir = Path.GetDirectoryName(csproj)!;
        var references = doc.Descendants("ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        return (Path.GetFullPath(csproj), web || exe, isTest, references);
    }

    /// <summary>
    /// The URL `dotnet run` would serve on, from the profile it actually uses. Profiles for IIS
    /// or Docker are skipped (commandName is not "Project"), and http is preferred over https so
    /// a development certificate cannot fail the first request.
    /// </summary>
    private static string? UrlFromLaunchSettings(string csproj)
    {
        var path = Path.Combine(Path.GetDirectoryName(csproj)!, "Properties", "launchSettings.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return null;

            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("commandName", out var command)
                    && !string.Equals(command.GetString(), "Project", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!profile.Value.TryGetProperty("applicationUrl", out var urls)) continue;

                var listed = (urls.GetString() ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (listed.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    is { } plain) return plain;
                if (listed.Length > 0) return listed[0];
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { /* no URL to be had */ }
        return null;
    }

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>`Now listening on: http://localhost:5161` and every equivalent a server prints.</summary>
    [GeneratedRegex(@"\b(https?)://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0):(\d{2,5})(?:/\S*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListeningUrl();

    /// <summary>
    /// Nothing an agent started may outlive its run. Called from the agent loop's finally, so a
    /// budget stop, a crash or a Ctrl-C leaves no orphaned server holding a port — the failure
    /// that would otherwise poison every following QA round on the same machine.
    /// </summary>
    public void Dispose()
    {
        foreach (var server in _servers) server.Handle.Dispose();
        _servers.Clear();
    }
}
