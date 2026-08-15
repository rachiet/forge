using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Core.Ci;

/// <summary>
/// Checks the files a browser reads, which the compiler never does: they are copied as content,
/// so one that does not parse ships and fails only when someone loads the page.
///
/// Four rules, each with a true or false answer and no opinion attached — JavaScript parses,
/// JSON parses, and an HTML page's inline scripts parse. Markup
/// itself is deliberately not judged: HTML defines error recovery, so every document "parses"
/// and any verdict beyond these would be a lint the harness could be argued out of.
///
/// A parser that is not installed is a skip, never a failure, matching the rest of CI.
/// </summary>
public static partial class StaticFileCheck
{
    /// <summary>How long one parser invocation may take before it is killed and skipped.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Directories holding build output, dependencies or git state rather than source.</summary>
    private static readonly string[] Ignored = [".git", "bin", "obj", "node_modules"];

    /// <summary>
    /// Checks every .js, .json and .html in the workspace. Returns null when they are all sound,
    /// or the failures as one message written for the engineer to act on: the repo-relative path
    /// and the parser's own text, nothing else.
    /// </summary>
    public static string? Check(string workspaceDir)
    {
        var failures = new StringBuilder();
        foreach (var file in SourceFiles(workspaceDir))
        {
            var problem = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".js" => JavaScriptProblem(file),
                ".json" => JsonProblem(file),
                ".html" or ".htm" => HtmlProblem(workspaceDir, file),
                _ => null,
            };
            if (problem is null) continue;

            failures.Append($"{Relative(workspaceDir, file)}\n{problem}\n\n");
        }

        return failures.Length == 0 ? null : $"""
            These files are read by the browser exactly as written, so the compiler never sees
            them and this check does instead.

            {failures.ToString().TrimEnd()}
            """;
    }

    /// <summary>Every .js, .json and .html in the repo, excluding build output and git state.</summary>
    private static IEnumerable<string> SourceFiles(string workspaceDir) =>
        Directory.EnumerateFiles(workspaceDir, "*.*", SearchOption.AllDirectories)
            .Where(f => !Ignored.Any(dir =>
                f.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                 .Any(part => string.Equals(part, dir, StringComparison.OrdinalIgnoreCase))))
            .Where(f => Path.GetExtension(f).ToLowerInvariant()
                is ".js" or ".json" or ".html" or ".htm")
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>A path as the engineer wrote it, relative to the workspace and with / separators.</summary>
    private static string Relative(string workspaceDir, string file) =>
        Path.GetRelativePath(workspaceDir, file).Replace('\\', '/');

    /// <summary>
    /// The syntax error node reports for a JavaScript file, or null when it parses. `node --check`
    /// compiles without running it, so a script with side effects is safe to check.
    /// </summary>
    private static string? JavaScriptProblem(string file) =>
        Run("node", "--check", file) is { Available: true, ExitCode: not 0 } failed
            ? failed.Output
            : null;

    /// <summary>The parse error for a JSON file, or null when it parses. Uses the runtime's reader.</summary>
    private static string? JsonProblem(string file)
    {
        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(file));
            return null;
        }
        catch (JsonException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// What is wrong with an HTML page: an inline script that does not parse. Silent in a
    /// browser — the page renders and simply does nothing — which is why it is worth a
    /// mechanical check. The paths the page links to are not judged; whether one loads is a
    /// failed request on a rendered page, reported by `PageCheck`.
    /// </summary>
    private static string? HtmlProblem(string workspaceDir, string file)
    {
        var html = File.ReadAllText(file);
        var problems = new StringBuilder();

        foreach (var script in InlineScripts(html))
        {
            var scratch = Path.Combine(Path.GetTempPath(), $"forge-inline-{Guid.NewGuid():N}.js");
            File.WriteAllText(scratch, script);
            try
            {
                if (JavaScriptProblem(scratch) is { } broken)
                    problems.Append($"  has an inline <script> that does not parse:\n{broken}\n");
            }
            finally
            {
                File.Delete(scratch);
            }
        }

        return problems.Length == 0 ? null : problems.ToString().TrimEnd();
    }

    /// <summary>The bodies of script blocks written into the page, ignoring those that load a file.</summary>
    private static IEnumerable<string> InlineScripts(string html) =>
        ScriptBlockPattern().Matches(html)
            .Where(m => !m.Groups["attrs"].Value.Contains("src=", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups["body"].Value)
            .Where(body => body.Trim().Length > 0);

    /// <summary>What running a parser produced, and whether the parser was installed at all.</summary>
    private sealed record ParserRun(bool Available, int ExitCode, string Output);

    /// <summary>
    /// Runs one parser and captures its output. A missing binary or one that outlives the timeout
    /// comes back unavailable rather than failed, so a machine without node skips the check
    /// instead of blocking every task on it.
    /// </summary>
    private static ParserRun Run(string binary, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ParserRun(Available: false, 0, "");
        }
        if (process is null) return new ParserRun(Available: false, 0, "");

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return new ParserRun(Available: false, 0, "");
            }

            var output = new StringBuilder(stdout.GetAwaiter().GetResult());
            var err = stderr.GetAwaiter().GetResult();
            if (err.Length > 0) output.Append('\n').Append(err);
            return new ParserRun(Available: true, process.ExitCode, output.ToString().Trim());
        }
    }

    /// <summary>Matches a script element, capturing its attributes and its body separately.</summary>
    [GeneratedRegex("""<script(?<attrs>[^>]*)>(?<body>.*?)</script\s*>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlockPattern();
}
