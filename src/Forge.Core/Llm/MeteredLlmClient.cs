using System.Data;
using Forge.Core.Db;
using Forge.Core.Model;

namespace Forge.Core.Llm;

/// <summary>
/// The supervisor as a decorator, not a convention (spec §11). Wraps any
/// provider adapter and:
///  - refuses the call outright once the task (or project) budget is spent, by
///    throwing — the task's terminal state (OutOfBudget → the Principal's queue,
///    with a strike counted) is decided in one place, TaskRunner.Park, not here;
///  - escalates a project-wide budget cap to the PM (a client spend decision);
///  - writes a token_ledger row and bumps tasks.tokens_spent after every call;
///  - injects a system_nudge message when a call crosses 70% of the task budget.
///
/// The unit throughout is tokens, never dollars: token counts come back from the
/// provider on every response and are what it bills on, whereas a dollar figure
/// would have to be derived from a price table we maintain by hand and that goes
/// stale silently on the next rate change.
/// </summary>
public sealed class MeteredLlmClient(
    ILlmClient inner,
    IDbConnection projectConn,
    long? projectTokenBudget = null) : ILlmClient
{
    private const double NudgeThreshold = 0.70;

    private readonly TaskRepository _tasks = new(projectConn);
    private readonly MessageRepository _messages = new(projectConn);
    private readonly LedgerRepository _ledger = new(projectConn);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        RefuseIfExhausted(request.Attribution);

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);

        Record(request, response.Usage);
        return response;
    }

    private void RefuseIfExhausted(LlmAttribution attribution)
    {
        if (projectTokenBudget is { } projectBudget)
        {
            var totals = _ledger.ProjectTotals();
            var projectSpent = totals.TokensIn + totals.TokensOut;
            if (projectSpent >= projectBudget)
            {
                QueueBudgetEscalation(attribution, "Project", projectSpent, projectBudget);
                throw new BudgetExhaustedException("Project", projectSpent, projectBudget);
            }
        }

        if (attribution.TaskId is not { } taskId) return;

        var task = _tasks.Get(taskId);
        if (task.TokensSpent < task.TokenBudget) return;

        // Enforcement is not making the call. Parking the task — OutOfBudget, a strike,
        // the workspace kept, the Principal notified — is TaskRunner.Park's job, so it
        // isn't split across two owners that could disagree.
        throw new BudgetExhaustedException($"Task {taskId}", task.TokensSpent, task.TokenBudget);
    }

    private void QueueBudgetEscalation(LlmAttribution attribution, string scope, long spent, long budget) =>
        _messages.Insert(Message.Create(
            MessageType.Escalation, "system", "pm",
            $"{scope} budget exhausted ({spent}/{budget} tokens); LLM call by " +
            $"{attribution.AgentInstanceId} refused" +
            (attribution.TaskId is { } t ? $"; task {t} blocked." : "."),
            attribution.TaskId));

    private void Record(LlmRequest request, LlmUsage usage)
    {
        var attribution = request.Attribution;
        _ledger.Append(new TokenLedgerEntry
        {
            AgentInstanceId = attribution.AgentInstanceId,
            Role = attribution.Role,
            TaskId = attribution.TaskId,
            Model = request.Model,
            TokensIn = usage.TokensIn,
            TokensOut = usage.TokensOut,
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
