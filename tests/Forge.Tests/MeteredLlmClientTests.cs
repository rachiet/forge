using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class MeteredLlmClientTests : IDisposable
{
    private sealed class FakeLlmClient(int tokensIn, int tokensOut) : ILlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse
            {
                Content = "ok",
                StopReason = "end_turn",
                Usage = new LlmUsage(tokensIn, tokensOut),
            });
        }
    }

    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");
    private readonly TaskRepository _tasks;

    public MeteredLlmClientTests()
    {
        _tasks = new TaskRepository(_conn);
    }

    public void Dispose() => _conn.Dispose();

    private long StartTask(int budget)
    {
        var task = _tasks.Insert(TaskRecord.Create(TaskType.Feature, "T", "O", budget));
        _tasks.Transition(task.Id, TaskStatus.Ready);
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        return task.Id;
    }

    private static LlmRequest Request(long? taskId) => new()
    {
        Model = "claude-sonnet-5",
        Messages = [new LlmMessage("user", "hi")],
        Attribution = new LlmAttribution("eng-20260719-100000", AgentRole.Engineer, taskId),
    };

    [Fact]
    public async Task Every_call_is_ledgered_and_spent_against_the_task()
    {
        var taskId = StartTask(budget: 10_000);
        var inner = new FakeLlmClient(1000, 500);
        var client = new MeteredLlmClient(inner, _conn, ModelPricing.Default);

        await client.CompleteAsync(Request(taskId));

        var entry = Assert.Single(new LedgerRepository(_conn).List(taskId));
        Assert.Equal(1000, entry.TokensIn);
        Assert.Equal(500, entry.TokensOut);
        Assert.Equal((1000 * 3.0 + 500 * 15.0) / 1_000_000, entry.CostUsd, 10);
        Assert.Equal(1500, _tasks.Get(taskId).TokensSpent);
    }

    [Fact]
    public async Task Crossing_70_percent_injects_one_system_nudge()
    {
        var taskId = StartTask(budget: 1000);
        var client = new MeteredLlmClient(new FakeLlmClient(300, 100), _conn, ModelPricing.Default);
        var messages = new MessageRepository(_conn);

        await client.CompleteAsync(Request(taskId)); // 400 spent — below 700
        Assert.Empty(messages.Pending("engineer"));

        await client.CompleteAsync(Request(taskId)); // 800 spent — crosses 700
        var nudge = Assert.Single(messages.Pending("engineer"));
        Assert.IsType<SystemNudgeMessage>(nudge);
        Assert.Equal(taskId, nudge.TaskId);

        // Already past the threshold: no second nudge, and 1200 > 1000 will refuse next time.
        var pendingBefore = messages.Pending("engineer").Count;
        await client.CompleteAsync(Request(taskId));
        Assert.Equal(pendingBefore, messages.Pending("engineer").Count);
    }

    [Fact]
    public async Task Exhausted_task_budget_refuses_the_call_and_leaves_parking_to_the_runner()
    {
        var taskId = StartTask(budget: 1000);
        _tasks.AddTokensSpent(taskId, 1000);
        var inner = new FakeLlmClient(1, 1);
        var client = new MeteredLlmClient(inner, _conn, ModelPricing.Default);

        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(taskId)));

        Assert.Equal(0, inner.Calls); // enforcement = not making the call
        // The supervisor is a single-purpose meter: it does NOT transition the task or
        // escalate for a task budget — TaskRunner.Park owns that (OutOfBudget → Principal),
        // so the two can't disagree. The task is untouched here.
        Assert.NotEqual(TaskStatus.Blocked, _tasks.Get(taskId).Status);
        Assert.Empty(new MessageRepository(_conn).Pending("pm"));
        Assert.Empty(new MessageRepository(_conn).Pending("principal"));
    }

    [Fact]
    public async Task Project_budget_cap_refuses_even_untasked_calls()
    {
        var client = new MeteredLlmClient(
            new FakeLlmClient(600, 500), _conn, ModelPricing.Default, projectTokenBudget: 1000);

        await client.CompleteAsync(Request(null)); // 1100 total, over the cap now
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(null)));
        Assert.Single(new MessageRepository(_conn).Pending("pm"));
    }
}
