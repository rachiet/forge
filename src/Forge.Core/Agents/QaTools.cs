using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>The application to start, read out of the repo.</summary>
/// <param name="ProjectPath">Repo-relative path of the .csproj to run.</param>
/// <param name="Url">The http URL from launchSettings, or null when the project declares none.</param>
/// <param name="Alternatives">Other runnable projects, when the repo has more than one.</param>
public sealed record RunTarget(string ProjectPath, string? Url, IReadOnlyList<string> Alternatives);

/// <summary>
/// QA's own tools, in the same toolset as the rest: serve() starts an application and leaves
/// it running, stop_server() ends it, and http() sends a single request to it. Unlike run(),
/// which waits for a process to exit, serve() owns a process that never does. Both record what
/// the harness watched happen as evidence file_bug can attach.
/// </summary>
public sealed partial class AgentToolset : IDisposable
{
    /// <summary>How long serve() waits for a server to bind before giving up on it.</summary>
    private static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a single http() request may take before it is abandoned.</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often serve() re-checks whether the server is listening yet.</summary>
    private const int ReadyPollMs = 250;

    /// <summary>How much of a response body is shown to the agent.</summary>
    private const int MaxBodyChars = 4_000;

    /// <summary>How much of a server's startup output is shown to the agent.</summary>
    private const int MaxStartupOutputChars = 3_000;

    /// <summary>
    /// Servers this instance started, in start order; the id an agent names is the index + 1.
    /// More than one may run at a time.
    /// </summary>
    private readonly List<RunningServer> _servers = [];

    /// <summary>One served process.</summary>
    /// <param name="Id">The number the agent refers to it by.</param>
    /// <param name="Handle">The running process.</param>
    /// <param name="BaseUrl">The address the harness confirmed it is listening on.</param>
    private sealed record RunningServer(int Id, ServerHandle Handle, string BaseUrl);

    /// <summary>
    /// Shared by every http() call, so connections are pooled. Certificate validation is off and
    /// redirects are not followed: the only reachable hosts are loopback ones the harness
    /// started, and a redirect is part of the contract under test.
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
    private static IEnumerable<KeyValuePair<string, ToolDoc>> QaCatalogue =>
        new Dictionary<string, ToolDoc>(StringComparer.Ordinal)
        {
            ["serve"] = new(
                "start a server and LEAVE it running. Use this, not run(), for anything that does not "
              + "exit on its own: run() waits for the process to finish and kills it at the timeout, so "
              + "it can never host something you then send requests to. The harness waits until the "
              + "server is really listening, returns its base URL, and stops it when your run ends.",
                ToolDoc.Required("command", "what starts it, e.g. `dotnet run --project src/App/App.csproj`."),
                ToolDoc.Optional("port", "the port it binds. Give this if the server does not print its "
                                       + "URL at startup — the harness then checks the socket directly."),
                ToolDoc.Optional("cwd", "directory to start it in, relative to your workspace root."),
                ToolDoc.Optional("ready_timeout", "seconds to wait for it to start listening. Default 60.")),

            ["stop_server"] = new("stop a server started with serve and see its final output. Not required "
                                + "at the end of your run; the harness stops them for you.",
                ToolDoc.Optional("server", "which server id to stop. Defaults to all of them.")),

            ["http"] = new(
                "send ONE request to a server you started with serve, and see the real status line, "
              + "response headers and body. Anything the server logged meanwhile comes back with the "
              + "response, so a 500 arrives with its stack trace. Loopback addresses only — this tests "
              + "your app, not the internet.",
                ToolDoc.Required("url", "a path like `/api/things` to use the server's own base URL, or a "
                                      + "full loopback URL."),
                ToolDoc.Optional("method", "GET (default), POST, PUT, PATCH, DELETE."),
                ToolDoc.Optional("body", "the request body, sent as application/json unless you say otherwise."),
                ToolDoc.Optional("headers", "one `Name: value` per line."),
                ToolDoc.Optional("content_type", "overrides the body's content type.")),
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
    /// Starts a server and waits until it is accepting connections, then registers it and returns
    /// its base URL. Readiness comes from the URL the server announces in its own output, or from
    /// opening a socket to an explicitly given port. Returns an error if it exits during startup
    /// or never starts listening.
    /// </summary>
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
            // Exited during startup: its output says why.
            if (handle.HasExited)
            {
                var exitTrace = Trace(
                    $"$ {command}   (started with serve)",
                    $"exited during startup with code {handle.ExitCode}",
                    handle.Output);
                handle.Dispose();
                // Usable as bug evidence, but not as a command how_to_run may quote.
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
            // Alive but unreachable: kill it rather than leave it holding the port.
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

        // Records the command as one really executed, which how_to_run is checked against.
        RecordEvidence(Trace($"$ {command}   (started with serve)",
            $"listening on {baseUrl}", handle.OutputSinceLastRead()), command);

        return new ToolOutcome(
            $"Server {server.Id} is up: {baseUrl} (started with `{command}`). "
            + "Send requests with http(); it keeps running until you stop it or your run ends.");
    }

    /// <summary>
    /// The startup project discovered from the repo, phrased as the serve() call to make, for
    /// appending to a failed serve. Empty when the repo has nothing runnable.
    /// </summary>
    private string StartupHint()
    {
        if (Discover(_jail.Root) is not { } target) return "";
        return $"\n\nThe startup project in this checkout is `{target.ProjectPath}` — "
             + $"serve(command: \"dotnet run --project {target.ProjectPath}\")"
             + (target.Url is { } url ? $", expected on {url}." : ".");
    }

    /// <summary>Stops the named server, or all of them, and returns their final output.</summary>
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
    /// Sends one request to the most recently started server and returns the status line, every
    /// response header, the body, and anything the server logged while handling it. The whole
    /// exchange is recorded as evidence file_bug can attach.
    /// </summary>
    private async Task<ToolOutcome> HttpAsync(ToolCall call, CancellationToken ct)
    {
        // With no server running there is nothing to observe, so nothing is recorded as evidence.
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
            // Headers in full: they are often the contract under test, not noise around it.
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
            // A refused or dropped connection is observed behaviour, recorded like any response.
            received = $"< request failed: {ex.Message}";
        }

        var serverLog = target.Handle.OutputSinceLastRead();
        var trace = Trace(sent.ToString(), received,
            serverLog.Length > 0 ? $"--- server {target.Id} log during this request ---\n{serverLog.TrimEnd()}" : null);

        // file_bug attaches exactly what is shown here.
        RecordEvidence(trace, command: null);
        if (target.Handle.HasExited)
            trace += $"\n\n(Server {target.Id} is no longer running — it exited with code {target.Handle.ExitCode}.)";
        return new ToolOutcome(Truncate(trace));
    }

