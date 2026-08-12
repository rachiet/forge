using System.Data;
using System.Text;
using System.Text.Json;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>What one agent instance produced.</summary>
/// <param name="InstanceId">The agent_instances row this run wrote.</param>
/// <param name="End">Why the loop stopped.</param>
/// <param name="Iterations">Turns taken.</param>
/// <param name="ProgressNote">The last note the agent saved, for its successor.</param>
/// <param name="Detail">A human-readable line about how it ended.</param>
/// <param name="Reply">What a chat role said to the client.</param>
/// <param name="ReviewApproved">A reviewer's verdict; null when this was not a review.</param>
/// <param name="ReviewFeedback">The reviewer's reason or approval note.</param>
/// <param name="ReviewConvention">A rule the reviewer wants appended to CONVENTIONS.md.</param>
/// <param name="RejectedBugReason">Why a bug was rejected, when one was.</param>
/// <param name="ProjectBudgetExhausted">
/// With End == Budget, means the PROJECT dollar cap refused the call rather than the task's
/// token budget. The runner pauses the build instead of striking the task.
/// </param>
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
    string? RejectedBugReason = null,
    bool ProjectBudgetExhausted = false);

/// <summary>
/// Runs one agent instance: assemble the context, call the model, parse its tool calls,
/// execute them in the jail, append the observations, and repeat until the agent finishes or
/// the harness stops it on budget, turns, refusals or an escalation.
///
/// The conversation is the working memory within a run. Across runs nothing survives but the
/// workspace, the progress note, and rows in the database.
/// </summary>
public sealed class AgentLoop(
    ILlmClient llm,
    IDbConnection conn,
    PromptAssembler assembler,
    AgentRecipe recipe,
    ForgeLogger? logger = null)
{
    /// <summary>Consecutive turns with no tool call before the instance is stopped.</summary>
    private const int MaxEmptyTurns = 3;

    /// <summary>
    /// How many consecutive turns may have every tool call refused before the instance
    /// ends as a crash. A turn with at least one accepted call resets the count.
    /// </summary>
    private const int MaxRefusedTurns = 5;

    /// <summary>Fraction of the iteration cap at which the agent is first told to wrap up.</summary>
    private const double IterationNudgeThreshold = 0.70;

    /// <summary>Re-validated here because `with` on a recipe bypasses the factory checks.</summary>
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
            [new LlmMessage("user", PromptAssembler.TaskPacket(
                task,
                new DiscussionRepository(conn).ClientGuidance(task.Id),
                // Read from the workspace, so the slice is the contract as it stands on this branch.
                ContractSlice(task, executor.Jail.Root),
                // Everything said about this task so far, so an instance is not the first to hear it.
                new DiscussionRepository(conn).History(task.Id)))],
            executor, task, ct);

    /// <summary>
    /// The task's operations as an OpenAPI fragment, or null when it names none or the
    /// project has no contract.
    /// </summary>
    private static string? ContractSlice(TaskRecord task, string workspace) =>
        task.ContractOps.Count > 0 && Design.ApiContract.Load(workspace) is { } contract
            ? contract.Slice(task.ContractOps)
            : null;

    /// <summary>
    /// Triages a stuck task. The opening turn is a packet the runner assembles describing the
    /// block and the verdicts available. Scoped to the task, so the instance and note land on it.
    /// </summary>
    public Task<AgentRunResult> RunTriageAsync(
        string packet, TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(
            assembler.SystemPrompt(_recipe, task, executor.Jail),
            [new LlmMessage("user", packet)],
            executor, task, ct);

    /// <summary>
    /// Reviews a task's diff. The conversation is the opening state, and the task is bound so
    /// the review's tools can act on it.
    /// </summary>
    public Task<AgentRunResult> RunReviewAsync(
        IReadOnlyList<LlmMessage> conversation, TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(assembler.ChatSystemPrompt(_recipe, executor.Jail), conversation, executor, task, ct);

    /// <summary>Runs a chat turn with no task attached; the conversation is the opening state.</summary>
    public Task<AgentRunResult> RunChatAsync(
        IReadOnlyList<LlmMessage> conversation, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(assembler.ChatSystemPrompt(_recipe, executor.Jail), conversation, executor, task: null, ct);

    /// <summary>
    /// The loop every entry point shares: call the model, run the tool calls it emitted, feed
    /// the observations back, and stop on the agent's own ending tool, the iteration cap, a
    /// refused budget, too many empty or fully-refused turns, or a provider failure.
    /// </summary>
    private async Task<AgentRunResult> RunAsync(
        string system,
        IReadOnlyList<LlmMessage> seed,
        ToolExecutor executor,
        TaskRecord? task,
        CancellationToken ct)
    {
        // Task-scoped when working a task, project-scoped for a chat turn.
        var log = task is { } scoped ? _baseLog.For(scoped.Id) : _baseLog;

        // Resolved once per instance: the model must not change under a conversation.
        var model = llm.ModelFor(_recipe.Tier);

        var instanceId = _instances.NewId(_recipe.InstancePrefix);
        _instances.Start(instanceId, _recipe.Role, model, task?.Id);
        log.Event(EventType.InstanceStart,
            $"{instanceId} ({SnakeCaseEnum.ToSnakeCase(_recipe.Role)}, {model})");

        // Disposed however the instance ends, so a server QA started with serve() is killed.
        using var toolset = new AgentToolset(executor, conn, _recipe, task, log);
        var attribution = new LlmAttribution(instanceId, _recipe.Role, task?.Id);

        List<LlmMessage> conversation = [.. seed];
        var iterations = 0;
        var emptyTurns = 0;
        var refusedTurns = 0;
        // The model's most recent output, used as the resume note when it wrote none itself.
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
                    // A snapshot, so a retrying adapter never sees later turns appear in it.
                    Messages = [.. conversation],
                    MaxTokens = _recipe.MaxTokens,
                    // The provider's own schema layer enforces the shape, so a call cannot
                    // arrive in a form the toolset would have to guess at.
                    Tools = AgentToolset.Definitions(_recipe),
                    Attribution = attribution,
                }, ct).ConfigureAwait(false);
            }
            catch (BudgetExhaustedException ex)
            {
                // The supervisor refused the call; the runner decides what happens to the task.
                log.Event(EventType.LlmRefused, ex.Message);
                return Finish(instanceId, EndReason.Budget, iterations, toolset, task, log, ex.Message, lastMessage)
                    with { ProjectBudgetExhausted = ex.ProjectCap };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Our own token: the harness is stopping. Not a crash.
                throw;
            }
            catch (Exception ex)
            {
                // Any provider failure — auth, rate limit, outage, or an HTTP timeout raised as
                // a TaskCanceledException — ends the instance as a crash, leaving the workspace
                // and note intact for a fresh instance to resume from.
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

            conversation.Add(new LlmMessage("assistant", response.Content) { ToolCalls = response.ToolCalls });
            // A turn that only called tools has no text, so its calls stand in as the last
            // thing it said — the resume note is written from this.
            lastMessage = response.Content is { Length: > 0 }
                ? response.Content
                : string.Join(" ", response.ToolCalls.Select(c => $"{c.Name}({c.ArgumentsJson})"));

            var calls = response.ToolCalls.Select(ToToolCall).ToList();
            if (calls.Count == 0)
            {
                // The turn the parser could make nothing of, recorded with the provider's own
                // reason for stopping and the start of what it sent. A response cut off at the
                // output limit and a model that simply did not call a tool are indistinguishable
                // from the outside, and this line is what tells them apart.
                log.Event(EventType.LlmNoToolCall,
                    $"turn {turn}: no tool call ({emptyTurns + 1} of {MaxEmptyTurns}), "
                    + $"stop reason {response.StopReason ?? "(none reported)"}, "
                    + $"{response.Content.Length} chars: {FirstLines(response.Content, 2)}");

                if (++emptyTurns >= MaxEmptyTurns)
                {
                    var why = $"No tool call in {MaxEmptyTurns} consecutive turns; the model is not acting. "
                            + $"Last stop reason: {response.StopReason ?? "(none reported)"}.";
                    log.Event(EventType.ErrorInternal, why);
                    return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                        why, lastMessage);
                }
                conversation.Add(new LlmMessage("user", """
                    Your last turn contained no tool call, so nothing happened and nothing
                    changed. Text alone is discarded — including a plan, a question, or a
                    request for access you already have.

                    Call a tool now. If you need to see a file first, that call is read_file.
                    """));
                continue;
            }

            emptyTurns = 0;
            var observations = new StringBuilder();
            var results = new List<LlmToolResult>();
            EndReason? end = null;
            var anyAccepted = false;

            for (var index = 0; index < calls.Count; index++)
            {
                var call = calls[index];
                var outcome = await toolset.ExecuteAsync(call, ct).ConfigureAwait(false);
                if (!outcome.Refused) anyAccepted = true;
                observations.AppendLine($"[{call.Name}]").AppendLine(outcome.Observation).AppendLine();
                // Paired to the call by the id the provider issued, so a result cannot be
                // attached to the wrong one when a turn made several.
                results.Add(new LlmToolResult(
                    response.ToolCalls[index].Id, call.Name, outcome.Observation));
                if (outcome.End is not { } reason) continue;
                end = reason;
                break; // an ending tool ends the turn; later calls in the batch do not run.
            }

            if (end is { } finalReason)
                return Finish(instanceId, finalReason, iterations, toolset, task, log,
                    observations.ToString().Trim(), lastMessage);

            // Only a turn where every call was refused counts toward the guard.
            if (anyAccepted) refusedTurns = 0;
            else if (++refusedTurns >= MaxRefusedTurns)
            {
                log.Event(EventType.ErrorInternal,
                    $"Every tool call refused for {MaxRefusedTurns} consecutive turns on turn {turn}.");
                var why = $"Every tool call was refused for {MaxRefusedTurns} consecutive turns; the " +
                          "model is not producing calls the harness accepts. Last refusals: " +
                          FirstLines(observations.ToString(), 3);
                return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                    why, lastMessage, noteOverride: why);
            }

            // The harness's own words for this turn — queued messages and the turn-cap nudge —
            // ride alongside the results rather than inside them, since they answer no call.
            var aside = new StringBuilder();
            AppendPendingMessages(task?.Id, aside, log);
            AppendIterationNudge(turn, aside, log);
            conversation.Add(new LlmMessage("user", aside.ToString().TrimEnd()) { ToolResults = results });
        }

        return Finish(instanceId, EndReason.Iterations, iterations, toolset, task, log,
            $"Iteration cap of {_recipe.IterationCap} turns reached.", lastMessage);
    }

    /// <summary>
    /// One provider call as the toolset takes it. Arguments arrive as a JSON object and the
    /// toolset reads them as text, so each value is flattened to its string form.
    /// </summary>
    private static ToolCall ToToolCall(LlmToolCall call)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(
            call.ArgumentsJson is { Length: > 0 } json ? json : "{}");
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                args[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.GetRawText();
            }
        }
        // Raw carries what the model actually sent, so a refusal can quote it back.
        return new ToolCall(call.Name, args, call.ArgumentsJson);
    }

    /// <summary>
    /// Appends any messages the harness queued for this role and task — the supervisor's budget
    /// nudge among them — to this turn's observations, and marks them received.
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
            _messages.SetStatus(message.Id, MessageStatus.Received);
            if (message.Type == MessageType.SystemNudge)
                log.Event(EventType.LlmNudge, message.Payload);
        }
    }

    /// <summary>The first <paramref name="count"/> non-blank lines, joined with " / ".</summary>
    private static string FirstLines(string text, int count)
    {
        var lines = text.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(count);
        return string.Join(" / ", lines);
    }

    /// <summary>
    /// Warns the agent that its turns are running out: once on crossing the 70% mark, and
    /// again on the final turn, telling it to end cleanly with done, progress_note or escalate.
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
    /// Closes out the instance: records its end, and for task work guarantees a progress note
    /// exists — the agent's own, the caller's override, or one built from its last output.
    /// </summary>
    private AgentRunResult Finish(
        string instanceId, EndReason end, int iterations,
        AgentToolset toolset, TaskRecord? task, ForgeLogger log, string? detail, string? lastMessage = null,
        string? noteOverride = null)
    {
        var note = toolset.LastProgressNote;
        if (note is null && noteOverride is not null && task is not null)
        {
            // The caller's note wins where the model's last words would mislead a successor.
            note = noteOverride;
            _tasks.SetProgressNote(task.Id, note);
        }
        else if (note is null && task is not null)
        {
            // Ended without writing a note: keep its last output verbatim as the resume note.
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
