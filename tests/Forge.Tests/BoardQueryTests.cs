using Forge.Core.Board;
using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// The board is what the client sees, so the property that matters most is that the
/// money adds up: whatever is on the page has to reconcile with the ledger, or the
/// client is looking at spend nobody can account for.
/// </summary>
public class BoardQueryTests : IDisposable
{
    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");
    private readonly TaskRepository _tasks;
    private readonly LedgerRepository _ledger;

    public BoardQueryTests()
    {
        _tasks = new TaskRepository(_conn);
        _ledger = new LedgerRepository(_conn);
    }

    public void Dispose() => _conn.Dispose();

    private BoardSnapshot Board() => new BoardQuery(_conn, "demo").Snapshot();

    private long Milestone(string name, int ordinal) =>
        new MilestoneRepository(_conn).Insert(new MilestoneRecord
        {
            Name = name, Ordinal = ordinal, Status = MilestoneStatus.Planned,
        }).Id;

    private TaskRecord Task(TaskType type, string title, long? milestone = null, long? parent = null)
    {
        var task = _tasks.Insert(TaskRecord.Create(
            type, title, "objective", 10_000, milestoneId: milestone,
            assignedRole: AgentRole.Engineer));
        if (parent is { } p) _tasks.SetParent(task.Id, p);
        return task;
    }

    private void Advance(long id, params TaskStatus[] path)
    {
        foreach (var status in path) _tasks.Transition(id, status);
    }

    private void Spend(long? taskId, decimal usd, AgentRole role = AgentRole.Engineer) =>
        _ledger.Append(new TokenLedgerEntry
        {
            AgentInstanceId = $"a-{Guid.NewGuid():N}",
            Role = role,
            TaskId = taskId,
            Model = "claude-sonnet-5",
            TokensIn = 10,
            TokensOut = 10,
            CostUsd = usd,
        });

    [Fact]
    public void An_unplanned_project_reports_nothing_to_show_yet()
    {
        var board = Board();

        Assert.False(board.Planned);
        Assert.Equal("planning", board.State);
        Assert.Empty(board.Milestones);
    }

