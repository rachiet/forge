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
        Assert.Empty(Board().Plan);
    }

    private BoardSnapshot Board() => new BoardQuery(_conn, "demo").Snapshot();

    private TaskRecord Task(TaskType type, string title, string? milestone = null,
                            long? parent = null, string? displayName = null)
    {
        var task = _tasks.Insert(TaskRecord.Create(
            type, title, "objective", 10_000,
            displayName: displayName,
            milestoneId: milestone is null ? null : new MilestoneRepository(_conn).EnsureByName(milestone).Id,
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
        Assert.Empty(board.Plan);
    }

    [Fact]
    public void A_phase_is_done_when_every_task_under_it_is()
    {
        Task(TaskType.Task, "queued", "Books API");

        var running = Task(TaskType.Task, "running", "Library interface");
        Advance(running.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress);

        var complete = Task(TaskType.Task, "complete", "Bootstrap");
        Advance(complete.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        var states = Board().Plan.ToDictionary(m => m.Name, m => m.State);
        Assert.Equal("pending", states["Books API"]);
        Assert.Equal("active", states["Library interface"]);
        Assert.Equal("done", states["Bootstrap"]);
    }

    [Fact]
    public void Phases_appear_in_the_order_they_were_first_named_with_their_tasks_under_them()
    {
        Task(TaskType.Task, "storage", "Books API", displayName: "Storing books on disk");
        Task(TaskType.Task, "page", "Library interface", displayName: "The page you see books on");
        Task(TaskType.Task, "endpoints", "Books API", displayName: "Adding and deleting books");

        var plan = Board().Plan;

        Assert.Equal(["Books API", "Library interface"], plan.Select(m => m.Name));
        Assert.Equal(["Storing books on disk", "Adding and deleting books"],
            plan[0].Tasks.Select(t => t.Name));
    }

    [Fact]
    public void A_task_with_no_display_name_falls_back_to_its_title()
    {
        Task(TaskType.Task, "implement-books-http-api", "Books API");

        var task = Assert.Single(Board().Plan[0].Tasks);
        Assert.Equal("implement-books-http-api", task.Name);
    }

    [Fact]
    public void The_task_a_worker_holds_is_the_one_marked_active()
    {
        var idle = Task(TaskType.Task, "queued", "Books API");
        var held = Task(TaskType.Task, "running", "Books API");
        Advance(held.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress);

        var states = Board().Plan[0].Tasks.ToDictionary(t => t.Id, t => t.State);
        Assert.Equal("pending", states[idle.Id]);
        Assert.Equal("active", states[held.Id]);
    }

    [Fact]
    public void The_first_phase_names_the_work_that_was_never_a_task_and_never_shows_as_active()
    {
        new MilestoneRepository(_conn).EnsureFirst(MilestoneRepository.GettingStarted);
        Task(TaskType.Task, "work", "Books API");
        Spend(null, 2.50m, AgentRole.Pm);          // intake
        Spend(null, 1.50m, AgentRole.Principal);   // the design run

        var first = Board().Plan[0];

        Assert.Equal(MilestoneRepository.GettingStarted, first.Name);
        Assert.Equal(4.00m, first.CostUsd);
        // The plan exists, so the phase that produced it is finished — and it has nothing in
        // flight, so it must not blink once the build is delivered.
        Assert.Equal("done", first.State);
        // The intake and planning runs are not tasks, so the phase says what they were.
        var line = Assert.Single(first.Tasks);
        Assert.Equal("Set up the project and planned the work", line.Name);
        Assert.Equal("done", line.State);
        Assert.Equal(4.00m, line.CostUsd);
    }

    [Fact]
    public void The_first_phase_is_pending_while_there_is_still_no_plan()
    {
        new MilestoneRepository(_conn).EnsureFirst(MilestoneRepository.GettingStarted);
        Task(TaskType.Feature, "A feature");       // decomposition has not run yet
        Spend(null, 2.50m, AgentRole.Pm);

        var first = Board().Plan[0];

        Assert.Equal("pending", first.State);
        Assert.Equal("pending", Assert.Single(first.Tasks).State);
    }

    [Fact]
    public void A_QA_round_is_charged_to_the_testing_phase_although_it_has_no_task()
    {
        new MilestoneRepository(_conn).EnsureByName(MilestoneRepository.Testing);
        Task(TaskType.Bug, "a bug QA filed", MilestoneRepository.Testing);
        Spend(null, 3.00m, AgentRole.Qa);

        var testing = Assert.Single(Board().Plan, m => m.Name == MilestoneRepository.Testing);
        Assert.Equal(3.00m, testing.CostUsd);
    }

    [Fact]
    public void Everything_the_page_shows_adds_up_to_the_ledger_total()
    {
        new MilestoneRepository(_conn).EnsureFirst(MilestoneRepository.GettingStarted);
        var feature = Task(TaskType.Feature, "A feature");
        var child = Task(TaskType.Task, "its child", "Books API", parent: feature.Id);
        var bug = Task(TaskType.Bug, "a bug", MilestoneRepository.Testing);

        Spend(child.Id, 4.00m);                       // planned work
        Spend(bug.Id, 1.50m);                         // a fix
        Spend(null, 2.50m, AgentRole.Pm);             // chat and design — no task at all
        Spend(null, 0.75m, AgentRole.Qa);             // a QA round — also no task

        var board = Board();

        // The property the client depends on: every dollar sits in exactly one phase.
        Assert.Equal(board.TotalCostUsd, board.Plan.Sum(m => m.CostUsd));
        Assert.Equal(8.75m, board.TotalCostUsd);
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
    public void A_project_whose_features_are_all_done_reads_as_complete_only_once_it_is_handed_over()
    {
        var feature = Task(TaskType.Feature, "Only one");
        var task = Task(TaskType.Task, "the work", "01-thing.md@v1", parent: feature.Id);
        Advance(task.Id, TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress,
            TaskStatus.InReview, TaskStatus.Merging, TaskStatus.Qa, TaskStatus.Done);

        // QA and delivery still to come, so the page must keep offering a way to resume.
        Assert.Equal("building", Board().State);

        new ProjectMetaRepository(_conn).Set("project_delivered", "1");
        Assert.Equal("complete", Board().State);
    }

    [Fact]
    public void Nothing_is_marked_active_when_no_task_is_in_flight()
    {
        Task(TaskType.Task, "queued", "Books API");

        Assert.DoesNotContain(Board().Plan.SelectMany(m => m.Tasks), t => t.State == "active");
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
    public void Cancelled_work_leaves_the_plan_but_its_spend_stays_in_the_total()
    {
        // A task the client dropped is not shown as work — but its money must not disappear
        // with it, or the client sees a total no phase explains.
        new MilestoneRepository(_conn).EnsureFirst(MilestoneRepository.GettingStarted);
        var feature = Task(TaskType.Feature, "The build");
        var kept = Task(TaskType.Task, "kept", "Books API", parent: feature.Id);
        var dropped = Task(TaskType.Task, "dropped", "Books API", parent: feature.Id);
        _tasks.Transition(dropped.Id, TaskStatus.Cancelled);

        Spend(kept.Id, 3.00m);
        Spend(dropped.Id, 1.60m);
        Spend(feature.Id, 0.40m);     // the decomposition turn, which is planning

        var board = Board();
        var api = Assert.Single(board.Plan, m => m.Name == "Books API");

        Assert.Equal(["kept"], api.Tasks.Select(t => t.Name));   // the dropped one is hidden
        Assert.Equal(4.60m, api.CostUsd);                        // but still charged here
        Assert.Equal(board.TotalCostUsd, board.Plan.Sum(m => m.CostUsd));
    }
}
