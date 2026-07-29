using Forge.Core.Configuration;
using Forge.Core.Secrets;
using Forge.Core.Tools;

namespace Forge.Tests;

public class EnvFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"forge-env-{Guid.NewGuid():N}");
    private readonly List<string> _touched = [];

    public EnvFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var name in _touched) Environment.SetEnvironmentVariable(name, null);
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string contents)
    {
        var path = Path.Combine(_dir, "forge_env");
        File.WriteAllText(path, contents);
        return path;
    }

    private void Track(params string[] names) => _touched.AddRange(names);

    [Fact]
    public void Parses_comments_blanks_export_prefixes_and_quotes()
    {
        var entries = EnvFile.Parse("""
            # a comment
            ANTHROPIC_API_KEY=sk-ant-api01-plain

            export EXPORTED_KEY=exported-value
            QUOTED_KEY="value with spaces"
            SINGLE_QUOTED='single'
            SPACED_KEY = padded
            NOT_A_PAIR
            =novalue
            URL=https://example.com/path?a=b
            """);

        Assert.Equal([
            ("ANTHROPIC_API_KEY", "sk-ant-api01-plain"),
            ("EXPORTED_KEY", "exported-value"),
            ("QUOTED_KEY", "value with spaces"),
            ("SINGLE_QUOTED", "single"),
            ("SPACED_KEY", "padded"),
            ("URL", "https://example.com/path?a=b"), // only the first '=' splits
        ], entries);
    }

    [Fact]
    public void Loads_into_the_process_environment()
    {
        Track("FORGE_TEST_LOADED");
        var path = Write("FORGE_TEST_LOADED=from-file\n");

        Assert.Equal(1, EnvFile.Load(path));
        Assert.Equal("from-file", Environment.GetEnvironmentVariable("FORGE_TEST_LOADED"));
    }

    [Fact]
    public void The_real_environment_wins_so_a_one_off_override_still_works()
    {
        Track("FORGE_TEST_PRESET");
        Environment.SetEnvironmentVariable("FORGE_TEST_PRESET", "from-shell");
        var path = Write("FORGE_TEST_PRESET=from-file\n");

        Assert.Equal(0, EnvFile.Load(path));
        Assert.Equal("from-shell", Environment.GetEnvironmentVariable("FORGE_TEST_PRESET"));
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        Assert.Equal(0, EnvFile.Load(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void Template_is_owner_only_and_never_clobbers_existing_credentials()
    {
        var path = Path.Combine(_dir, "forge_env");

        Assert.True(EnvFile.CreateTemplate(path));
        Assert.Contains("ANTHROPIC_API_KEY=", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        File.WriteAllText(path, "ANTHROPIC_API_KEY=sk-ant-api01-real\n");
        Assert.False(EnvFile.CreateTemplate(path));
        Assert.Contains("sk-ant-api01-real", File.ReadAllText(path));
    }
}

/// <summary>
/// The guarantee that makes a credentials file safe: whatever Forge holds in its
/// own environment must not reach a process an agent asked for.
/// </summary>
public class ToolExecutorEnvironmentTests : IDisposable
{
    private const string Canary = "FORGE_TEST_CANARY_SECRET";

    private readonly string _jail = Path.Combine(Path.GetTempPath(), $"forge-envjail-{Guid.NewGuid():N}");

    public ToolExecutorEnvironmentTests()
    {
        Directory.CreateDirectory(_jail);
        Environment.SetEnvironmentVariable(Canary, "sk-ant-should-never-leak");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api01-also-should-never-leak");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-oai-should-never-leak");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "goog-should-never-leak");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Canary, null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", null);
        Directory.Delete(_jail, recursive: true);
    }

    private ToolExecutor Executor(IReadOnlyDictionary<string, string>? environment = null) =>
        new(_jail, ["env", "sh"], new SecretsVault(Path.Combine(_jail, ".vault")), environment: environment);

    [Fact]
    public async Task Forges_own_credentials_do_not_reach_a_process_the_agent_ran()
    {
        var result = await Executor().RunAsync("env");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(Canary, result.Stdout);
        // Every provider key rides the same allowlist, so every one must be invisible.
        Assert.DoesNotContain("ANTHROPIC_API_KEY", result.Stdout);
        Assert.DoesNotContain("OPENAI_API_KEY", result.Stdout);
        Assert.DoesNotContain("GEMINI_API_KEY", result.Stdout);
        Assert.DoesNotContain("sk-ant-", result.Stdout);
        Assert.DoesNotContain("sk-oai-", result.Stdout);
        Assert.DoesNotContain("goog-", result.Stdout);
    }

    [Fact]
    public async Task The_toolchain_still_gets_what_it_needs()
    {
        var result = await Executor().RunAsync("env");

        Assert.Contains("PATH=", result.Stdout);
        // HOME is redirected into the jail so a child cannot read ~/forge_env.
        Assert.Contains($"HOME={_jail}", result.Stdout);
        Assert.DoesNotContain(
            $"HOME={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\n", result.Stdout);
    }

    [Fact]
    public async Task Explicitly_supplied_variables_are_passed_through()
    {
        var result = await Executor(new Dictionary<string, string> { ["NUGET_PACKAGES"] = "/tmp/nuget" })
            .RunAsync("env");

        Assert.Contains("NUGET_PACKAGES=/tmp/nuget", result.Stdout);
    }
}