    [Fact]
    public void Milestone_state_is_derived_from_its_tasks_not_from_the_status_column()
    {
        // milestones.status is left at 'planned' throughout — nothing advances it, which
        // is exactly why the board must not trust it.
        var untouched = Milestone("Not started", 1);
        var underway = Milestone("Underway", 2);
        var finished = Milestone("Finished", 3);

        Task(TaskType.Task, "queued", untouched);

        var running = Task(TaskType.Task, "running", underway);
        Advance(running.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress);

        var complete = Task(TaskType.Task, "complete", finished);
        Advance(complete.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        var states = Board().Milestones.ToDictionary(m => m.Name, m => m.State);
        Assert.Equal("pending", states["Not started"]);
        Assert.Equal("active", states["Underway"]);
        Assert.Equal("done", states["Finished"]);
    }

    [Fact]
    public void A_milestone_with_no_tasks_at_all_is_pending_not_complete()
    {
        Milestone("Empty", 1);

        var milestone = Assert.Single(Board().Milestones);
        Assert.Equal("pending", milestone.State);
        Assert.Equal(0, milestone.Total);
    }

    [Fact]
    public void A_features_cost_includes_the_children_it_was_decomposed_into()
    {
        var feature = Task(TaskType.Feature, "Comparison page");
        var childA = Task(TaskType.Task, "backend", parent: feature.Id);
        var childB = Task(TaskType.Task, "frontend", parent: feature.Id);

        Spend(feature.Id, 0.10m);    // the Principal's own decomposition turn
        Spend(childA.Id, 1.25m);
        Spend(childB.Id, 0.65m);

        var item = Assert.Single(Board().Features);
        Assert.Equal(2.00m, item.CostUsd);
    }

    [Fact]
    public void Everything_the_page_shows_adds_up_to_the_ledger_total()
    {
        var feature = Task(TaskType.Feature, "A feature");
        var child = Task(TaskType.Task, "its child", parent: feature.Id);
        var bug = Task(TaskType.Bug, "a bug nobody parented");

        Spend(child.Id, 4.00m);                       // inside a feature
        Spend(bug.Id, 1.50m);                         // a task under no feature
        Spend(null, 2.50m, AgentRole.Pm);             // chat / design / QA — no task at all

        var board = Board();
        var features = board.Features.Sum(f => f.CostUsd);

        Assert.Equal(4.00m, features);
        Assert.Equal(2.50m, board.ProjectLevelCostUsd);
        Assert.Equal(1.50m, board.UnparentedTaskCostUsd);

        // The property the client depends on: no unexplained money.
        Assert.Equal(board.TotalCostUsd,
            features + board.ProjectLevelCostUsd + board.UnparentedTaskCostUsd);
        Assert.Equal(8.00m, board.TotalCostUsd);
    }

    [Fact]
    public void Spend_is_broken_out_per_agent()
    {
        var task = Task(TaskType.Task, "work");
        Spend(task.Id, 3.00m, AgentRole.Engineer);
        Spend(task.Id, 1.00m, AgentRole.Engineer);
        Spend(null, 5.00m, AgentRole.Principal);

        var agents = Board().Agents.ToDictionary(a => a.Role);
        Assert.Equal(4.00m, agents["engineer"].CostUsd);
        Assert.Equal(2, agents["engineer"].Calls);
        Assert.Equal(5.00m, agents["principal"].CostUsd);

        // Most expensive first — the client's question is "where did the money go".
        Assert.Equal("principal", Board().Agents[0].Role);
    }

    [Fact]
    public void The_chat_shows_only_what_the_client_is_party_to()
    {
        var messages = new MessageRepository(_conn);
        messages.Insert(Message.Create(MessageType.Question, "client", "pm", "how much will this cost?"));
        messages.Insert(Message.Create(MessageType.Answer, "pm", "client", "about twenty dollars."));
        // Internal traffic the client has no business seeing.
        var task = Task(TaskType.Task, "some work");
        messages.Insert(Message.Create(MessageType.Escalation, "engineer", "principal", "I am stuck.", task.Id));
        messages.Insert(Message.Create(MessageType.SystemNudge, "system", "engineer", "wrap up.", task.Id));

        var chat = Board().Chat;

        Assert.Equal(2, chat.Count);
        Assert.Equal("client", chat[0].From);
        Assert.Equal("about twenty dollars.", chat[1].Text);
    }

    [Fact]
    public void A_project_whose_milestones_are_all_done_reads_as_complete()
    {
        var milestone = Milestone("Only one", 1);
        var task = Task(TaskType.Task, "the work", milestone);
        Advance(task.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        Assert.Equal("complete", Board().State);
    }

    [Fact]
    public void A_cancelled_features_spend_still_reconciles_instead_of_vanishing()
    {
        // The features section hides a cancelled feature — but its money must not
        // disappear with it, or the client sees a total no section explains.
        var kept = Task(TaskType.Feature, "Kept feature");
        var keptChild = Task(TaskType.Task, "kept child", parent: kept.Id);
        var cancelled = Task(TaskType.Feature, "Cancelled feature");
        var orphan = Task(TaskType.Task, "cancelled child", parent: cancelled.Id);
        _tasks.Transition(cancelled.Id, TaskStatus.Cancelled);

        Spend(keptChild.Id, 3.00m);
        Spend(cancelled.Id, 0.40m);   // the cancelled feature's own decomposition turn
        Spend(orphan.Id, 1.60m);

        var board = Board();

        Assert.Single(board.Features);                       // the cancelled one is hidden
        var features = board.Features.Sum(f => f.CostUsd);
        Assert.Equal(3.00m, features);
        Assert.Equal(2.00m, board.UnparentedTaskCostUsd);    // 0.40 + 1.60 land here

        Assert.Equal(board.TotalCostUsd,
            features + board.ProjectLevelCostUsd + board.UnparentedTaskCostUsd);
    }
}
