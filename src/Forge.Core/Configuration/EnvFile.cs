namespace Forge.Core.Configuration;

/// <summary>
/// Loads Forge's own credentials — the LLM provider keys — from a dotenv-style file into the
/// process environment at startup. These are the harness's keys, not client project secrets,
/// which live in the encrypted vault. The file sits outside the data root, so credentials do
/// not travel with the repos and databases it holds.
/// </summary>
public static class EnvFile
{
    public const string EnvVar = "FORGE_ENV";

    /// <summary>FORGE_ENV if set, else ~/forge_env.</summary>
    public static string Resolve() =>
        Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "forge_env");

    /// <summary>
    /// Loads the file into the process environment and returns how many variables it applied.
    /// A variable already set in the real environment wins. A missing file is not an error.
    /// </summary>
    public static int Load(string? path = null)
    {
        path ??= Resolve();
        if (!File.Exists(path)) return 0;

        var applied = 0;
        foreach (var (key, value) in Parse(File.ReadAllText(path)))
        {
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 }) continue;
            Environment.SetEnvironmentVariable(key, value);
            applied++;
        }
        return applied;
    }

    /// <summary>
    /// Parses one line: KEY=VALUE, with # comments, blank lines, an optional `export ` prefix
    /// and optional surrounding quotes. No interpolation.
    /// </summary>
    internal static IReadOnlyList<(string Key, string Value)> Parse(string text)
    {
        List<(string, string)> entries = [];
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (key.Length > 0) entries.Add((key, value));
        }
        return entries;
    }

    /// <summary>Writes a starter file with owner-only permissions, and never overwrites one.</summary>
    public static bool CreateTemplate(string? path = null)
    {
        path ??= Resolve();
        if (File.Exists(path)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            # Forge credentials — the harness's own key, not client project secrets.
            # Client secrets go in the encrypted vault: `forge secrets set NAME`.
            #
            # This file is read at startup and is never exposed to agents: the tool
            # executor builds a scrubbed environment for every command it runs.
            #
            # Set the key for whichever provider llm.json selects — you only need one.
            # The provider defaults to anthropic; change it in <data root>/llm.json or
            # with FORGE_LLM_PROVIDER.

            # Claude API key (sk-ant-api...), sent as the x-api-key header.
            ANTHROPIC_API_KEY=

            # OpenAI API key (sk-...), sent as an Authorization: Bearer header.
            OPENAI_API_KEY=

            # Google AI Studio key, sent as the x-goog-api-key header.
            GEMINI_API_KEY=

            """);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }
}
