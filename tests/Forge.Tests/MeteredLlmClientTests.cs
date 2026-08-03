using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class MeteredLlmClientTests : IDisposable
{
    private sealed class FakeLlmClient(int tokensIn, int tokensOut, int cacheRead = 0, int cacheWrite = 0) : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        public int Calls { get; private set; }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new LlmResponse
            {
                Content = "ok",
                StopReason = "end_turn",
                Usage = new LlmUsage(tokensIn, tokensOut, cacheRead, cacheWrite),
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
        var task = _tasks.Insert(TaskRecord.Create(TaskType.Task, "T", "O", budget));
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
        var client = new MeteredLlmClient(inner, _conn, TestPrices.Catalog);

        await client.CompleteAsync(Request(taskId));

        var entry = Assert.Single(new LedgerRepository(_conn).List(taskId));
        Assert.Equal(1000, entry.TokensIn);
        Assert.Equal(500, entry.TokensOut);
        Assert.Equal(1500, _tasks.Get(taskId).TokensSpent);
    }

    [Fact]
    public async Task Crossing_70_percent_injects_one_system_nudge()
    {
        var taskId = StartTask(budget: 1000);
        var client = new MeteredLlmClient(new FakeLlmClient(300, 100), _conn, TestPrices.Catalog);
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
        var inner = new FakeLlmClient(600, 600);
        var client = new MeteredLlmClient(inner, _conn, TestPrices.Catalog);

        // The first call is allowed and takes the instance past its allowance; the
        // second is refused, because the cap is read back from what this instance ran.
        await client.CompleteAsync(Request(taskId));
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(taskId)));

        Assert.Equal(1, inner.Calls); // enforcement = not making the call
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
            new FakeLlmClient(600, 500), _conn, TestPrices.Catalog, projectBudgetUsd: 0.005m);

        // One call at sonnet rates: 600 × $2/Mtok + 500 × $10/Mtok = $0.0062, past the cap.
        await client.CompleteAsync(Request(null));
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(null)));
        Assert.Single(new MessageRepository(_conn).Pending("pm"));
    }

    [Fact]
    public async Task All_four_token_buckets_and_the_cost_are_ledgered()
    {
        var taskId = StartTask(budget: 10_000);
        var client = new MeteredLlmClient(new FakeLlmClient(1000, 500, 10_000, 2000), _conn, TestPrices.Catalog);

        await client.CompleteAsync(Request(taskId));

        var entry = Assert.Single(new LedgerRepository(_conn).List(taskId));
        Assert.Equal(1000, entry.TokensIn);
        Assert.Equal(500, entry.TokensOut);
        Assert.Equal(10_000, entry.CacheReadTokens);
        Assert.Equal(2000, entry.CacheWriteTokens);

        // sonnet: 1000×$2/M + 500×$10/M + 10000×$0.20/M + 2000×$2.50/M
        Assert.Equal(0.014m, entry.CostUsd);
        Assert.False(string.IsNullOrWhiteSpace(entry.PricedWith));
    }

    [Fact]
    public async Task A_second_instance_on_the_same_task_gets_its_own_allowance()
    {
        // The failure this exists for: an engineer that used its allowance left the
        // reviewer nothing, so the review was refused on turn 1 and the task was struck
        // for work that had actually been submitted fine.
        var taskId = StartTask(budget: 1000);
        var inner = new FakeLlmClient(600, 600);
        var client = new MeteredLlmClient(inner, _conn, TestPrices.Catalog);

        await client.CompleteAsync(Request(taskId));
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(taskId)));

        // A different instance on the same task is not charged for what the first spent.
        var reviewer = Request(taskId) with
        {
            Attribution = new LlmAttribution("rev-20260719-100500", AgentRole.Principal, taskId),
        };
        await client.CompleteAsync(reviewer);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task The_budget_counts_every_token_the_call_processed()
    {
        // Counting only the uncached remainder made a budget mean wildly different
        // amounts of work on a provider that caches and one that does not.
        var taskId = StartTask(budget: 10_000);
        var client = new MeteredLlmClient(new FakeLlmClient(100, 50, 500_000, 90_000), _conn, TestPrices.Catalog);

        await client.CompleteAsync(Request(taskId));

        Assert.Equal(100 + 50 + 500_000 + 90_000, _tasks.Get(taskId).TokensSpent);
    }

    [Fact]
    public async Task Spend_is_attributed_per_role()
    {
        var client = new MeteredLlmClient(new FakeLlmClient(1000, 1000), _conn, TestPrices.Catalog);

        await client.CompleteAsync(Request(null) with
        {
            Attribution = new LlmAttribution("pm-1", AgentRole.Pm, null),
        });
        await client.CompleteAsync(Request(null) with
        {
            Attribution = new LlmAttribution("eng-1", AgentRole.Engineer, null),
        });
        await client.CompleteAsync(Request(null) with
        {
            Attribution = new LlmAttribution("eng-2", AgentRole.Engineer, null),
        });

        var spend = new LedgerRepository(_conn).SpendByRole().ToDictionary(r => r.Role);
        Assert.Equal(2, spend[AgentRole.Engineer].Calls);
        Assert.Equal(1, spend[AgentRole.Pm].Calls);
        Assert.Equal(spend[AgentRole.Pm].CostUsd * 2, spend[AgentRole.Engineer].CostUsd);
    }

    [Fact]
    public async Task An_unpriced_model_refuses_rather_than_recording_a_free_call()
    {
        var taskId = StartTask(budget: 10_000);
        var client = new MeteredLlmClient(new FakeLlmClient(10, 10), _conn, TestPrices.Catalog);

        await Assert.ThrowsAsync<ModelNotPricedException>(() =>
            client.CompleteAsync(Request(taskId) with { Model = "some-unknown-model" }));
    }

    [Fact]
    public void The_supervisor_forwards_tier_resolution_to_the_provider()
    {
        var client = new MeteredLlmClient(new FakeLlmClient(1, 1), _conn, TestPrices.Catalog);

        Assert.Equal("claude-opus-4-8", client.ModelFor(ModelTier.Reasoning));
        Assert.Equal("claude-sonnet-5", client.ModelFor(ModelTier.Coding));
    }

    [Fact]
    public async Task Raising_the_live_budget_takes_effect_on_the_next_call_without_a_restart()
    {
        decimal? cap = 0.001m;   // below one call's cost
        var client = new MeteredLlmClient(
            new FakeLlmClient(600, 500), _conn, TestPrices.Catalog, liveBudgetUsd: () => cap);

        await client.CompleteAsync(Request(null));   // spends past the cap
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => client.CompleteAsync(Request(null)));

        cap = 100m;   // the client pressed Raise — same instance, no restart
        var response = await client.CompleteAsync(Request(null));
        Assert.Equal("ok", response.Content);
    }
}
