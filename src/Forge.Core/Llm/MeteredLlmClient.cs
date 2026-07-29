using System.Data;
using Forge.Core.Db;
using Forge.Core.Llm.Pricing;
using Forge.Core.Model;

namespace Forge.Core.Llm;

/// <summary>
/// The supervisor as a decorator, not a convention (spec §11). Wraps any
/// provider adapter and:
///  - refuses the call outright once the task (or project) budget is spent, by
///    throwing — the task's terminal state (OutOfBudget → the Principal's queue,
///    with a strike counted) is decided in one place, TaskRunner.Park, not here;
///  - escalates a project-wide budget cap to the PM (a client money decision);
///  - writes a token_ledger row and bumps tasks.tokens_spent after every call;
///  - injects a system_nudge message when a call crosses 70% of the task budget.
///
/// The two budgets are deliberately different units, because they do different jobs.
/// The *project* budget is dollars: it is the client's real spend, priced from all
/// four token buckets against a live rate table. The *task* budget stays tokens: it
/// exists to stop one agent looping forever, and is an approximation — it counts the
/// uncached prompt remainder plus output, so it undercounts real traffic by a wide
/// margin. That is tolerated knowingly; money safety comes from the dollar cap.
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

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        RefuseIfExhausted(request.Attribution);

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);

        Record(request, response.Usage);
        return response;
    }

    private void RefuseIfExhausted(LlmAttribution attribution)
    {
        // Re-read per call when a live source is wired (the board passes the project's
        // stored settings): the client raising the cap mid-build must take effect on the
        // NEXT call, not after a restart. A fixed value (the CLI flag) still wins.
        if ((projectBudgetUsd ?? liveBudgetUsd?.Invoke()) is { } budget)
        {
            var spent = _ledger.ProjectTotals().CostUsd;
            if (spent >= budget)
            {
                // Escalate once, not once per refused call: after the cap trips, every
                // task the loop touches is refused, and each refusal landing its own
                // escalation would bury the PM in identical messages.
                if (!_messages.Pending("pm").Any(m =>
                        m.Type == MessageType.Escalation && m.Payload.StartsWith("Project budget exhausted")))
                {
                    QueueBudgetEscalation(attribution, "Project", $"${spent:F4}", $"${budget:F2}");
                }
                throw BudgetExhaustedException.Usd("Project", spent, budget);
            }
        }

        if (attribution.TaskId is not { } taskId) return;

        var task = _tasks.Get(taskId);
        if (task.TokensSpent < task.TokenBudget) return;

        // Enforcement is not making the call. Parking the task — OutOfBudget, a strike,
        // the workspace kept, the Principal notified — is TaskRunner.Park's job, so it
        // isn't split across two owners that could disagree.
        throw BudgetExhaustedException.Tokens($"Task {taskId}", task.TokensSpent, task.TokenBudget);
    }

    private void QueueBudgetEscalation(LlmAttribution attribution, string scope, string spent, string budget) =>
        _messages.Insert(Message.Create(
            MessageType.Escalation, "system", "pm",
            $"{scope} budget exhausted ({spent} of {budget}); LLM call by " +
            $"{attribution.AgentInstanceId} refused" +
            (attribution.TaskId is { } t ? $"; task {t} blocked." : "."),
            attribution.TaskId));

    private void Record(LlmRequest request, LlmUsage usage)
    {
        var attribution = request.Attribution;

        // Priced here rather than at read time so the ledger records what the call cost
        // when it was made. All four buckets are stored alongside, so a row can still be
        // re-derived later if the rate table is corrected.
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

        var before = _tasks.Get(taskId);
        _tasks.AddTokensSpent(taskId, usage.TokensIn + usage.TokensOut);
        var after = before.TokensSpent + usage.TokensIn + usage.TokensOut;

        var threshold = before.TokenBudget * NudgeThreshold;
        if (before.TokensSpent < threshold && after >= threshold)
        {
            _messages.Insert(Message.Create(
                MessageType.SystemNudge, "system",
                SnakeCaseEnum.ToSnakeCase(attribution.Role),
                $"Task {taskId} has used {after} of {before.TokenBudget} budgeted tokens (≥70%). " +
                "Wrap up now, or write a progress note and escalate.",
                taskId));
        }
    }
}