    /// <summary>
    /// The base URL a server announced in its output, or null if it has announced none. Prefers
    /// plain http over https, and honours an explicitly requested port when it bound several.
    /// </summary>
    private static string? AnnouncedUrl(string output, int? port)
    {
        var urls = ListeningUrl().Matches(output)
            .Select(m => (
                // 0.0.0.0 is not an address requests can be sent to; use loopback instead.
                Url: m.Value.TrimEnd('/').Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal),
                Port: int.Parse(m.Groups[2].Value),
                Secure: m.Groups[1].Value.Equals("https", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (urls.Count == 0) return null;

        var matching = port is { } p ? urls.Where(u => u.Port == p).ToList() : urls;
        if (matching.Count == 0) return null;
        // Plain http where the server offers both, so no request can fail on a certificate.
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

    /// <summary>
    /// Resolves a path against the running server's base URL, or accepts a full http(s) address.
    /// Returns null for anything that is not a loopback web address.
    /// </summary>
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
    /// Adds `Name: value` headers, one per line, to the request or its content. Returns an error
    /// message for a malformed or unacceptable header, or null when all were applied.
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
    /// Records what the harness just observed as the evidence file_bug will attach, and the
    /// command as one how_to_run may quote when it started something.
    /// </summary>
    private void RecordEvidence(string trace, string? command)
    {
        _lastRunTrace = trace;
        if (command is not null) _ranCommands.Add(command);
    }

    /// <summary>Joins the non-empty parts of a trace with newlines.</summary>
    private static string Trace(params string?[] parts) =>
        string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p))).TrimEnd();

    /// <summary>Truncates to the last <paramref name="max"/> characters, where a failure's cause sits.</summary>
    private static string Tail(string text, int max)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length <= max
            ? trimmed
            : $"... [{trimmed.Length - max} earlier chars omitted]\n" + trimmed[^max..];
    }

    /// <summary>
    /// Finds the application to start by reading the repo. A project qualifies if it is runnable
    /// (a Web or BlazorWebAssembly SDK, or OutputType Exe) and is not a test project, and it is
    /// chosen if nothing else references it — references from test projects are ignored, since
    /// the test project references the app. The URL comes from launchSettings.json. Returns null
    /// when the checkout holds nothing runnable; ties break on the shortest path.
    /// </summary>
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

        // With no root — a reference cycle, or an unusual layout — fall back to every candidate.
        var ranked = (roots.Count > 0 ? roots : candidates)
            .OrderBy(p => p.Path.Length)
            .ToList();

        var chosen = ranked[0];
        return new RunTarget(
            Relative(checkoutDir, chosen.Path),
            UrlFromLaunchSettings(chosen.Path),
            [.. ranked.Skip(1).Select(p => Relative(checkoutDir, p.Path))]);
    }

    /// <summary>
    /// Reads one .csproj: its absolute path, whether it is runnable, whether it is a test project,
    /// and the projects it references. A file that will not parse is reported as not runnable.
    /// </summary>
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

        // Resolved to absolute paths, and Windows separators normalised, so the graph compares.
        var dir = Path.GetDirectoryName(csproj)!;
        var references = doc.Descendants("ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

        return (Path.GetFullPath(csproj), web || exe, isTest, references);
    }

    /// <summary>
    /// The URL from the project's launchSettings.json, or null if it declares none. Only profiles
    /// `dotnet run` itself uses are read (commandName "Project"), and http is preferred over https.
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

    /// <summary>A path relative to the checkout root, with forward slashes.</summary>
    private static string Relative(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>Matches a loopback URL in a server's output, capturing its scheme and port.</summary>
    [GeneratedRegex(@"\b(https?)://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0):(\d{2,5})(?:/\S*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListeningUrl();

    /// <summary>
    /// Stops every server this instance started. Called however the run ends, so no server
    /// outlives it holding a port.
    /// </summary>
    public void Dispose()
    {
        foreach (var server in _servers) server.Handle.Dispose();
        _servers.Clear();
    }
}
