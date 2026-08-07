using System.Data;
using Forge.Core.Db;
using Forge.Core.Llm.Pricing;
using Forge.Core.Model;

namespace Forge.Core.Llm;

/// <summary>
/// Wraps a provider adapter and supervises every call: refuses it by throwing once the
/// project's dollar cap or the task's token budget is spent, escalates a spent project cap
/// to the PM once, writes a token_ledger row and bumps tasks.tokens_spent after each call,
/// and injects a system_nudge at 70% of the task budget.
///
/// The project budget is USD, priced from all four token buckets. The task budget is tokens,
/// measured per agent instance.
/// </summary>
public sealed class MeteredLlmClient(
    ILlmClient inner,
    IDbConnection projectConn,
    PriceCatalog prices,
    decimal? projectBudgetUsd = null,
    Func<decimal?>? liveBudgetUsd = null) : ILlmClient
{
    private const double NudgeThreshold = 0.70;

    private readonly TaskRepository _tasks = new(projectConn);
    private readonly MessageRepository _messages = new(projectConn);
    private readonly LedgerRepository _ledger = new(projectConn);

    public string ModelFor(ModelTier tier) => inner.ModelFor(tier);

    /// <summary>Checks the budgets, makes the call, then ledgers what it cost.</summary>
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        RefuseIfExhausted(request.Attribution);

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);

        Record(request, response.Usage);
        return response;
    }

    /// <summary>
    /// Throws when the project's dollar cap or this instance's token budget is spent.
    /// The fixed CLI budget wins; otherwise the live source is re-read on every call, so a
    /// cap raised mid-build applies to the next one.
    /// </summary>
    private void RefuseIfExhausted(LlmAttribution attribution)
    {
        if ((projectBudgetUsd ?? liveBudgetUsd?.Invoke()) is { } budget)
        {
            var spent = _ledger.ProjectTotals().CostUsd;
            if (spent >= budget)
            {
                // One escalation per exhausted cap, not one per refused call.
                if (!_messages.Pending("pm").Any(m =>
                        m.Type == MessageType.Escalation && m.Payload.StartsWith("Project budget exhausted")))
                {
                    QueueBudgetEscalation(attribution, "Project", $"${spent:F4}", $"${budget:F2}");
                }
                throw BudgetExhaustedException.Usd("Project", spent, budget);
            }
        }

        if (attribution.TaskId is not { } taskId) return;

        // Totalled per agent instance, over all four token buckets.
        var task = _tasks.Get(taskId);
        var processed = _ledger.InstanceTotals(attribution.AgentInstanceId).TotalTokens;
        if (processed < task.TokenBudget) return;

        // Throwing is the whole enforcement; TaskRunner.Park decides what happens to the task.
        throw BudgetExhaustedException.Tokens($"Task {taskId}", processed, task.TokenBudget);
    }

    /// <summary>Files a pending escalation to the PM naming the spent cap and the refused call.</summary>
    private void QueueBudgetEscalation(LlmAttribution attribution, string scope, string spent, string budget) =>
        _messages.Insert(Message.Create(
            MessageType.Escalation, "system", "pm",
            $"{scope} budget exhausted ({spent} of {budget}); LLM call by " +
            $"{attribution.AgentInstanceId} refused" +
            (attribution.TaskId is { } t ? $"; task {t} blocked." : "."),
            attribution.TaskId));

    /// <summary>
    /// Writes the call to the token ledger, adds its tokens to the task's running total, and
    /// nudges the agent when it crosses 70% of the task budget.
    /// </summary>
    private void Record(LlmRequest request, LlmUsage usage)
    {
        var attribution = request.Attribution;

        // Priced at call time; the four buckets are stored alongside so a row stays recomputable.
        var price = prices.For(request.Model);

        _ledger.Append(new TokenLedgerEntry
        {
            AgentInstanceId = attribution.AgentInstanceId,
            Role = attribution.Role,
            TaskId = attribution.TaskId,
            Model = request.Model,
            TokensIn = usage.TokensIn,
            TokensOut = usage.TokensOut,
            CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            CostUsd = price.CostOf(usage),
            PricedWith = prices.Snapshot.Id,
        });

        if (attribution.TaskId is not { } taskId) return;

        // tasks.tokens_spent is a reporting total for the board; it gates nothing.
        var processed = usage.TokensIn + usage.TokensOut + usage.CacheReadTokens + usage.CacheWriteTokens;
        var task = _tasks.Get(taskId);
        _tasks.AddTokensSpent(taskId, (int)processed);

        // The nudge fires on the instance's 70% mark, the same total the cap refuses on.
        var before = _ledger.InstanceTotals(attribution.AgentInstanceId).TotalTokens - processed;
        var threshold = task.TokenBudget * NudgeThreshold;
        if (before < threshold && before + processed >= threshold)
        {
            _messages.Insert(Message.Create(
                MessageType.SystemNudge, "system",
                SnakeCaseEnum.ToSnakeCase(attribution.Role),
                $"Task {taskId} has used {before + processed} of {task.TokenBudget} budgeted tokens (≥70%). " +
                "Wrap up now, or write a progress note and escalate.",
                taskId));
        }
    }
}
