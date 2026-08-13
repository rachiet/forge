using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class RepositoryTests : IDisposable
{
    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");

    public void Dispose() => _conn.Dispose();

    private TaskRecord InsertTask(int budget = 10_000) =>
        new TaskRepository(_conn).Insert(TaskRecord.Create(
            TaskType.Task, "Add login", "Users can log in", budget,
            acceptanceCriteria: "POST /login returns 200",
            contextPaths: ["src/auth/", "docs/design/03-contracts/auth.yaml"],
            requirementsRef: RequirementsRef.Parse("01-users-auth.md"),
            assignedRole: AgentRole.Engineer,
            createdBy: "principal"));

    [Fact]
    public void Task_round_trips_through_the_db()
    {
        var repo = new TaskRepository(_conn);
        var inserted = InsertTask();
        var loaded = repo.Get(inserted.Id);

        Assert.Equal(TaskType.Task, loaded.Type);
        Assert.Equal(TaskStatus.Created, loaded.Status);
        Assert.Equal(AgentRole.Engineer, loaded.AssignedRole);
        Assert.Equal(["src/auth/", "docs/design/03-contracts/auth.yaml"], loaded.ContextPaths);
        Assert.Equal(new RequirementsRef("01-users-auth.md"), loaded.RequirementsRef);
        Assert.Equal(10_000, loaded.TokenBudget);
        Assert.NotNull(loaded.CreatedAt);
    }

    [Fact]
    public void Transition_walks_the_legal_map_and_rejects_shortcuts()
    {
        var repo = new TaskRepository(_conn);
        var task = InsertTask();

        repo.Transition(task.Id, TaskStatus.Ready);
        repo.Transition(task.Id, TaskStatus.Claimed);
        repo.Transition(task.Id, TaskStatus.InProgress);
        Assert.Equal(TaskStatus.InProgress, repo.Get(task.Id).Status);

        Assert.Throws<IllegalTaskTransitionException>(
            () => repo.Transition(task.Id, TaskStatus.Done));
        Assert.Equal(TaskStatus.InProgress, repo.Get(task.Id).Status);
    }

    [Fact]
    public void Raw_status_updates_are_rejected_by_the_schema()
    {
        var task = InsertTask();
        var ex = Assert.Throws<SqliteException>(() =>
            _conn.Execute("UPDATE tasks SET status = 'nonsense' WHERE id = @Id", new { task.Id }));
        Assert.Contains("CHECK", ex.Message);
    }

    [Fact]
    public void Message_queue_semantics_pending_then_done()
    {
        var tasks = InsertTask();
        var repo = new MessageRepository(_conn);
        var q = repo.Insert(Message.Create(MessageType.Question, "engineer", "principal", "Is X in scope?", tasks.Id));
        repo.Insert(Message.Create(MessageType.Status, "pm", "client", "on track"));

        var pending = repo.Pending("principal");
        var only = Assert.Single(pending);
        Assert.IsType<QuestionMessage>(only);
        Assert.Equal(q.Id, only.Id);

        repo.SetStatus(q.Id, MessageStatus.Received);
        Assert.Empty(repo.Pending("principal"));

        Assert.Equal(2, repo.Log().Count);
        Assert.Single(repo.Log(tasks.Id));
    }

    [Fact]
    public void Active_feature_closes_only_when_it_has_children_and_all_are_terminal()
    {
        var repo = new TaskRepository(_conn);
        var feature = repo.Insert(TaskRecord.Create(
            TaskType.Feature, "Multi-city view", "Show several cities at once", 60_000,
            assignedRole: AgentRole.Principal, createdBy: "pm") with { Status = TaskStatus.Triage });
        repo.Transition(feature.Id, TaskStatus.Active);

        // A childless active Feature must not close — no work has run yet.
        Assert.Empty(repo.ActiveFeaturesReadyToClose());

        var child1 = InsertTask();
        var child2 = InsertTask();
        repo.SetParent(child1.Id, feature.Id);
        repo.SetParent(child2.Id, feature.Id);
        Assert.Equal(feature.Id, repo.Get(child1.Id).ParentId);

        // One child still in flight → Feature not ready to close.
        Drive(repo, child1, TaskStatus.Done);
        Assert.Empty(repo.ActiveFeaturesReadyToClose());

        // Both children terminal (done + a non-done terminal both count) → ready to close.
        repo.Transition(child2.Id, TaskStatus.Ready);
        repo.Transition(child2.Id, TaskStatus.Cancelled);
        Assert.Equal([feature.Id], repo.ActiveFeaturesReadyToClose());
    }

    [Fact]
    public void Feature_is_born_in_triage_so_the_principal_picks_it_up()
    {
        var repo = new TaskRepository(_conn);
        var feature = repo.Insert(TaskRecord.Create(
            TaskType.Feature, "Change request", "One change", 60_000,
            assignedRole: AgentRole.Principal, createdBy: "pm") with { Status = TaskStatus.Triage });

        var owned = repo.NextPrincipalOwned();
        Assert.NotNull(owned);
        Assert.Equal(feature.Id, owned!.Id);
        Assert.Equal(TaskStatus.Triage, owned.Status);
    }

    private static void Drive(TaskRepository repo, TaskRecord task, TaskStatus to)
    {
        repo.Transition(task.Id, TaskStatus.Ready);
        repo.Transition(task.Id, TaskStatus.Claimed);
        repo.Transition(task.Id, TaskStatus.InProgress);
        repo.Transition(task.Id, TaskStatus.InReview);
        repo.Transition(task.Id, TaskStatus.Merging);
        repo.Transition(task.Id, TaskStatus.Qa);
        repo.Transition(task.Id, to);
    }

    [Fact]
    public void Ledger_totals_aggregate_by_task_and_project()
    {
        var task = InsertTask();
        var ledger = new LedgerRepository(_conn);
        ledger.Append(new TokenLedgerEntry
        {
            AgentInstanceId = "eng-20260719-100000",
            Role = AgentRole.Engineer,
            TaskId = task.Id,
            Model = "claude-sonnet-5",
            TokensIn = 1000,
            TokensOut = 500,
        });
        ledger.Append(new TokenLedgerEntry
        {
            AgentInstanceId = "pm-20260719-100001",
            Role = AgentRole.Pm,
            TaskId = null,
            Model = "claude-fable-5",
            TokensIn = 200,
            TokensOut = 100,
        });

        var taskTotals = ledger.TaskTotals(task.Id);
        Assert.Equal(1000, taskTotals.TokensIn);
        Assert.Equal(500, taskTotals.TokensOut);
        var project = ledger.ProjectTotals();
        Assert.Equal(1200, project.TokensIn);
        Assert.Equal(600, project.TokensOut);
        Assert.Equal(2, ledger.List().Count);
        Assert.Single(ledger.List(task.Id));
    }

    // --- The task DAG stays acyclic -----------------------------------------------
    // A dependency is satisfied only by a `done` task, so a cycle is not a slow build:
    // every task in it is permanently unclaimable. HabitTracker's Principal wrote 3 → 4
    // and 4 → 3, the loop drained instantly, and the board looked broken.

    [Fact]
    public void Direct_cycle_is_refused()
    {
        var repo = new TaskRepository(_conn);
        var a = InsertTask();
        var b = InsertTask();
        repo.AddDependency(b.Id, a.Id);

        var ex = Assert.Throws<DependencyCycleException>(() => repo.AddDependency(a.Id, b.Id));

        Assert.Equal([b.Id, a.Id], ex.Chain);
        Assert.Equal([a.Id], repo.DependenciesOf(b.Id));
        Assert.Empty(repo.DependenciesOf(a.Id));
    }

    [Fact]
    public void Indirect_cycle_is_refused_and_names_the_whole_path()
    {
        // The path is what makes the refusal actionable: the Principal has no tool to
        // delete an edge, so it must be told which existing ones it is fighting.
        var repo = new TaskRepository(_conn);
        var a = InsertTask();
        var b = InsertTask();
        var c = InsertTask();
        repo.AddDependency(b.Id, a.Id);
        repo.AddDependency(c.Id, b.Id);

        var ex = Assert.Throws<DependencyCycleException>(() => repo.AddDependency(a.Id, c.Id));

        Assert.Equal([c.Id, b.Id, a.Id], ex.Chain);
        Assert.Contains($"{c.Id} → {b.Id} → {a.Id}", ex.Message);
    }

    [Fact]
    public void A_task_cannot_depend_on_the_feature_it_belongs_to()
    {
        // The Feature closes only once its tasks are done, so a task waiting on it waits
        // forever — a deadlock the cycle check cannot see, since the closing rule is the
        // runner's rather than an edge in task_deps.
        var repo = new TaskRepository(_conn);
        var feature = repo.Insert(TaskRecord.Create(
            TaskType.Feature, "SnipBox", "Build it", 60_000,
            assignedRole: AgentRole.Principal, createdBy: "pm"));
        var task = InsertTask();

        var ex = Assert.Throws<FeatureDependencyException>(() => repo.AddDependency(task.Id, feature.Id));

        Assert.Empty(repo.DependenciesOf(task.Id));
        Assert.Contains(RefusalCode.DependsOnFeature, ex.Message, StringComparison.Ordinal);
        Assert.Contains("sibling task", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dependency_on_a_task_that_does_not_exist_is_refused()
    {
        // Nothing will ever carry that id, so the edge is another permanent block.
        var repo = new TaskRepository(_conn);
        var task = InsertTask();

        var ex = Assert.Throws<ArgumentException>(() => repo.AddDependency(task.Id, 9_999));

        Assert.Empty(repo.DependenciesOf(task.Id));
        Assert.Contains(RefusalCode.NoSuchTask, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diamond_is_allowed()
    {
        // Two tasks sharing a dependency, and a fourth waiting on both, is an ordinary
        // plan — the check must reject cycles only, not any second path between two tasks.
        var repo = new TaskRepository(_conn);
        var root = InsertTask();
        var left = InsertTask();
        var right = InsertTask();
        var join = InsertTask();
        repo.AddDependency(left.Id, root.Id);
        repo.AddDependency(right.Id, root.Id);
        repo.AddDependency(join.Id, left.Id);
        repo.AddDependency(join.Id, right.Id);

        Assert.Equal([left.Id, right.Id], repo.DependenciesOf(join.Id));
    }

    [Fact]
    public void Cycle_check_terminates_on_a_graph_that_already_has_one()
    {
        // Live databases predate this check, so the traversal meets cycles it did not
        // prevent. A check that hangs on the deadlock it was written to stop is worse
        // than none: the edges here are inserted behind the repository, as they were.
        var repo = new TaskRepository(_conn);
        var a = InsertTask();
        var b = InsertTask();
        var outsider = InsertTask();
        _conn.Execute("INSERT INTO task_deps (task_id, depends_on) VALUES (@a, @b), (@b, @a)",
            new { a = a.Id, b = b.Id });

        Assert.Empty(repo.DependencyChain(from: a.Id, to: outsider.Id));
        Assert.Equal([a.Id, b.Id], repo.DependencyChain(from: a.Id, to: b.Id));
    }

    [Fact]
    public void Self_dependency_is_still_refused()
    {
        var repo = new TaskRepository(_conn);
        var task = InsertTask();

        Assert.Throws<ArgumentException>(() => repo.AddDependency(task.Id, task.Id));
    }
}
