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

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"forge-envhome-{Guid.NewGuid():N}");

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
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private ToolExecutor Executor(IReadOnlyDictionary<string, string>? environment = null) =>
        new(_jail, ["env", "sh"], new SecretsVault(Path.Combine(_jail, ".vault")),
            environment: environment, agentHome: _home);

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
        // HOME is redirected to the agent home so a child cannot read ~/forge_env.
        Assert.Contains($"HOME={_home}", result.Stdout);
        Assert.DoesNotContain(
            $"HOME={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}\n", result.Stdout);
    }

    /// <summary>
    /// The caches a toolchain writes to HOME must not land in the checkout the agent commits,
    /// so HOME is a directory of its own and the jail stays as git left it.
    /// </summary>
    [Fact]
    public async Task A_commands_home_is_outside_the_jail_and_the_harness_creates_it()
    {
        var result = await Executor().RunAsync("sh -c 'echo $HOME > $HOME/marker'");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(_home, "marker")));
        Assert.False(File.Exists(Path.Combine(_jail, "marker")));
    }

    [Fact]
    public async Task Explicitly_supplied_variables_are_passed_through()
    {
        var result = await Executor(new Dictionary<string, string> { ["NUGET_PACKAGES"] = "/tmp/nuget" })
            .RunAsync("env");

        Assert.Contains("NUGET_PACKAGES=/tmp/nuget", result.Stdout);
    }
}

/// <summary>
/// The other half of the same guarantee: the harness re-runs the code an agent wrote — the
/// build, the tests, the application, git — and those starts must scrub exactly as the agent's
/// own commands do.
/// </summary>
public class HarnessChildProcessTests : IDisposable
{
    private const string Canary = "FORGE_TEST_HARNESS_CANARY";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"forge-childproc-{Guid.NewGuid():N}");

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"forge-childhome-{Guid.NewGuid():N}");

    public HarnessChildProcessTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(Canary, "sk-ant-should-never-leak");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api01-also-should-never-leak");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Canary, null);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Directory.Delete(_dir, recursive: true);
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    [Fact]
    public void Forges_own_credentials_do_not_reach_a_process_the_harness_started()
    {
        var psi = ChildProcess.Create("dotnet", _dir, _home);

        Assert.False(psi.Environment.ContainsKey(Canary));
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("OPENAI_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("GEMINI_API_KEY"));
    }

    [Fact]
    public void The_toolchain_still_gets_what_it_needs_and_a_home_that_is_not_the_users()
    {
        var psi = ChildProcess.Create("dotnet", _dir, _home);

        Assert.True(psi.Environment.ContainsKey("PATH"));
        Assert.Equal(_home, psi.Environment["HOME"]);
        Assert.True(Directory.Exists(_home));
        Assert.NotEqual(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), psi.Environment["HOME"]);
    }

    [Fact]
    public void A_caller_with_no_project_in_scope_still_gets_a_scrubbed_environment()
    {
        var psi = ChildProcess.Create("git", _dir);

        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal(ChildProcess.HarnessHome, psi.Environment["HOME"]);
    }

    [Fact]
    public void What_one_child_needs_is_added_on_top_of_the_allowlist()
    {
        var psi = ChildProcess.Create("dotnet", _dir, _home,
            new Dictionary<string, string> { ["FORGE_ACCEPTANCE_BASE_URL"] = "http://127.0.0.1:5000" });

        Assert.Equal("http://127.0.0.1:5000", psi.Environment["FORGE_ACCEPTANCE_BASE_URL"]);
    }

    /// <summary>
    /// The guarantee is only as good as the number of places that can bypass it, so the type
    /// is the only one in Forge.Core allowed to construct a ProcessStartInfo.
    /// </summary>
    [Fact]
    public void No_other_code_builds_a_process_start_info_of_its_own()
    {
        var core = Path.Combine(SourceRoot(), "src", "Forge.Core");
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "ChildProcess.cs")
            .Where(f => File.ReadAllText(f).Contains("new ProcessStartInfo", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(core, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These start a process without the scrubbed environment: " + string.Join(", ", offenders));
    }

    /// <summary>Walks up from the test binary to the directory holding Forge.sln.</summary>
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Forge.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Forge.sln not found above the test binary.");
    }
}
