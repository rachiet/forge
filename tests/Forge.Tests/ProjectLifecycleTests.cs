using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Db;
using Forge.Core.Workspaces;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// Creating a project, keeping its settings to itself, and the one-build-at-a-time
/// rule — the three things a client can now do without a terminal.
/// </summary>
public class ProjectLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-life-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;

    public ProjectLifecycleTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new ForgePaths(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a git handle on Windows; the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    // ---------- what a new repo is born with ----------

    [Fact]
    public void A_new_repo_is_born_with_the_house_conventions()
    {
        // Seeded by the harness, not authored per project: two finished projects had
        // disagreed on their error shape and test naming for no reason, and the second
        // started without any of the rules the first had paid for in failed tasks.
        ProjectBootstrap.Init(_paths, "alpha");

        var onTrunk = Git.Run(_paths.ProjectBareRepo("alpha"),
            "show", $"{WorkspaceManager.TrunkBranch}:{ProjectBootstrap.ConventionsFile}");

        Assert.True(onTrunk.Ok);
        Assert.Equal(PromptLibrary.Resolve().Template(ProjectBootstrap.ConventionsFile), onTrunk.Stdout);
    }

    // ---------- per-project settings ----------

    [Fact]
    public void Two_projects_keep_their_own_provider_and_budget()
    {
        // The bug this exists to prevent: llm.json is one file for the whole data root,
        // so without per-project settings the second project created would silently run
        // on the first one's provider — and burn its budget at the wrong rates.
        ProjectBootstrap.Init(_paths, "alpha");
        ProjectBootstrap.Init(_paths, "beta");

        using (var conn = Database.OpenProject(_paths.ProjectDb("alpha")))
            _ = new ProjectSettings(conn) { Provider = "gemini", BudgetUsd = 25m };
        using (var conn = Database.OpenProject(_paths.ProjectDb("beta")))
            _ = new ProjectSettings(conn) { Provider = "anthropic", BudgetUsd = 10m };

        using var alpha = Database.OpenProject(_paths.ProjectDb("alpha"));
        using var beta = Database.OpenProject(_paths.ProjectDb("beta"));

        Assert.Equal("gemini", new ProjectSettings(alpha).Provider);
        Assert.Equal(25m, new ProjectSettings(alpha).BudgetUsd);
        Assert.Equal("anthropic", new ProjectSettings(beta).Provider);
        Assert.Equal(10m, new ProjectSettings(beta).BudgetUsd);
    }

    [Fact]
    public void A_projects_provider_outranks_the_machine_wide_file()
    {
        File.WriteAllText(Path.Combine(_root, "llm.json"), """{ "provider": "anthropic" }""");

        Assert.Equal("anthropic", LlmConfig.Load(_root).Provider);
        Assert.Equal("gemini", LlmConfig.Load(_root, "gemini").Provider);
    }

    [Fact]
    public void The_budget_can_be_raised_after_creation()
    {
        ProjectBootstrap.Init(_paths, "alpha");
        using var conn = Database.OpenProject(_paths.ProjectDb("alpha"));
        var settings = new ProjectSettings(conn) { BudgetUsd = 20m };

        settings.BudgetUsd = 80m;

        Assert.Equal(80m, new ProjectSettings(conn).BudgetUsd);
    }

    [Fact]
    public void An_unset_budget_reads_as_uncapped_rather_than_zero()
    {
        ProjectBootstrap.Init(_paths, "alpha");
        using var conn = Database.OpenProject(_paths.ProjectDb("alpha"));

        // Zero would be a cap of nothing, which refuses the very first call.
        Assert.Null(new ProjectSettings(conn).BudgetUsd);
        Assert.Null(new ProjectSettings(conn).Provider);
    }

    // ---------- a brand-new project is a blank slate ----------

    [Fact]
    public void A_new_project_shows_nothing_but_the_conversation()
    {
        ProjectBootstrap.Init(_paths, "alpha");
        using var conn = Database.OpenProject(_paths.ProjectDb("alpha"));

        var board = new BoardQuery(conn, "alpha").Snapshot();

        Assert.Equal("planning", board.State);
        Assert.False(board.Planned);
        Assert.False(board.SpecReady);
        Assert.Empty(board.Plan);
        // Not empty: a new project opens with Iris's greeting, so the client arrives at a
        // conversation rather than a blank box. It is fixed text, so it costs nothing.
        var opening = Assert.Single(board.Chat);
        Assert.Equal("pm", opening.From);
        Assert.Equal(ProjectBootstrap.Greeting, opening.Text);
        Assert.Equal(0m, board.TotalCostUsd);
    }

    [Fact]
    public void The_spec_stays_hidden_until_the_pm_hands_work_to_the_principal()
    {
        ProjectBootstrap.Init(_paths, "alpha");
        using var conn = Database.OpenProject(_paths.ProjectDb("alpha"));
        var tasks = new TaskRepository(conn);

        // A proposal put to the client is still a draft: it is read in the review dialog,
        // and nothing reaches the page until they approve it.
        new RequirementsProposal("Build it", "objective").Save(conn);
        Assert.False(new BoardQuery(conn, "alpha").Snapshot().SpecReady);

        // An approved proposal opens the Feature, and that is the trigger.
        tasks.Insert(TaskRecord.Create(
            TaskType.Feature, "Build it", "objective", 10_000, assignedRole: AgentRole.Principal));

        Assert.True(new BoardQuery(conn, "alpha").Snapshot().SpecReady);
    }

    // ---------- one build at a time ----------

    [Fact]
    public void A_second_build_cannot_start_while_one_is_running()
    {
        using var first = WorkerLease.TryAcquire(_paths, "alpha");
        Assert.NotNull(first);

        // Even a different project: the decision is one build at a time, machine-wide.
        Assert.Null(WorkerLease.TryAcquire(_paths, "beta"));
        Assert.Equal("alpha", WorkerLease.Current(_paths)!.Project);
    }

    [Fact]
    public void Releasing_the_lease_lets_the_next_build_start()
    {
        using (WorkerLease.TryAcquire(_paths, "alpha")) { }

        using var next = WorkerLease.TryAcquire(_paths, "beta");
        Assert.NotNull(next);
        Assert.Equal("beta", WorkerLease.Current(_paths)!.Project);
    }

    [Fact]
    public void A_worker_that_stopped_beating_is_treated_as_dead()
    {
        var now = DateTimeOffset.Parse("2026-07-29T10:00:00Z");
        using var held = WorkerLease.TryAcquire(_paths, "alpha", () => now);
        Assert.NotNull(held);

        // A crashed process never releases the file; the heartbeat is what frees it.
        var later = now + WorkerStatus.Timeout + TimeSpan.FromSeconds(1);
        Assert.Null(WorkerLease.Current(_paths, () => later));
        Assert.NotNull(WorkerLease.TryAcquire(_paths, "beta", () => later));
    }

    [Fact]
    public void Nothing_is_building_on_a_fresh_machine()
    {
        Assert.Null(WorkerLease.Current(_paths));
    }

    // ---------- project creation ----------

    [Fact]
    public void Creating_a_project_registers_it_and_lays_out_its_directory()
    {
        ProjectBootstrap.Init(_paths, "alpha");

        Assert.True(File.Exists(_paths.ProjectDb("alpha")));
        Assert.True(Directory.Exists(_paths.ProjectBareRepo("alpha")));
        Assert.True(Directory.Exists(_paths.WorkspacesDir("alpha")));

        using var global = Database.OpenGlobal(_paths.GlobalDb);
        Assert.Equal(1, Dapper.SqlMapper.ExecuteScalar<long>(
            global, "SELECT COUNT(*) FROM projects WHERE name = 'alpha'"));
    }

    [Fact]
    public void The_same_project_cannot_be_created_twice()
    {
        ProjectBootstrap.Init(_paths, "alpha");
        Assert.Throws<InvalidOperationException>(() => ProjectBootstrap.Init(_paths, "alpha"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("")]
    public void A_name_that_could_escape_the_data_root_is_refused(string name) =>
        Assert.Throws<ArgumentException>(() => ForgePaths.ValidName(name));

    [Fact]
    public async Task The_lease_beats_itself_so_a_long_task_cannot_be_mistaken_for_a_corpse()
    {
        // Beat() used to be the worker loop's job, called only BETWEEN tasks — and one
        // task routinely outlasts the timeout, so the lease read stale mid-task and a
        // second build could start. The timer makes liveness independent of task length.
        using var lease = WorkerLease.TryAcquire(_paths, "alpha", beatEvery: TimeSpan.FromMilliseconds(40));
        Assert.NotNull(lease);

        var first = WorkerLease.Current(_paths)!.HeartbeatAt;
        await Task.Delay(300);
        var later = WorkerLease.Current(_paths)!.HeartbeatAt;

        Assert.True(later > first, $"heartbeat never advanced ({first:O} → {later:O})");
    }
}
