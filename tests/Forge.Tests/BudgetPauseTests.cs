using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Forge.Core.Secrets;
using Forge.Core.Agents;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// The two budgets fail differently, and the difference is the whole point: a task
/// over its own token budget is that task's failure (strike, Principal's queue), but
/// a project over its dollar cap did nothing wrong anywhere — the build pauses with
/// the board untouched, so raising the cap resumes instead of excavating wreckage.
/// </summary>
public class BudgetPauseTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-pause-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;

    public BudgetPauseTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
        _tasks = new TaskRepository(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_dataRoot, recursive: true);
    }

    private TaskRunner Runner(ILlmClient llm, decimal? projectBudgetUsd = null) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog, projectBudgetUsd),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());

    private TaskRecord ReadyTask(int budget = 100_000) =>
        _tasks.Transition(
            _tasks.Insert(TaskRecord.Create(
                TaskType.Task, "Add greeting", "Create greeting.txt", budget,
                assignedRole: AgentRole.Engineer, createdBy: "human")).Id,
            TaskStatus.Ready);

    /// <summary>Spend past any cap before the run, so the very first call is refused.</summary>
    private void SpendProjectBudget(decimal usd) =>
        new LedgerRepository(_conn).Append(new TokenLedgerEntry
        {
            AgentInstanceId = "pm-prior", Role = AgentRole.Pm, TaskId = null,
            Model = "claude-sonnet-5", TokensIn = 10, TokensOut = 10, CostUsd = usd,
        });

    [Fact]
    public async Task A_spent_project_cap_pauses_the_task_without_striking_it()
    {
        SpendProjectBudget(1.00m);
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(ScriptedLlmClient.Tool("done", ("summary", "never reached")));

        var outcome = await Runner(llm, projectBudgetUsd: 0.50m).RunAsync(task);

        Assert.True(outcome.ProjectBudgetExhausted);
        Assert.Equal(0, llm.Calls);   // refused before the provider was ever touched

        var after = _tasks.Get(task.Id);
        // The old behaviour was the bug: strike → out_of_budget → the Principal's
        // queue, for a task that never got to run a single call.
        Assert.Equal(0, after.OutOfBudgetCount);
        Assert.NotEqual(TaskStatus.OutOfBudget, after.Status);
        Assert.NotEqual(TaskStatus.Blocked, after.Status);
    }

    [Fact]
    public async Task The_cap_escalates_to_the_pm_once_not_once_per_refused_call()
    {
        SpendProjectBudget(1.00m);
        var task = ReadyTask();
        var runner = Runner(new ScriptedLlmClient(), projectBudgetUsd: 0.50m);

        await runner.RunAsync(_tasks.Get(task.Id));
        await runner.RunAsync(_tasks.Get(task.Id));
        await runner.RunAsync(_tasks.Get(task.Id));

        var escalations = new MessageRepository(_conn).Pending("pm")
            .Where(m => m.Payload.StartsWith("Project budget exhausted")).ToList();
        Assert.Single(escalations);
    }

    [Fact]
    public async Task A_tasks_own_token_budget_still_strikes_it()
    {
        // The contrast case: no project cap, but the task itself is out of tokens.
        var task = ReadyTask(budget: 100);
        _tasks.AddTokensSpent(task.Id, 100);

        var outcome = await Runner(new ScriptedLlmClient()).RunAsync(_tasks.Get(task.Id));

        Assert.False(outcome.ProjectBudgetExhausted);
        Assert.Equal(1, _tasks.Get(task.Id).OutOfBudgetCount);
        Assert.Equal(TaskStatus.OutOfBudget, _tasks.Get(task.Id).Status);
    }

    [Fact]
    public async Task The_loop_reports_the_pause_so_callers_stop_pulling_work()
    {
        SpendProjectBudget(1.00m);
        ReadyTask();
        ReadyTask();

        var outcome = await Runner(new ScriptedLlmClient(), projectBudgetUsd: 0.50m)
            .RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        Assert.True(outcome!.ProjectBudgetExhausted);
        // Both tasks intact for the resume — neither struck nor blocked.
        Assert.All(_tasks.List().Where(t => t.Type == TaskType.Task),
            t => Assert.Equal(0, t.OutOfBudgetCount));
    }

    [Fact]
    public async Task Triage_subtasks_are_adopted_and_released_not_stranded()
    {
        // A stuck task the Principal decides to break down. Before the fix, the
        // subtasks were born `created` and nothing ever released them: the only
        // release paths ran after design sign-off or Feature decomposition, so a
        // triage decomposition quietly deadlocked the board.
        var milestone = new MilestoneRepository(_conn).Insert(
            new MilestoneRecord { Name = "M1", Ordinal = 1 });
        var stuck = _tasks.Insert(TaskRecord.Create(
            TaskType.Task, "Too big", "Do everything", 1_000,
            milestoneId: milestone.Id, assignedRole: AgentRole.Engineer));
        foreach (var s in new[] { TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress, TaskStatus.OutOfBudget })
            _tasks.Transition(stuck.Id, s);
        _tasks.IncrementOutOfBudgetCount(stuck.Id);

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("create_task",
                ("title", "First half"), ("objective", "Do the first half"), ("budget", "50000")),
            ScriptedLlmClient.Tool("redirect", ("guidance", "Do only the second half now.")));

        var outcome = await Runner(llm).RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        var subtask = Assert.Single(_tasks.List(), t => t.Title == "First half");
        Assert.Equal(TaskStatus.Ready, subtask.Status);       // claimable, not stranded
        Assert.Equal(stuck.Id, subtask.ParentId);             // lineage recorded
        Assert.Equal(milestone.Id, subtask.MilestoneId);      // client's view keeps the cost grouped
    }
}
