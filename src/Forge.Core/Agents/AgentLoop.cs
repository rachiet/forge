using System.Data;
using System.Text;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

public sealed record AgentRunResult(
    string InstanceId,
    EndReason End,
    int Iterations,
    string? ProgressNote,
    string? Detail = null,
    string? Reply = null,
    bool? ReviewApproved = null,
    string? ReviewFeedback = null,
    string? ReviewConvention = null,
    string? RejectedBugReason = null);

/// <summary>
/// The harness's inner loop (spec §4.1):
///   assemble context → call LLM → parse tool calls → execute jailed tools →
///   append observations → repeat until done | budget | iteration cap | escalation.
///
/// Stateful within a run (the conversation is the working memory), stateless
/// across runs (nothing survives but the workspace, the progress note, and rows
/// in the DB). Everything in here is trusted mechanical code; everything coming
/// back from the model is untrusted output under supervision (Principle 6).
/// </summary>
public sealed class AgentLoop(
    ILlmClient llm,
    IDbConnection conn,
    PromptAssembler assembler,
    AgentRecipe recipe,
    ForgeLogger? logger = null)
{
    /// <summary>Turns with no tool call are dead weight; three in a row means the model is lost.</summary>
    private const int MaxEmptyTurns = 3;

    /// <summary>
    /// The turn-budget analogue of the supervisor's 70% token nudge
    /// (<see cref="MeteredLlmClient"/>): the fraction of the iteration cap at which
    /// the model is first told to wrap up. Kept equal to the token threshold so a
    /// run out of turns and a run out of tokens feel the same to the agent.
    /// </summary>
    private const double IterationNudgeThreshold = 0.70;

    /// <summary>Validated here, not only in the factories: `with` bypasses those.</summary>
    private readonly AgentRecipe _recipe = recipe.Validate();

    private readonly TaskRepository _tasks = new(conn);
    private readonly MessageRepository _messages = new(conn);
    private readonly AgentInstanceRepository _instances = new(conn);
    private readonly ForgeLogger _baseLog = logger ?? ForgeLogger.Null;

    /// <summary>Work a task: the packet is the opening turn.</summary>
    public Task<AgentRunResult> RunAsync(
        TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(
            assembler.SystemPrompt(_recipe, task, executor.Jail),
            [new LlmMessage("user", PromptAssembler.TaskPacket(task))],
            executor, task, ct);

    /// <summary>
    /// Triage a stuck task: the opening turn is a just-in-time packet describing the
    /// block and how to resolve it (redirect / decompose / escalate), assembled by the
    /// runner — not the role prompt. Task-scoped like task work, so the Principal's
    /// note and instance land on the task it is unblocking.
    /// </summary>
    public Task<AgentRunResult> RunTriageAsync(
        string packet, TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(
            assembler.SystemPrompt(_recipe, task, executor.Jail),
            [new LlmMessage("user", packet)],
            executor, task, ct);

    /// <summary>
    /// Review a task's diff. Chat-shaped (the diff is the opening turn), but the task is
    /// bound so the review's tools can act on it — specifically `reject_bug`, which closes
    /// an invalid bug rather than looping request_changes on a fix for a non-bug.
    /// </summary>
    public Task<AgentRunResult> RunReviewAsync(
        IReadOnlyList<LlmMessage> conversation, TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(assembler.ChatSystemPrompt(_recipe, executor.Jail), conversation, executor, task, ct);

    /// <summary>Answer a client: the conversation so far is the opening state.</summary>
    public Task<AgentRunResult> RunChatAsync(
        IReadOnlyList<LlmMessage> conversation, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(assembler.ChatSystemPrompt(_recipe, executor.Jail), conversation, executor, task: null, ct);

    private async Task<AgentRunResult> RunAsync(
        string system,
        IReadOnlyList<LlmMessage> seed,
        ToolExecutor executor,
        TaskRecord? task,
        CancellationToken ct)
    {
        // Task-scoped when working a task, project-scoped for a chat turn — so
        // every line this run emits carries the right correlation automatically.
        var log = task is { } scoped ? _baseLog.For(scoped.Id) : _baseLog;

        // Resolved once per instance, not per turn: the model must not change under a
        // conversation. The recipe asks for a tier; the configured client names it.
        var model = llm.ModelFor(_recipe.Tier);

        var instanceId = _instances.NewId(_recipe.InstancePrefix);
        _instances.Start(instanceId, _recipe.Role, model, task?.Id);
        log.Event(EventType.InstanceStart,
            $"{instanceId} ({SnakeCaseEnum.ToSnakeCase(_recipe.Role)}, {model})");

        var toolset = new AgentToolset(executor, conn, _recipe, task, log);
        var attribution = new LlmAttribution(instanceId, _recipe.Role, task?.Id);

        List<LlmMessage> conversation = [.. seed];
        var iterations = 0;
        var emptyTurns = 0;
        // The model's most recent output. When an instance dies without writing its
        // own progress note, this becomes the resume note (`ProgressStatus: …`), so
        // the next instance — and we — see exactly what it was thinking when it stopped.
        string? lastMessage = null;

        for (var turn = 1; turn <= _recipe.IterationCap; turn++)
        {
            iterations = turn;

            LlmResponse response;
            try
            {
                response = await llm.CompleteAsync(new LlmRequest
                {
                    Model = model,
                    System = system,
                    // Snapshot, not the live list: a request is a value, and an
                    // adapter that queues or retries must not see later turns appear
                    // inside a request it already accepted.
                    Messages = [.. conversation],
                    MaxTokens = _recipe.MaxTokens,
                    Attribution = attribution,
                }, ct).ConfigureAwait(false);
            }
            catch (BudgetExhaustedException ex)
            {
                // The supervisor refused the call; the loop's only job is to stop.
                // TaskRunner.Park then moves the task to OutOfBudget and counts the strike.
                log.Event(EventType.LlmRefused, ex.Message);
                return Finish(instanceId, EndReason.Budget, iterations, toolset, task, log, ex.Message, lastMessage);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Genuine cancellation — Ctrl-C, a shutdown signal — is OUR token being
                // tripped. Let it propagate and actually stop the run; it is not a crash.
                throw;
            }
            catch (Exception ex)
            {
                // The provider is a network boundary: auth failures, rate limits, outages —
                // and timeouts, which the HTTP stack raises as a TaskCanceledException even
                // though we never cancelled (ct is not tripped, so the guard above passed it
                // through to here). Treat all of it as a transient crash: park the work with
                // its workspace and note intact so a fresh instance resumes, rather than
                // letting a 429 or a timeout escape and kill the whole run.
                log.Event(EventType.ErrorProvider, $"turn {turn}: {ex.Message}");
                return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                    $"LLM call failed on turn {turn}: {ex.Message}", lastMessage);
            }

            log.Event(EventType.LlmCall,
                $"turn {turn}: {response.Usage.TokensIn + response.Usage.TokensOut} tokens " +
                $"(in {response.Usage.TokensIn} / out {response.Usage.TokensOut}" +
                (response.Usage.CacheReadTokens + response.Usage.CacheWriteTokens > 0
                    ? $" / cache read {response.Usage.CacheReadTokens} write {response.Usage.CacheWriteTokens}"
                    : "") + ")");

            conversation.Add(new LlmMessage("assistant", response.Content));
            lastMessage = response.Content;

            var calls = ToolCallParser.Parse(response.Content);
            if (calls.Count == 0)
            {
                if (++emptyTurns >= MaxEmptyTurns)
                {
                    return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                        $"No tool call in {MaxEmptyTurns} consecutive turns; the model is not acting.", lastMessage);
                }
                conversation.Add(new LlmMessage("user",
                    "No tool call found in your last turn. Nothing happened. Emit a " +
                    "<tool name=\"...\"> block, or finish your turn with the tool that ends it."));
                continue;
            }

            emptyTurns = 0;
            var observations = new StringBuilder();
            EndReason? end = null;

            foreach (var call in calls)
            {
                var outcome = await toolset.ExecuteAsync(call, ct).ConfigureAwait(false);
                observations.AppendLine($"[{call.Name}]").AppendLine(outcome.Observation).AppendLine();
                if (outcome.End is not { } reason) continue;
                end = reason;
                break; // the ending tool ends the turn; later calls in the batch are moot.
            }

            if (end is { } finalReason)
                return Finish(instanceId, finalReason, iterations, toolset, task, log,
                    observations.ToString().Trim(), lastMessage);

            AppendPendingMessages(task?.Id, observations, log);
            AppendIterationNudge(turn, observations, log);
            conversation.Add(new LlmMessage("user", observations.ToString().TrimEnd()));
        }

        return Finish(instanceId, EndReason.Iterations, iterations, toolset, task, log,
            $"Iteration cap of {_recipe.IterationCap} turns reached.", lastMessage);
    }

    /// <summary>
    /// Deliver anything the harness queued for this role mid-loop — most importantly
    /// the supervisor's 70% budget nudge, which is useless if it only lands in a table.
    /// </summary>
    private void AppendPendingMessages(long? taskId, StringBuilder observations, ForgeLogger log)
    {
        foreach (var message in _messages.Pending(SnakeCaseEnum.ToSnakeCase(_recipe.Role)))
        {
            if (message.TaskId != taskId) continue;
            observations
                .AppendLine($"[message: {SnakeCaseEnum.ToSnakeCase(message.Type)} from {message.FromAgent}]")
                .AppendLine(message.Payload)
                .AppendLine();
            _messages.SetStatus(message.Id, MessageStatus.Done);
            if (message.Type == MessageType.SystemNudge)
                log.Event(EventType.LlmNudge, message.Payload);
        }
    }

    /// <summary>
    /// The turn-budget counterpart of the 70% token nudge. Tokens are metered by
    /// the supervisor, which can inject a nudge from outside the loop; turns are
    /// counted here, so the loop must warn about them itself. Without this the model
    /// gets no signal that it is running out of turns and is simply cut off cold at
    /// the cap — the exact failure that stranded a finished-but-undeclared scaffold
    /// in `blocked` (it had built and tested, but spent its last turns exploring
    /// instead of calling `done`). We fire twice: once on crossing the 70% mark
    /// ("wrap up"), and a hard warning on the final turn ("land the plane now").
    /// </summary>
    private void AppendIterationNudge(int turn, StringBuilder observations, ForgeLogger log)
    {
        var cap = _recipe.IterationCap;
        var remaining = cap - turn;
        if (remaining <= 0) return; // no next turn to influence.

        string? nudge = null;
        if (remaining == 1)
            nudge = $"⛔ STOP. This is your LAST turn (turn {turn} of {cap}) — there is NO turn after this. " +
                    "You MUST make your only action one of: `done` (work complete and verified), or " +
                    "`progress_note` (exactly what is done, what is left, and the next action), or `escalate`. " +
                    "No other output is acceptable. Do NOT read, run, or explore — any other response is " +
                    "discarded and your uncommitted work is lost. Call one of those three tools now.";
        else if (turn == NudgeTurn(cap))
            nudge = $"You are on turn {turn} of {cap} (≥70% of your turn budget). Wrap up: finish with " +
                    "`done`/`escalate`, or write a `progress_note` (what is done, what is left, the exact " +
                    "next action). Do not start work you cannot finish in the turns that remain.";

        if (nudge is null) return;
        observations.AppendLine().AppendLine(nudge);
        log.Event(EventType.LlmNudge, nudge);
    }

    /// <summary>The turn at which the 70% wrap-up nudge fires; at least 1 for tiny caps.</summary>
    private static int NudgeTurn(int cap) => Math.Max(1, (int)Math.Ceiling(cap * IterationNudgeThreshold));

    /// <summary>
    /// Close out the instance. For task work a progress note is written
    /// unconditionally: the resume path depends on one existing, so it cannot be
    /// left to the model's discretion (Principle 6 — the harness enforces).
    /// </summary>
    private AgentRunResult Finish(
        string instanceId, EndReason end, int iterations,
        AgentToolset toolset, TaskRecord? task, ForgeLogger log, string? detail, string? lastMessage = null)
    {
        var note = toolset.LastProgressNote;
        if (note is null && task is not null)
        {
            // The instance died without ending its own turn (budget/iteration/crash).
            // If it left any final words, keep them verbatim as the resume note — a
            // `ProgressStatus:` from the model beats a generic harness sentence, and
            // it's the only record of what it was doing (the conversation is discarded).
            note = lastMessage is { Length: > 0 }
                ? $"ProgressStatus [ended {SnakeCaseEnum.ToSnakeCase(end)} after {iterations} turns]: {lastMessage.Trim()}"
                : $"Instance {instanceId} ended ({SnakeCaseEnum.ToSnakeCase(end)}) after {iterations} turns " +
                  $"with no output. {detail}".Trim();
            _tasks.SetProgressNote(task.Id, note);
        }

        _instances.End(instanceId, end);
        log.Event(EventType.InstanceEnd,
            $"{instanceId} ended: {SnakeCaseEnum.ToSnakeCase(end)} after {iterations} turns");
        return new AgentRunResult(instanceId, end, iterations, note, detail, toolset.LastReply,
            toolset.ReviewApproved, toolset.ReviewFeedback, toolset.ReviewConvention, toolset.RejectedBugReason);
    }
}
