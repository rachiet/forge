using Dapper;
using Forge.Core;
using Forge.Core.Board;
using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// The escape hatch for work the Principal cannot resolve: it parks on the client
/// instead of stranding, and their answer either restarts it or drops it.
/// </summary>
public class NeedsHumanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-needs-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;

    public NeedsHumanTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new ForgePaths(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    private SqliteConnection Open()
    {
        var dbPath = _paths.ProjectDb("alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return Database.OpenProject(dbPath);
    }

    /// <summary>Inserts a claimable engineer task and walks it to <paramref name="through"/>.</summary>
    private static TaskRecord Insert(TaskRepository tasks, string title, params TaskStatus[] through)
    {
        var task = tasks.Insert(TaskRecord.Create(
            TaskType.Task, title, "objective", 10_000, assignedRole: AgentRole.Engineer));
        task = tasks.Transition(task.Id, TaskStatus.Ready);
        foreach (var status in through) task = tasks.Transition(task.Id, status);
        return task;
    }

    [Fact]
    public void Task_awaiting_the_client_is_not_offered_to_the_principal()
    {
        // The old design hid an escalated task behind a pending message, which meant
        // the queue and the board disagreed about whether anything was left to do.
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var stuck = Insert(tasks, "stuck", TaskStatus.Blocked, TaskStatus.NeedsHuman);

        Assert.Null(tasks.NextPrincipalOwned());
        Assert.Equal([stuck.Id], tasks.AwaitingClient().Select(t => t.Id));
    }

    [Fact]
    public void Blocked_task_is_still_offered_to_the_principal()
    {
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var blocked = Insert(tasks, "blocked", TaskStatus.Blocked);

        Assert.Equal(blocked.Id, tasks.NextPrincipalOwned()?.Id);
    }

    [Fact]
    public void Needs_human_routes_to_the_pm_and_a_blocked_task_to_the_principal()
    {
        Assert.Equal(AgentRole.Pm, TaskTransitions.RoleFor(TaskStatus.NeedsHuman));
        Assert.Equal(AgentRole.Principal, TaskTransitions.RoleFor(TaskStatus.Blocked));
    }

    [Fact]
    public void Client_guidance_sends_the_task_back_with_a_clean_strike_count()
    {
        // Without the reset the task returns to the Principal at its strike ceiling
        // and is given up on before anyone acts on what the client just said.
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var stuck = Insert(tasks, "stuck", TaskStatus.Blocked, TaskStatus.NeedsHuman);
        tasks.IncrementOutOfBudgetCount(stuck.Id);
        tasks.IncrementOutOfBudgetCount(stuck.Id);
        tasks.IncrementOutOfBudgetCount(stuck.Id);

        tasks.ResetOutOfBudgetCount(stuck.Id);
        tasks.Transition(stuck.Id, TaskStatus.Triage);

        var after = tasks.Get(stuck.Id);
        Assert.Equal(0, after.OutOfBudgetCount);
        Assert.Equal(TaskStatus.Triage, after.Status);
        Assert.Equal(stuck.Id, tasks.NextPrincipalOwned()?.Id);
    }

    [Fact]
    public void Dependents_of_a_cancelled_task_are_found_transitively()
    {
        // A dependency edge is only satisfied by a `done` task, so cancelling one
        // without its dependents leaves them permanently unclaimable and invisible.
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var root = Insert(tasks, "root");
        var child = Insert(tasks, "child");
        var grandchild = Insert(tasks, "grandchild");
        var unrelated = Insert(tasks, "unrelated");
        tasks.AddDependency(child.Id, root.Id);
        tasks.AddDependency(grandchild.Id, child.Id);

        var affected = tasks.UnfinishedDependents(root.Id).Select(t => t.Id).ToList();

        Assert.Equal([child.Id, grandchild.Id], affected);
        Assert.DoesNotContain(unrelated.Id, affected);
    }

    [Fact]
    public void Finished_dependents_are_left_alone()
    {
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var root = Insert(tasks, "root");
        var child = Insert(tasks, "child");
        tasks.AddDependency(child.Id, root.Id);
        foreach (var s in new[] { TaskStatus.Claimed, TaskStatus.InProgress, TaskStatus.InReview,
                                  TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done })
            tasks.Transition(child.Id, s);

        Assert.Empty(tasks.UnfinishedDependents(root.Id));
    }

    [Fact]
    public void Board_reports_the_tasks_waiting_on_the_client()
    {
        using var conn = Open();
        var tasks = new TaskRepository(conn);
        var stuck = Insert(tasks, "polish the layout", TaskStatus.Blocked, TaskStatus.NeedsHuman);
        tasks.SetProgressNote(stuck.Id, "needs a decision");

        var snapshot = new BoardQuery(conn, "alpha").Snapshot();

        Assert.True(snapshot.NeedsClient);
        var waiting = Assert.Single(snapshot.AwaitingClient!);
        Assert.Equal("polish the layout", waiting.Title);
        Assert.Equal("needs a decision", waiting.Note);
    }

    [Fact]
    public void A_board_with_nothing_stuck_reports_no_ask()
    {
        using var conn = Open();
        Insert(new TaskRepository(conn), "normal");

        Assert.False(new BoardQuery(conn, "alpha").Snapshot().NeedsClient);
    }

    [Fact]
    public void Migration_widens_the_status_check_on_an_existing_database()
    {
        // A project.db created before this status existed keeps its old CHECK, and
        // CREATE TABLE IF NOT EXISTS will not touch it.
        var dbPath = _paths.ProjectDb("legacy");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var raw = Database.Open(dbPath))
        {
            raw.Execute(Schema.ProjectDdl.Replace(
                "'needs_human','cancelled'", "'cancelled'", StringComparison.Ordinal));
            raw.Execute("""
                INSERT INTO tasks (type, title, objective, status, token_budget, assigned_role)
                VALUES ('task', 'legacy', 'objective', 'blocked', 10000, 'engineer')
                """);
        }

        using var conn = Database.OpenProject(dbPath);
        var tasks = new TaskRepository(conn);
        var migrated = tasks.Transition(tasks.List().Single().Id, TaskStatus.NeedsHuman);

        Assert.Equal(TaskStatus.NeedsHuman, migrated.Status);
        Assert.Equal("legacy", migrated.Title);
    }
}
