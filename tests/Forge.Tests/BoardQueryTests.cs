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

    [Fact]
    public void A_proposal_survives_on_the_board_until_it_is_decided()
    {
        Assert.Null(Board().Proposal);

        new RequirementsProposal("Initial build", "Ship the thing").Save(_conn);

        // It stays pending while the client reads it and goes on talking, so the review
        // dialog can be reopened from the same button.
        Assert.Equal("Initial build", Board().Proposal!.Title);
        Assert.Equal("Initial build", Board().Proposal!.Title);
    }

    [Fact]
    public void Approving_opens_the_feature_and_clears_the_proposal()
    {
        new RequirementsProposal("Initial build", "Ship the thing").Save(_conn);

        var feature = RequirementsProposal.Load(_conn)!.Approve(_conn);

        Assert.Equal(TaskType.Feature, feature.Type);
        Assert.Equal(TaskStatus.Triage, feature.Status);       // the Principal decomposes it
        Assert.Equal(AgentRole.Principal, feature.AssignedRole);
        // Cleared, so the buttons cannot be clicked twice into two Features.
        Assert.Null(Board().Proposal);
        Assert.True(Board().SpecReady);
    }

    [Fact]
    public void Declining_leaves_no_feature_behind()
    {
        new RequirementsProposal("Initial build", "Ship the thing").Save(_conn);

        RequirementsProposal.Clear(_conn);

        Assert.Null(Board().Proposal);
        Assert.False(Board().SpecReady);
        Assert.Empty(Board().Features);
    }

    private BoardSnapshot Board() => new BoardQuery(_conn, "demo").Snapshot();

    private TaskRecord Task(TaskType type, string title, string? requirement = null, long? parent = null)
    {
        var task = _tasks.Insert(TaskRecord.Create(
            type, title, "objective", 10_000,
            requirementsRef: requirement is null ? null : RequirementsRef.Parse(requirement),
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
        Assert.Empty(board.Requirements);
    }

    [Fact]
    public void A_requirements_state_is_derived_from_the_tasks_that_name_it()
    {
        Task(TaskType.Task, "queued", "01-queued.md@v1");

        var running = Task(TaskType.Task, "running", "02-underway.md@v1");
        Advance(running.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress);

        var complete = Task(TaskType.Task, "complete", "03-finished.md@v2");
        Advance(complete.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        var states = Board().Requirements.ToDictionary(r => r.File, r => r.State);
        Assert.Equal("pending", states["01-queued.md"]);
        Assert.Equal("active", states["02-underway.md"]);
        // The version is stripped, so a bumped requirement stays one group.
        Assert.Equal("done", states["03-finished.md"]);
    }

    [Fact]
    public void Tasks_naming_no_requirement_are_grouped_apart_and_reported_last()
    {
        Task(TaskType.Task, "the feature work", "01-thing.md@v1");
        Task(TaskType.Chore, "scaffolding", null);

        var groups = Board().Requirements;

        Assert.Equal(["01-thing.md", ""], groups.Select(r => r.File));
    }

    [Fact]
    public void A_feature_adds_its_cost_to_a_group_without_counting_as_work_in_it()
    {
        // The Feature is the container its children are counted in; counting it too would
        // leave a group one short of complete for the whole build.
        var feature = Task(TaskType.Feature, "The build");
        var child = Task(TaskType.Task, "the work", "01-thing.md@v1", parent: feature.Id);
        Advance(child.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);
        Spend(feature.Id, 0.20m);      // the design turn that decomposed it

        var groups = Board().Requirements.ToDictionary(r => r.File);

        Assert.Equal("done", groups["01-thing.md"].State);
        Assert.Equal(1, groups["01-thing.md"].Total);
        Assert.Equal(0, groups[""].Total);          // the Feature is not work
        Assert.Equal(0.20m, groups[""].CostUsd);    // but its money is still shown
    }

    [Fact]
    public void Two_versions_of_one_requirement_are_the_same_group()
    {
        Task(TaskType.Task, "first pass", "01-thing.md@v1");
        Task(TaskType.Task, "after the change request", "01-thing.md@v2");

        var group = Assert.Single(Board().Requirements);
        Assert.Equal(2, group.Total);
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
    public void A_project_whose_features_are_all_done_reads_as_complete()
    {
        var feature = Task(TaskType.Feature, "Only one");
        var task = Task(TaskType.Task, "the work", "01-thing.md@v1", parent: feature.Id);
        Advance(task.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        Assert.Equal("complete", Board().State);
    }

    [Fact]
    public void The_task_in_hand_is_reported_with_the_requirement_it_serves()
    {
        Task(TaskType.Task, "not started yet", "01-later.md@v1");
        var running = Task(TaskType.Task, "the one being built", "02-now.md@v1");
        Advance(running.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress);

        var inHand = Board().CurrentTask;

        Assert.NotNull(inHand);
        Assert.Equal("the one being built", inHand.Title);
        Assert.Equal("in_progress", inHand.Status);
        Assert.Equal("02-now.md", inHand.Requirement);
    }

    [Fact]
    public void Nothing_is_in_hand_when_no_task_is_in_flight()
    {
        Task(TaskType.Task, "queued", "01-thing.md@v1");

        Assert.Null(Board().CurrentTask);
    }

    [Fact]
    public void A_pending_proposal_does_not_put_the_spec_on_the_page()
    {
        // The draft is read in the review dialog; the page changes only on approval, so
        // declining leaves nothing behind.
        new RequirementsProposal("Initial build", "Ship the thing").Save(_conn);

        Assert.NotNull(Board().Proposal);
        Assert.False(Board().SpecReady);
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
