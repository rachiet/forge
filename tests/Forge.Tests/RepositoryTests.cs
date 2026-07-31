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
            requirementsRef: RequirementsRef.Parse("01-users-auth.md@v2"),
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
        Assert.Equal(new RequirementsRef("01-users-auth.md", 2), loaded.RequirementsRef);
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
}
