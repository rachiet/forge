using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Secrets;
using Forge.Core.Ui;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// Choosing a theme is a selection from files Forge ships, applied to the client's repo with no
/// agent and no tokens. The PM can raise the picker but never picks.
/// </summary>
public class ThemePickerTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-theme-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly PromptLibrary _prompts = PromptLibrary.Resolve();

    public ThemePickerTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A repo with a runnable web project, which is what the kit installs into.</summary>
    private void SeedRunnableProject()
    {
        var seed = Path.Combine(_dataRoot, "seed");
        Git.Require(_paths.ProjectDir(Project), "clone", _paths.ProjectBareRepo(Project), seed);
        var app = Path.Combine(seed, "src", "App");
        Directory.CreateDirectory(Path.Combine(app, "wwwroot"));
        File.WriteAllText(Path.Combine(app, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(app, "wwwroot", "index.html"), "<h1>app</h1>");
        Git.Require(seed, "add", "-A");
        Git.Require(seed, "commit", "-m", "feat: the app");
        Git.Require(seed, "push", "origin", "master");
        Directory.Delete(seed, recursive: true);
    }

    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"master:{path}").Stdout;

    [Fact]
    public void Every_theme_the_kit_ships_is_a_tile_carrying_its_own_stylesheet()
    {
        var tiles = UiKit.ThemeTiles(_prompts);

        // The catalogue is the files on disk: adding a theme file adds a tile, with no list
        // anywhere to keep in step.
        Assert.Equal(UiKit.Themes(_prompts).Count, tiles.Count);
        foreach (var tile in tiles)
        {
            Assert.NotEmpty(tile.Summary);
            // The page rescopes these declarations onto the tile, so a tile shows the real theme.
            Assert.Contains(":root", tile.Css);
            Assert.Contains("--fg-light-surface", tile.Css);
        }

        // A tile is painted in one scheme at a time; `auto` is the viewer's setting, not a look.
        var modes = UiKit.ModeStylesheets(_prompts);
        Assert.Equal(["dark", "light"], modes.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_pm_can_raise_the_picker_but_has_no_way_to_choose_a_theme()
    {
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("offer_theme_choice"),
            ScriptedLlmClient.Tool("choose_theme", ("theme", "vivid"), ("mode", "dark")),
            ScriptedLlmClient.Tool("reply", ("message", "Pick whichever you like best.")));

        await new PmChat(
            _paths, Project, _conn,
            new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
            new SecretsVault(_paths.VaultDir), _prompts).SendAsync("Can I see some colour options?");

        var meta = new ProjectMetaRepository(_conn);
        Assert.True(ThemeOffer.Pending(meta));

        // choose_theme is the Principal's, at design time. The PM offering is not the PM deciding.
        Assert.Contains("no tool 'choose_theme' is available to you", llm.Observations(2));
        Assert.Equal(ThemeChoice.DefaultTheme, ThemeChoice.From(meta).Theme);
    }

    [Fact]
    public void Choosing_installs_the_theme_on_trunk_without_an_agent()
    {
        SeedRunnableProject();
        var choice = new ThemeChoice("vivid", "dark", "teal");

        var applied = AppearanceChange.Apply(_paths, Project, choice, _prompts);

        Assert.True(applied);
        // The stylesheet the application serves now carries the chosen theme and mode.
        var css = ShowFromTrunk("src/App/wwwroot/forge-ui/theme.css");
        Assert.Contains("Theme: vivid", css);
        Assert.Contains("Mode: dark", css);

        // Not a single token was spent doing it.
        Assert.Equal(0, new LedgerRepository(_conn).ProjectTotals().TokensIn);
    }

    [Fact]
    public void The_copy_the_client_runs_gets_the_new_theme_without_waiting_for_a_handover()
    {
        SeedRunnableProject();
        // The delivered checkout, as DeliverAsync leaves it.
        new WorkspaceManager(_paths, Project).PrepareTrunkClone(_paths.ProjectBuild(Project));

        AppearanceChange.Apply(_paths, Project, new ThemeChoice("carbon", "dark"), _prompts);

        // Not the repo this time: the folder the client opens and runs, which is a copy taken at
        // delivery and would otherwise serve the old stylesheet until the next one.
        var served = File.ReadAllText(Path.Combine(
            _paths.ProjectBuild(Project), "src", "App", "wwwroot", "forge-ui", "theme.css"));
        Assert.Contains("Theme: carbon", served);
        Assert.Contains("Mode: dark", served);
    }

    [Fact]
    public void Changing_the_theme_is_not_a_change_request_and_leaves_no_record_behind()
    {
        SeedRunnableProject();
        AppearanceChange.Apply(_paths, Project, new ThemeChoice("vivid", "dark"), _prompts);
        AppearanceChange.Apply(_paths, Project, new ThemeChoice("paper", "light"), _prompts);

        // A theme is a setting, not something that was asked for and built: the change log stays
        // the record of requirements, and the client can flip themes as often as they like.
        Assert.Empty(ChangeLog.Read(_paths, Project));
        Assert.Contains("paper", ShowFromTrunk("src/App/wwwroot/forge-ui/theme.css"));
    }
}
