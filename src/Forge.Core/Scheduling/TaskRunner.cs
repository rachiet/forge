using System.Data;
using Dapper;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Ci;
using Forge.Core.Db;
using Forge.Core.Design;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Qa;
using Forge.Core.Review;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Scheduling;

public sealed record TaskRunOutcome(
    long TaskId, EndReason End, TaskStatus Status, string Summary,
    // The project's dollar cap refused a call. Nothing on the board failed; the loop
    // must stop pulling work until the client raises the cap.
    bool ProjectBudgetExhausted = false);

/// <summary>
/// One serial worker (spec §1: v1 is one worker; the cap is config, not
/// architecture). Claims a task, gives an agent instance a jailed workspace,
/// then decides what actually happened by looking at git — not by believing the
/// agent's report. From M4 the "what happened" includes harness-run CI and a
/// Principal review before merge, with a bounded revision loop back to the engineer.
///
/// The CI step is injectable so orchestration tests can drive the gates without a
/// real .NET toolchain; production uses CiRunner.Run.
/// </summary>
public sealed class TaskRunner(
    ForgePaths paths,
    string project,
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null,
    Func<string, CiResult>? ci = null)
{
    /// <summary>Bound the engineer↔review loop: a task that can't pass is escalated, not retried forever.</summary>
    private const int RevisionCap = 5;

    /// <summary>How many times a provider crash auto-resumes before the task is handed to the Principal.</summary>
    private const int CrashRetryCap = 2;

    /// <summary>Strikes at OutOfBudget before the Principal stops redirecting and implements the task directly.</summary>
    private const int DirectImplementStrike = 2;

    /// <summary>project_meta key holding the task ids the client was last asked about.</summary>
    private const string AskedKey = "client_asked_about";

    /// <summary>project_meta flag: the finished project has been handed to the client.</summary>
    private const string DeliveredKey = "project_delivered";

    private readonly TaskRepository _tasks = new(conn);
    private readonly MessageRepository _messages = new(conn);
    private readonly ProjectMetaRepository _meta = new(conn);
    private readonly AgentInstanceRepository _instances = new(conn);
    private readonly WorkspaceManager _workspaces = new(paths, project);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;
    private readonly Func<string, CiResult> _ci = ci ?? CiRunner.Run;

    /// <summary>
    /// Resume before claim, deliberately: a task left in_progress is a task whose
    /// worker was killed, and its workspace is still on disk. Picking up new work
    /// while abandoned work exists is how a queue leaks tasks.
    /// </summary>
    public TaskRecord? NextTask(AgentRole role)
    {
        var roleName = SnakeCaseEnum.ToSnakeCase(role);
        var row = conn.QueryFirstOrDefault<long?>("""
            SELECT id FROM tasks
            WHERE assigned_role = @roleName AND status IN ('in_progress', 'claimed')
            ORDER BY id LIMIT 1
            """, new { roleName })
            ?? conn.QueryFirstOrDefault<long?>("""
            SELECT t.id FROM tasks t
            WHERE t.assigned_role = @roleName AND t.status = 'ready'
              AND NOT EXISTS (
                SELECT 1 FROM task_deps d
                JOIN tasks dep ON dep.id = d.depends_on
                WHERE d.task_id = t.id AND dep.status != 'done')
            ORDER BY t.id LIMIT 1
            """, new { roleName });

        return row is { } id ? _tasks.Get(id) : null;
    }

    public async Task<TaskRunOutcome?> RunNextAsync(AgentRole role, CancellationToken ct = default)
    {
        var task = NextTask(role);
        return task is null ? null : await RunAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One step of the autonomous loop, by priority: a Principal-owned task
    /// (blocked/out-of-budget) is cleared first — it usually gates the DAG — and only
    /// then does the engineer advance. Returns null when neither has claimable work,
    /// which is what drains the board.
    /// </summary>
    public async Task<TaskRunOutcome?> RunNextByPriorityAsync(CancellationToken ct = default)
    {
        DiscardCancelledWork();
        // Work already part-way through the pipeline is finished before anything new is
        // started, so a task cannot sit one step from done while fresh work piles up.
        if (_tasks.NextInStatus(TaskStatus.Merging) is { } approved)
            return MergeApproved(approved);
        if (_tasks.NextInStatus(TaskStatus.InReview) is { } submitted)
            return await ReviewAsync(submitted, ct).ConfigureAwait(false);
        if (_tasks.NextPrincipalOwned() is { } stuck)
            return await TriageOrImplementAsync(stuck, ct).ConfigureAwait(false);
        if (await AskClientAboutStuckWorkAsync(ct).ConfigureAwait(false) is { } asked)
            return asked;
        if (NextTask(AgentRole.Engineer) is { } next)
            return await RunAsync(next, ct).ConfigureAwait(false);
        // No task work left. First close any Feature whose children have all finished —
        // that transition (active → done) is what makes the board quiescent — then, if
        // the board is complete but not yet QA-verified, run QA; otherwise done → null.
        CloseFinishedFeatures();
        return await MaybeRunQaAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Principal's two-strike ladder for a stuck task. Strike 1 (or a plain
    /// blocked task): triage — diagnose and redirect/decompose/escalate. Strike 2 on
    /// an out-of-budget task: implement it directly. Past that: give up to a human.
    /// A filed bug in `triage` is a different job: accept or reject it.
    /// </summary>
    private async Task<TaskRunOutcome> TriageOrImplementAsync(TaskRecord task, CancellationToken ct)
    {
        var log = _log.For(task.Id);
        // Triage is entered by two kinds of task, routed by type: a PM-opened Feature is
        // decomposed into child tasks; a QA-filed bug is accepted or rejected.
        if (task.Status == TaskStatus.Triage)
            return task.Type switch
            {
                TaskType.Feature => await DecomposeFeatureAsync(task, ct).ConfigureAwait(false),
                TaskType.Bug => await TriageBugAsync(task, ct).ConfigureAwait(false),
                // A plain task reaches triage when the client sent it back with guidance.
                _ => await TriageAsync(task, ct).ConfigureAwait(false),
            };
        if (task.Status == TaskStatus.OutOfBudget)
        {
            // Past the last strike the engineer and the Principal have both failed on this
            // task, which is the strongest evidence available that it is too big rather than
            // merely hard. One final triage with `redirect` taken away: split it or give the
            // client the decision. GiveUp still catches a triage that resolves nothing.
            if (task.OutOfBudgetCount > DirectImplementStrike)
                return await TriageAsync(task, ct, AgentRecipe.PrincipalFinalTriage).ConfigureAwait(false);
            if (task.OutOfBudgetCount >= DirectImplementStrike)
                return await ImplementDirectlyAsync(task, ct).ConfigureAwait(false);
        }
        return await TriageAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A PM-opened Feature sits in `triage` until the loop hands it here. The Principal
    /// decomposes it — reusing the design phase, which already picks the greenfield vs
    /// change-request brief — and then the harness back-fills `parent_id` on the tasks
    /// that were just created and releases them to `ready` (autonomous: no client
    /// sign-off step), and moves the Feature to `active` so the loop never re-pulls it.
    /// The Feature closes to `done` later, when every child has finished (the sweep).
    /// </summary>
    private async Task<TaskRunOutcome> DecomposeFeatureAsync(TaskRecord feature, CancellationToken ct)
    {
        var log = _log.For(feature.Id);
        log.Message($"Principal decomposing feature {feature.Id}: {feature.Title}");

        var before = _tasks.List().Select(t => t.Id).ToHashSet();
        var design = new DesignPhase(paths, project, conn, llm, vault, prompts, _log);
        var outcome = await design.RunAsync(ct).ConfigureAwait(false);

        if (outcome.ProjectBudgetExhausted)
            return new TaskRunOutcome(feature.Id, outcome.End, feature.Status,
                $"Feature {feature.Id} decomposition paused — project budget exhausted.",
                ProjectBudgetExhausted: true);

        // No tasks means the Principal pushed back (an ill-advised CR ends in escalate,
        // which leaves a pending pm message that NextPrincipalOwned skips) or the design
        // run crashed. Either way, leave the Feature in triage rather than activating an
        // empty Feature; an escalation parks it on a human, a crash retries next tick.
        if (outcome.TasksCreated == 0)
        {
            var note = $"Feature {feature.Id} produced no tasks ({SnakeCaseEnum.ToSnakeCase(outcome.End)}): {outcome.Summary}";
            log.Message(note);
            return new TaskRunOutcome(feature.Id, outcome.End, feature.Status, note);
        }

        // An unlinked plan does not go to engineering. A requirement no operation serves can
        // never be verified — QA tests the contract — and an operation no task claims is an
        // endpoint nobody will build. The tasks already exist, so re-running design would
        // duplicate them; the Feature is parked on the client instead, naming the gap.
        if (!outcome.Contract.Complete)
        {
            var gap = $"Design is incomplete: {outcome.Contract.Describe()}.";
            log.Message(gap);
            _messages.Insert(Message.Create(MessageType.Escalation, "principal", "pm", gap, feature.Id));
            Transition(feature.Id, TaskStatus.NeedsHuman, log);
            return new TaskRunOutcome(feature.Id, EndReason.Escalated, TaskStatus.NeedsHuman, gap);
        }

        // Back-fill the linkage the harness owns: every task created in this run becomes
        // a child of the Feature and is released to the board (created → ready).
        foreach (var child in _tasks.List().Where(t => !before.Contains(t.Id) && t.Type != TaskType.Feature))
        {
            _tasks.SetParent(child.Id, feature.Id);

            // A child inherits the Feature's milestone unless the Principal named one
            // itself. Mechanical rather than asked-for: the client's progress view groups
            // by milestone, and a child that quietly lands with none would silently drop
            // its cost out of that view (Principle 6 — the harness enforces).
            if (feature.MilestoneId is { } milestone && _tasks.Get(child.Id).MilestoneId is null)
                _tasks.SetMilestone(child.Id, milestone);

            if (_tasks.Get(child.Id).Status == TaskStatus.Created)
                Transition(child.Id, TaskStatus.Ready, log);
        }

        // A new Feature is a fresh QA cycle: clear any earlier "did not converge" flag,
        // the same re-arm design approve used to do before the flow became autonomous.
        // The handover is re-armed too, so the change request is handed over when it lands.
        _meta.Set("qa_escalated", "0");
        _meta.Set(DeliveredKey, "0");
        Transition(feature.Id, TaskStatus.Active, log);

        var summary = $"Feature {feature.Id} decomposed into {outcome.TasksCreated} task(s) and activated.";
        log.Message(summary);
        return new TaskRunOutcome(feature.Id, outcome.End, TaskStatus.Active, summary);
    }

    /// <summary>Deletes the workspace and branch of every cancelled task that still has one.</summary>
    private void DiscardCancelledWork()
    {
        foreach (var task in _tasks.CancelledWithBranch())
        {
            _workspaces.DiscardWithBranch(task.Id, task.BranchName);
            _tasks.ClearBranch(task.Id);
            _log.For(task.Id).Event(EventType.GitBranch, $"discarded branch {task.BranchName} (cancelled)");
        }
    }

    /// <summary>
    /// Runs one PM turn to put the tasks waiting on the client into the chat, and returns
    /// its outcome. Null when nothing is waiting or the client has already been asked.
    /// </summary>
    /// <remarks>
    /// The question is composed once per distinct set of waiting tasks, tracked in
    /// project_meta, so a loop that runs repeatedly does not re-ask the same thing.
    /// </remarks>
    private async Task<TaskRunOutcome?> AskClientAboutStuckWorkAsync(CancellationToken ct)
    {
        var waiting = _tasks.AwaitingClient();
        if (waiting.Count == 0)
        {
            _meta.Set(AskedKey, "");
            return null;
        }

        var ids = string.Join(",", waiting.Select(t => t.Id));
        if (_meta.Get(AskedKey) == ids) return null;

        var chat = new PmChat(paths, project, conn, llm, vault, prompts, _log);
        var turn = await chat.AskAboutStuckWorkAsync(waiting, ct).ConfigureAwait(false);
        _meta.Set(AskedKey, ids);

        var summary = $"Asked the client about task(s) {ids}.";
        _log.Message(summary);
        return new TaskRunOutcome(waiting[0].Id, turn.End, TaskStatus.NeedsHuman, summary);
    }

    /// <summary>
    /// Harness-owned Feature completion (spec Principle 6 — never an agent's claim): a
    /// Feature in `active` whose children have all reached a terminal state is closed to
    /// `done`. Read from the board via `parent_id`, so "the last child finished" is
    /// derived, not tracked. Closing the Feature is what makes the board quiescent and
    /// arms QA, so this runs just before the QA gate.
    /// </summary>
    private void CloseFinishedFeatures()
    {
        foreach (var featureId in _tasks.ActiveFeaturesReadyToClose())
        {
            var log = _log.For(featureId);
            Transition(featureId, TaskStatus.Done, log);
            log.Message($"Feature {featureId} complete — all child tasks finished; QA can run.");
        }
    }

    /// <summary>
    /// A fresh Principal reads the stuck task's WIP and note, then resolves it with a
    /// tool: redirect (back to the engineer with direction), break_and_relink (replace it
    /// with the smaller tasks it just created), or escalate (a requirements question for
    /// the PM). If it resolves nothing — runs out of its own turns — the task is escalated
    /// to a human so the autonomous loop cannot spin on it.
    /// </summary>
    /// <param name="recipe">
    /// <see cref="AgentRecipe.PrincipalFinalTriage"/> on the last strike, which drops
    /// `redirect` so another attempt at the same task is not on the menu.
    /// </param>
    private async Task<TaskRunOutcome> TriageAsync(
        TaskRecord task, CancellationToken ct, AgentRecipe? finalTriage = null)
    {
        var log = _log.For(task.Id);
        var recipe = finalTriage ?? AgentRecipe.PrincipalTriage;
        log.Message($"Principal triaging {SnakeCaseEnum.ToSnakeCase(task.Status)} task {task.Id}: {task.Title}");

        // No headroom to arrange: the budget is per instance, so this triage starts at
        // zero however much the engineer that got stuck had already spent.
        var branch = task.BranchName ?? WorkspaceManager.BranchName(task);
        if (task.BranchName is null) SetBranch(task.Id, branch);
        _workspaces.Prepare(task, branch);
        var executor = new ToolExecutor(_workspaces.Path(task.Id), recipe.ToolAllowlist, vault);

        var before = _tasks.List().Select(t => t.Id).ToHashSet();
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);
        var result = await RunWithCrashRetryAsync(() =>
            loop.RunTriageAsync(TriagePacket(task, recipe.Tools.Contains("redirect")),
                _tasks.Get(task.Id), executor, ct)).ConfigureAwait(false);

        ReleaseTriageSubtasks(task, before, log);

        var status = _tasks.Get(task.Id).Status;
        if (result.ProjectBudgetExhausted)
            return new TaskRunOutcome(task.Id, result.End, status,
                $"Triage of task {task.Id} paused — project budget exhausted.", ProjectBudgetExhausted: true);
        // redirect → Ready (resolved). escalate → the Principal wants the client, so the
        // task parks on them. Anything else means triage itself failed to resolve it.
        if (result.End == EndReason.Escalated)
            return ParkOnClient(task.Id, result.ProgressNote ?? "Escalated to the client.", log);
        if (status is TaskStatus.OutOfBudget or TaskStatus.Blocked or TaskStatus.Triage)
            return GiveUp(task, log);

        return new TaskRunOutcome(task.Id, result.End, status,
            $"Triaged task {task.Id}: {result.ProgressNote ?? SnakeCaseEnum.ToSnakeCase(result.End)}.");
    }

    /// <summary>
    /// Adopt and release whatever tasks the Principal created while triaging a stuck
    /// task. Without this they were born `created` and STAYED there — the only release
    /// paths were design sign-off and Feature decomposition, neither of which runs after
    /// a triage — so a "break it down" verdict quietly deadlocked the board with work
    /// nobody could claim. Same harness-owned back-fill as decomposition: parent them to
    /// the task they decompose, inherit its milestone, and flip them claimable.
    /// </summary>
    private void ReleaseTriageSubtasks(TaskRecord triaged, IReadOnlySet<long> before, ForgeLogger log)
    {
        foreach (var child in _tasks.List().Where(t => !before.Contains(t.Id) && t.Type != TaskType.Feature))
        {
            // A replacement from break_and_relink keeps the parent and milestone that verdict
            // gave it: it files them under the SPLIT task's own feature, because the split task
            // is cancelled and a child of a cancelled task drops out of the board's feature view.
            // It still needs releasing, though — that is this method's real job, and skipping it
            // is what left "break it down" work unclaimable before.
            if (_tasks.Get(child.Id).SplitDepth == 0)
            {
                _tasks.SetParent(child.Id, triaged.Id);
                if (triaged.MilestoneId is { } milestone && _tasks.Get(child.Id).MilestoneId is null)
                    _tasks.SetMilestone(child.Id, milestone);
            }
            if (_tasks.Get(child.Id).Status == TaskStatus.Created)
                Transition(child.Id, TaskStatus.Ready, log);
            log.Message($"Triage subtask {child.Id} adopted under task {triaged.Id} and released.");
        }
    }

    /// <summary>
    /// The second strike: redirecting did not land it, so the Principal implements the
    /// task itself. A fresh, generous budget and the implementer recipe (opus + run),
    /// through the normal CI + review + merge path — the result is still verified.
    /// </summary>
    private async Task<TaskRunOutcome> ImplementDirectlyAsync(TaskRecord task, CancellationToken ct)
    {
        var log = _log.For(task.Id);
        var recipe = AgentRecipe.PrincipalImplementer;
        log.Message($"Principal implementing task {task.Id} directly (strike {task.OutOfBudgetCount}).");

        // The task's own budget may have been set for an engineer; this attempt is the
        // Principal's and gets the room its recipe asks for.
        if (task.TokenBudget < recipe.DefaultBudget) _tasks.SetBudget(task.Id, recipe.DefaultBudget);
        return await RunAsync(_tasks.Get(task.Id), recipe, ct).ConfigureAwait(false);
    }

    /// <summary>Even the Principal could not land it: block it and put the decision to a human (the PM).</summary>
    private TaskRunOutcome GiveUp(TaskRecord task, ForgeLogger log)
    {
        var note = $"Task {task.Id} still unresolved after Principal triage/implementation — needs a human decision.";
        _tasks.SetProgressNote(task.Id, note);
        log.Event(EventType.ErrorInternal, note);
        Notify(task.Id, MessageType.Escalation, "pm", note);
        return ParkOnClient(task.Id, note, log);
    }

    /// <summary>Moves a task to needs_human and reports it as escalated.</summary>
    /// <remarks>
    /// The status is what takes the task out of the autonomous queue, so the loop drains
    /// the rest of the board instead of re-triaging something only the client can answer.
    /// </remarks>
    private TaskRunOutcome ParkOnClient(long taskId, string note, ForgeLogger log)
    {
        var current = _tasks.Get(taskId).Status;
        if (current != TaskStatus.NeedsHuman && TaskTransitions.IsLegal(current, TaskStatus.NeedsHuman))
            Transition(taskId, TaskStatus.NeedsHuman, log);

        // A task arriving here is something new to raise, even if the client was asked
        // about this same id before — clearing the watermark makes the PM ask again.
        _meta.Set(AskedKey, "");
        return new TaskRunOutcome(taskId, EndReason.Escalated, _tasks.Get(taskId).Status, note);
    }

    /// <summary>
    /// The just-in-time triage briefing — injected as the Principal's opening turn, not
    /// baked into the role prompt. Names the concrete block and the allowed resolutions,
    /// mirroring how the last-turn message is injected into the engineer loop.
    /// </summary>
    /// <param name="canRedirect">
    /// False on the final triage, whose recipe has no `redirect`. The menu must match the
    /// tools: offering a verdict the harness will refuse wastes a turn and reads as a bug.
    /// </param>
    private static string TriagePacket(TaskRecord task, bool canRedirect = true)
    {
        var situation = task.Status == TaskStatus.OutOfBudget
            ? $"ran out of its token/turn budget (strike {task.OutOfBudgetCount} of {DirectImplementStrike})"
            : "is blocked — an engineer escalated, or the harness could not integrate the work";
        var redirectOption = canRedirect
            ? """
              - Wrong approach, or genuinely needed more room → `redirect(guidance, [budget])` with
                concrete, specific direction (raise the absolute budget if it ran out of tokens).
              """
            : """
              You do NOT have `redirect` this time: an engineer has failed at this task and so
              have you, so handing it back unchanged is not on the menu. Either it becomes
              smaller tasks, or the client decides what to do with it.
              """;
        return $"""
            # Triage: task {task.Id} is stuck, and it is yours to unblock
            You authored the task plan, so a stalled task is yours to fix — and it is the
            highest priority, because a stuck task usually gates others in the DAG.

            This task {situation}.

            Task: {task.Title}
            Objective: {task.Objective}
            Status: {SnakeCaseEnum.ToSnakeCase(task.Status)}
            The engineer's last note (read it, then read the workspace and its diff to see how far it got):
            {task.ProgressNote ?? "(none left)"}

            Diagnose the cause with read_file/list_dir/grep, then end your turn with ONE of:
            - Too big to finish as one task → `create_task` for each piece (add_dependency
              between them if the order matters), then `break_and_relink(new_tasks: "7,8,9")`.
              That replaces this task with them: whatever was waiting on it waits on them
              instead, and it is cancelled. Do NOT redirect afterwards — the work is theirs now.
            - A requirements/scope question only the client can answer → `escalate(reason)`.
            {redirectOption}
            Do not write code. Resolve it with one of the tools above.
            """;
    }

    // ---- QA (M5a): a project-level acceptance gate that runs only when the board is done ----

    /// <summary>How many QA↔fix rounds before a non-converging project is escalated to the client.</summary>
    private const int QaRoundCap = 5;

    private int MetaInt(string key) => int.TryParse(_meta.Get(key), out var v) ? v : 0;

    /// <summary>
    /// Run a triage/QA phase, retrying a provider crash in place (up to the crash cap)
    /// rather than escalating to a human on the first blip — the resilience task runs
    /// already get from Park, which these phases otherwise lacked. Returns the last
    /// result; if it never cleared, that result is still a Crash and the caller escalates.
    /// </summary>
    private async Task<AgentRunResult> RunWithCrashRetryAsync(Func<Task<AgentRunResult>> run)
    {
        AgentRunResult result;
        var attempt = 0;
        do { result = await run().ConfigureAwait(false); attempt++; }
        while (result.End == EndReason.Crash && attempt <= CrashRetryCap);
        return result;
    }

    /// <summary>
    /// Run QA iff the board is complete and there is new completed work to verify — the
    /// first build, a bug-fix, or a change request's tasks (any done task counts). The
    /// project is done (returns null) once a QA round produces nothing new: a round that
    /// files zero bugs, or whose bugs are all rejected, never raises the done count past
    /// the watermark. A non-converging project escalates to the client after the cap.
    /// </summary>
    private async Task<TaskRunOutcome?> MaybeRunQaAsync(CancellationToken ct)
    {
        if (!_tasks.BoardQuiescent()) return null;
        if (MetaInt("qa_escalated") == 1) return null;

        var rounds = MetaInt("qa_rounds");
        var newWorkToVerify = _tasks.CountDone() > MetaInt("qa_verified_count");
        // Verified, and nothing has finished since: the project is complete. Hand it over
        // once, then this returns null on every later tick and the loop drains.
        if (rounds > 0 && !newWorkToVerify) return await DeliverAsync(ct).ConfigureAwait(false);

        if (rounds >= QaRoundCap)
        {
            var note = $"QA and fixes did not converge after {QaRoundCap} rounds — escalating to the client.";
            _log.Event(EventType.ErrorInternal, note);
            _messages.Insert(Message.Create(MessageType.Escalation, "system", "pm", note)); // project-scoped, no task
            _meta.Set("qa_escalated", "1");
            return new TaskRunOutcome(0, EndReason.Escalated, TaskStatus.Qa, note);
        }

        return await RunQaAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the finished project out where the client can run it and has the PM tell them
    /// how. Returns null when the handover has already been done.
    /// </summary>
    /// <remarks>
    /// A bare repo cannot be run and task workspaces are deleted after merge, so without
    /// this the finished project has no directory the client could open. Re-armed by
    /// <see cref="DecomposeFeatureAsync"/>, so a change request is handed over again.
    /// </remarks>
    private async Task<TaskRunOutcome?> DeliverAsync(CancellationToken ct)
    {
        if (MetaInt(DeliveredKey) == 1) return null;

        var checkout = paths.ProjectBuild(project);
        _workspaces.PrepareTrunkClone(checkout);

        // QA's command wins when it recorded one: it started the app for real, which the
        // project files alone cannot tell us (a port, for instance).
        var delivery = _meta.Get("run_command") is { Length: > 0 } recorded
            ? new Delivery(checkout, recorded, _meta.Get("run_url"))
            : DeliveryPlan.For(checkout);

        _meta.Set(DeliveredKey, "1");
        if (delivery is null)
        {
            _log.Message("Project complete; nothing runnable found to hand over.");
            return null;
        }

        _meta.Set("run_command", delivery.Command);
        _meta.Set("run_dir", delivery.Directory);
        _log.Message($"Project complete — checked out to {checkout}; run with: {delivery.Command}");

        var chat = new PmChat(paths, project, conn, llm, vault, prompts, _log);
        var turn = await chat.AnnounceReadyAsync(delivery, ct).ConfigureAwait(false);
        return new TaskRunOutcome(0, turn.End, TaskStatus.Done, "Project handed over to the client.");
    }

    /// <summary>
    /// Black-box QA on the finished project: a QA instance reads the requirements and the
    /// contract, exercises the project through its observable side-channel, and files a bug
    /// for each requirement not met — seeded with the ledger so it does not re-file. Project-
    /// scoped (no task); runs on a fresh trunk clone like the design/PM phases.
    /// </summary>
    private async Task<TaskRunOutcome> RunQaAsync(CancellationToken ct)
    {
        var recipe = AgentRecipe.Qa;
        _log.Message("QA phase: verifying the finished project against the client's requirements");

        var workspace = _workspaces.PrepareTrunkClone(paths.RoleWorkspace(project, "qa"));
        var executor = new ToolExecutor(workspace, recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);

        var bugsBefore = _tasks.List().Count(t => t.Type == TaskType.Bug);
        var result = await RunWithCrashRetryAsync(() =>
            loop.RunChatAsync([new LlmMessage("user", QaBrief(workspace))], executor, ct)).ConfigureAwait(false);

        // A spent project cap refused QA's calls: the round never really ran, so the
        // watermark must not move (that would falsely mark the project verified) and no
        // qa_escalated flag is set — raising the budget re-runs QA as if nothing happened.
        if (result.ProjectBudgetExhausted)
            return new TaskRunOutcome(0, result.End, TaskStatus.Qa,
                "QA paused — project budget exhausted.", ProjectBudgetExhausted: true);

        // A provider outage that outlasts the retries: don't advance the watermark (that
        // would falsely mark the project QA-verified) and stop the loop; surface it to the
        // human via the PM. qa_escalated is cleared on the next design sign-off.
        if (result.End == EndReason.Crash)
        {
            var crashNote = "QA could not complete — the provider failed after retries. Re-run once it's healthy.";
            _messages.Insert(Message.Create(MessageType.Escalation, "system", "pm", crashNote));
            _meta.Set("qa_escalated", "1");
            _log.Event(EventType.ErrorProvider, crashNote);
            return new TaskRunOutcome(0, EndReason.Crash, TaskStatus.Qa, crashNote);
        }

        // The suite QA just wrote is part of the project, not scratch work: it has to
        // survive the round to be re-run against the next change.
        if (_workspaces.CommitAndPushTrunk(workspace, "test(qa): acceptance suite"))
            _log.Event(EventType.GitCommit, "committed the acceptance suite to trunk");

        _meta.Set("qa_rounds", (MetaInt("qa_rounds") + 1).ToString());

        // A project with an HTTP contract is judged by its suite; one without has no
        // operations to cover and nothing mechanical to run, so it keeps the older rule —
        // QA exercised it and filed what failed.
        if (ApiContract.Load(workspace) is not null
            && SuiteVerdict(workspace) is { } incomplete)
        {
            // No verdict: the watermark stays put, so the next tick re-runs QA and
            // QaRoundCap bounds the retries before it reaches the client.
            _log.Message(incomplete);
            return new TaskRunOutcome(0, result.End, TaskStatus.Qa, incomplete);
        }

        // Advance the watermark to the count of finished work QA has now verified. Newly
        // filed bugs are still in triage (not done), so it only rises again when an accepted
        // bug is fixed or a change request's tasks complete — exactly what re-triggers QA.
        _meta.Set("qa_verified_count", _tasks.CountDone().ToString());

        var filed = _tasks.List().Count(t => t.Type == TaskType.Bug) - bugsBefore;
        var summary = filed == 0
            ? "QA passed — every requirement met; the project is accepted."
            : $"QA filed {filed} bug(s) for the Principal to triage.";
        _log.Message($"QA round complete — {summary}");
        return new TaskRunOutcome(0, result.End, TaskStatus.Qa, summary);
    }

    /// <summary>
    /// Runs the acceptance suite and returns why the round produced no verdict, or null when
    /// it did. A red suite IS a verdict — it becomes a bug — so only "not covered" and "did
    /// not run" stop the round, and neither may be mistaken for a pass.
    /// </summary>
    private string? SuiteVerdict(string workspace)
    {
        // Coverage first, from the test source: a suite that covers half the contract can
        // go green while verifying half the project, which is the false pass this replaces.
        var contract = ApiContract.Load(workspace)!;
        var declared = AcceptanceSuite.DeclaredOperations(workspace);
        if (contract.OperationIds.Where(id => !declared.Contains(id)).ToList() is { Count: > 0 } uncovered)
            return "QA round incomplete — no acceptance test covers " + string.Join(", ", uncovered);

        var acceptance = AcceptanceSuite.Run(workspace);
        if (!acceptance.Ran)
            return $"QA round incomplete — the acceptance suite did not run: {acceptance.Output}";

        if (acceptance.Passed)
        {
            _log.Message("Acceptance suite passed against the contract.");
            return null;
        }

        FileAcceptanceFailure(acceptance);
        return null;
    }

    /// <summary>
    /// A red suite becomes one bug carrying the test output verbatim. Filed by the harness
    /// rather than by QA: the failure is process output, and a verdict that depends on the
    /// model remembering to report it is the verdict this design replaces.
    /// </summary>
    private void FileAcceptanceFailure(AcceptanceResult acceptance)
    {
        var objective = $"""
            The acceptance suite fails against the contract. Reproduce it, then make it pass.

            ## Expected
            Every test in `{AcceptanceSuite.Directory}` passes against a running instance.
            The suite is black-box and tests the contract in
            `{ApiContract.Path}` — fix the implementation, not the test.

            ## Observed — the acceptance run, captured verbatim
            ```
            {Truncate(acceptance.Output)}
            ```
            """;

        var bug = _tasks.Insert(TaskRecord.Create(
            TaskType.Bug,
            "Acceptance suite fails",
            objective,
            300_000,
            acceptanceCriteria: $"`dotnet test {AcceptanceSuite.Directory}` passes against a running instance.",
            assignedRole: AgentRole.Engineer,
            createdBy: "qa") with { Status = TaskStatus.Triage });

        _log.Message($"Bug {bug.Id} filed from the acceptance run.");
    }

    /// <summary>Keeps the END of a test log: the failure summary is at the bottom.</summary>
    private static string Truncate(string output, int max = 6_000) =>
        output.Length <= max ? output : $"... [{output.Length - max} earlier chars omitted]\n{output[^max..]}";

    private string QaBrief(string workspace)
    {
        var ledger = _tasks.BugLedger();
        var ledgerText = ledger.Count == 0
            ? "(no bugs on record yet)"
            : string.Join("\n", ledger.Select(b =>
                $"- Bug {b.Id} [{SnakeCaseEnum.ToSnakeCase(b.Status)}]: {b.Title}"));
        return $"""
            # QA: write the acceptance suite for this project

            The project is built and merged. Your job is the suite in `{AcceptanceSuite.Directory}`
            that decides whether it is accepted — you do not deliver a verdict yourself. The
            harness starts the app, runs your suite against it, and reads the result.

            {ContractSection(workspace)}

            Write xUnit tests in `{AcceptanceSuite.Directory}` (its own project, and NOT added
            to the solution file — the engineers' CI must never run it):
            - Reach the app only over HTTP at the base URL in the `{AcceptanceSuite.BaseUrlVariable}`
              environment variable. Never reference the application's projects; this is
              black-box, and a test that calls the code directly is not an acceptance test.
            - Tag every test with the operation it exercises:
              `[Trait("{AcceptanceSuite.OperationTrait}", "shorten-create")]`. Every operation in
              the contract must be covered by at least one test, and the round does not pass
              until they all are.
            - Assert what the contract states — the status codes, the response field names,
              and the error cases, not just the happy path. A test that asserts nothing passes
              and verifies nothing.
            - If a suite is already there, update it to match the contract as it now stands
              and add tests for anything new, rather than starting again.

            Run it yourself while you work — `serve` the app and `run` the tests — so you hand
            over a suite you have seen execute. When it is right, call `done`.

            {StartupSection(workspace)}

            Use `file_bug` only for something the suite cannot express — a requirement with no
            observable channel at all. A failing test is not filed by hand; the harness files
            it with the run output attached.

            Bugs already on record — do NOT re-file any of these (rejected ones are settled,
            open ones are already tracked; only a regression of a *fixed* bug is fileable again):
            {ledgerText}

            When the suite covers every operation, call `done` with a summary.
            """;
    }

    /// <summary>
    /// The operations the suite must cover, listed from the contract itself so QA is
    /// working from the same set the coverage gate will check it against.
    /// </summary>
    private static string ContractSection(string workspace)
    {
        if (ApiContract.Load(workspace) is not { } contract)
            return "This project has no OpenAPI contract, so there is nothing to cover "
                 + "mechanically. Verify it through `run` and the files it writes, and file "
                 + "a bug for anything unmet.";

        var operations = string.Join("\n", contract.Operations.Select(
            o => $"- `{o.OperationId}` — {o.Signature} (serves {o.Requirement})"));

        return $"""
            ## The contract — `{ApiContract.Path}`

            Read it in full; it states the schemas and status codes. Every operation below
            needs a test:

            {operations}
            """;
    }

    /// <summary>
    /// Tells QA exactly what to start, read out of the checkout by <see cref="AgentToolset.Discover"/>.
    /// </summary>
    /// <remarks>
    /// Handed over rather than left to be worked out, because a guessed project path fails as a
    /// BUILD error — indistinguishable, to the model, from a broken project — and there is no
    /// layout convention to fall back on: one project here is `src/BillSplitter.Web`, another is
    /// `src/Weatherboard`. A round that guessed wrong once filed four bugs it never observed.
    /// </remarks>
    private static string StartupSection(string workspace)
    {
        if (AgentToolset.Discover(workspace) is not { } target)
            return "This project has no runnable app; verify it through `run` and the files it writes.";

        var url = target.Url is { } declared
            ? $"It declares {declared} in launchSettings, so pass port {new Uri(declared).Port} to serve()."
            : "It does not declare a URL; serve() will report the one it binds.";
        var other = target.Alternatives.Count == 0
            ? ""
            : $"\n(Other runnable projects, if that one is not the app under test: {string.Join(", ", target.Alternatives)}.)";

        return $"""
            ## Starting the app — use this, do not guess a path

            The startup project, read from the repo just now, is `{target.ProjectPath}`:

                serve(command: "dotnet run --project {target.ProjectPath}")

            {url}{other}
            """;
    }

    /// <summary>
    /// A filed bug the Principal accepts (→ Ready, an engineer fixes it) or rejects
    /// (→ Rejected, kept with the reason and never re-filed). The Principal reads the
    /// bug plus the requirements on a trunk clone to decide; if it can't, the bug goes
    /// to a human so the loop can't spin on it.
    /// </summary>
    private async Task<TaskRunOutcome> TriageBugAsync(TaskRecord bug, CancellationToken ct)
    {
        var log = _log.For(bug.Id);
        var recipe = AgentRecipe.PrincipalTriage;
        log.Message($"Principal triaging bug {bug.Id}: {bug.Title}");

        var workspace = _workspaces.PrepareTrunkClone(paths.RoleWorkspace(project, "bug-triage"));
        var executor = new ToolExecutor(workspace, recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);

        var result = await RunWithCrashRetryAsync(() =>
            loop.RunTriageAsync(BugTriagePacket(bug), _tasks.Get(bug.Id), executor, ct)).ConfigureAwait(false);

        var status = _tasks.Get(bug.Id).Status;
        if (result.ProjectBudgetExhausted)
            return new TaskRunOutcome(bug.Id, result.End, status,
                $"Bug {bug.Id} triage paused — project budget exhausted.", ProjectBudgetExhausted: true);
        if (status == TaskStatus.Triage) // undecided (ran out) — hand it to a human, don't spin
        {
            var note = $"Bug {bug.Id} could not be triaged automatically — needs a human decision.";
            log.Event(EventType.ErrorInternal, note);
            Notify(bug.Id, MessageType.Escalation, "pm", note);
            return new TaskRunOutcome(bug.Id, EndReason.Escalated, TaskStatus.Triage, note);
        }
        return new TaskRunOutcome(bug.Id, result.End, status, $"Bug {bug.Id}: {SnakeCaseEnum.ToSnakeCase(status)}.");
    }

    private string BugTriagePacket(TaskRecord bug)
    {
        var ledger = _tasks.BugLedger().Where(b => b.Id != bug.Id).ToList();
        var ledgerText = ledger.Count == 0
            ? "(no other bugs on record)"
            : string.Join("\n", ledger.Select(b =>
                $"- Bug {b.Id} [{SnakeCaseEnum.ToSnakeCase(b.Status)}]: {b.Title}"));
        return $"""
            # Triage bug {bug.Id}: is it real, and not already handled?
            QA filed this against the client's requirements. Decide, then end your turn:
            accept it if it's a genuine defect, reject it if it isn't.

            {bug.Objective}

            Requirement: {bug.RequirementsRef?.ToString() ?? "(unspecified)"}
            {(bug.ProgressNote is { Length: > 0 } n ? $"\nEarlier note (may carry the client's guidance from a re-triage):\n{n}\n" : "")}
            Read the requirement and, if needed, the code to judge it. Then ONE of:
            - Real defect → `accept_bug` (an engineer will fix it).
            - Not a defect (expected behaviour, out of scope, a duplicate of a rejected/open
              bug below) → `reject_bug(reason)`. It is kept on record and never re-filed.

            Other bugs on record (a duplicate of any of these should be rejected):
            {ledgerText}
            """;
    }

    public Task<TaskRunOutcome> RunAsync(TaskRecord task, CancellationToken ct = default) =>
        RunAsync(task, AgentRecipe.For(task.AssignedRole
            ?? throw new InvalidOperationException($"Task {task.Id} has no assigned role.")), ct);

    /// <summary>
    /// Run one instance of <paramref name="recipe"/> against the task and integrate or
    /// park it. The recipe is a parameter so the Principal can implement a task directly
    /// (its own recipe) through the same build → review → merge path as an engineer.
    /// </summary>
    public async Task<TaskRunOutcome> RunAsync(TaskRecord task, AgentRecipe recipe, CancellationToken ct = default)
    {
        var log = _log.For(task.Id);

        // A task that keeps failing CI or review is a tarpit; stop feeding it. Only
        // engineer instances that ended `done` count — those are submissions that
        // reached the gates and were sent back. A budget kill, crash or iteration cap
        // is a park-and-resume, not a failed revision. The Principal implementing
        // directly is the escalation past this cap, so it is exempt.
        if (recipe.Role == AgentRole.Engineer)
        {
            // Counted since the last time someone gave new direction — a Principal triage,
            // or the client answering — not for the life of the task. All-time counting
            // made the cap unclearable: new guidance arrived, the next claim re-blocked on
            // attempts nobody could undo, and the task bounced straight back out.
            var instances = _instances.ForTask(task.Id);
            var lastDirection = instances
                .Where(i => i.Role == AgentRole.Principal)
                .Select(i => i.StartedAt)
                .DefaultIfEmpty("")
                .Max();
            var attempts = instances.Count(i =>
                i.Role == AgentRole.Engineer && i.EndReason == EndReason.Done
                && string.CompareOrdinal(i.StartedAt, lastDirection) > 0);
            if (attempts >= RevisionCap)
                return BlockExhausted(task, log, attempts);
        }

        log.Message($"Starting task {task.Id}: {task.Title}");
        var statusBeforeClaim = task.Status;
        task = Claim(task, log);
        var branch = task.BranchName ?? WorkspaceManager.BranchName(task);
        if (task.BranchName is null) SetBranch(task.Id, branch);

        _workspaces.Prepare(task, branch);
        log.Event(EventType.GitBranch, $"prepared workspace on {branch}");
        var executor = new ToolExecutor(_workspaces.Path(task.Id), recipe.ToolAllowlist, vault);

        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);
        var result = await loop.RunAsync(_tasks.Get(task.Id), executor, ct).ConfigureAwait(false);

        return result.End == EndReason.Done
            ? Submit(task, branch, result, log)
            : Park(task, result, log, statusBeforeClaim);
    }

    /// <summary>
    /// Claiming is a status transition guarded by the legal-transition map and an
    /// optimistic UPDATE, which is what makes it safe when the worker count rises
    /// above one without any change here.
    /// </summary>
    private TaskRecord Claim(TaskRecord task, ForgeLogger log)
    {
        // Ready is the normal claim; OutOfBudget/Blocked is the Principal taking a stuck
        // task over. A task already in_progress is a resume — leave it, don't re-transition.
        var status = _tasks.Get(task.Id).Status;
        if (status is TaskStatus.Ready or TaskStatus.OutOfBudget or TaskStatus.Blocked)
            Transition(task.Id, TaskStatus.Claimed, log);
        if (_tasks.Get(task.Id).Status == TaskStatus.Claimed)
            Transition(task.Id, TaskStatus.InProgress, log);
        return _tasks.Get(task.Id);
    }

    /// <summary>Every status change is one log line, so the task's walk through the board is legible.</summary>
    private void Transition(long taskId, TaskStatus to, ForgeLogger log)
    {
        var from = _tasks.Get(taskId).Status;
        _tasks.Transition(taskId, to);
        log.Event(EventType.TaskTransition,
            $"{SnakeCaseEnum.ToSnakeCase(from)} → {SnakeCaseEnum.ToSnakeCase(to)}");
    }

    private void SetBranch(long taskId, string branch) =>
        conn.Execute("UPDATE tasks SET branch_name = @branch WHERE id = @taskId", new { taskId, branch });

    /// <summary>
    /// Commits, pushes and CI-checks what the engineer produced, then submits it for
    /// review. Returns the task to the board rather than reviewing it here.
    /// </summary>
    /// <remarks>
    /// Whether the work advances is decided from ground truth, never the agent's claim:
    /// git says whether there are commits, harness-run CI says whether it builds.
    /// </remarks>
    private TaskRunOutcome Submit(
        TaskRecord task, string branch, AgentRunResult result, ForgeLogger log)
    {
        _workspaces.CommitAll(task.Id, $"task({task.Id}): {task.Title}");

        if (!_workspaces.HasCommitsAhead(task.Id, branch))
        {
            var note = "Agent reported done but produced no commits — nothing to merge.";
            _tasks.SetProgressNote(task.Id, $"{note} Previous note: {result.ProgressNote}");
            Transition(task.Id, TaskStatus.Blocked, log);
            log.Event(EventType.ErrorInternal, note);
            Notify(task.Id, MessageType.Escalation, "principal", note);
            return new TaskRunOutcome(task.Id, result.End, TaskStatus.Blocked, note);
        }

        _workspaces.PushBranch(task.Id, branch);
        log.Event(EventType.GitPush, $"pushed {branch}");

        try
        {
            // CI is harness-run and zero tokens, so it stays attached to the engineer's
            // turn: the Principal never reviews code that does not build.
            log.Event(EventType.CiRun, "dotnet build/test");
            var ci = _ci(_workspaces.Path(task.Id));
            if (!ci.Passed)
            {
                log.Event(EventType.CiFailed, ci.Summary);
                return RequestRevision(task, log, "CI",
                    $"CI failed at `{ci.Step}`. Fix the build/tests and call done again.\n\n{Shorten(ci.Output, 2000)}");
            }
            log.Event(EventType.CiPassed, ci.Summary);

            // Hand off and stop. Review is the next tick's work, claimed from the board,
            // so a worker that dies here resumes instead of stranding the task.
            Transition(task.Id, TaskStatus.InReview, log);
            var handoff = $"Submitted for review. {result.ProgressNote}".Trim();
            _tasks.SetProgressNote(task.Id, handoff);
            return new TaskRunOutcome(task.Id, result.End, TaskStatus.InReview, handoff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BlockIntegration(task, log, ex, result.End);
        }
    }

    /// <summary>
    /// Reviews a task waiting in <see cref="TaskStatus.InReview"/> and routes the verdict:
    /// approved moves to merging, changes requested goes back to the engineer, a rejected
    /// bug closes.
    /// </summary>
    private async Task<TaskRunOutcome> ReviewAsync(TaskRecord task, CancellationToken ct)
    {
        var log = _log.For(task.Id);
        var branch = task.BranchName ?? WorkspaceManager.BranchName(task);
        try
        {
            _workspaces.Prepare(task, branch);
            var verdict = await new ReviewPhase(conn, llm, vault, prompts, _log)
                .RunAsync(task, branch, _workspaces, ct).ConfigureAwait(false);

            // The reviewer judged the bug not a real defect (already transitioned to
            // Rejected). Nothing to merge or revise — discard the branch and close it.
            // This is what breaks the "fix a non-bug forever" loop.
            if (verdict.RejectedBugReason is { } rejectReason)
            {
                _workspaces.Discard(task.Id);
                log.Message($"Task {task.Id}: bug rejected in review — {rejectReason}");
                return new TaskRunOutcome(task.Id, EndReason.Done, TaskStatus.Rejected, $"Bug rejected: {rejectReason}");
            }

            // A review that never reached a verdict says nothing about the code. Leave the
            // task in in_review so the next tick reviews it again: sending it back to the
            // engineer would spend a whole implementation run answering feedback that no
            // reviewer ever gave.
            if (verdict.End is EndReason.Crash or EndReason.Iterations or EndReason.Budget)
            {
                var failed = _instances.ForTask(task.Id).Count(i =>
                    i.Id.StartsWith(AgentRecipe.PrincipalReview.InstancePrefix, StringComparison.Ordinal)
                    && i.EndReason is EndReason.Crash or EndReason.Iterations or EndReason.Budget);

                // Bounded, or a reviewer that cannot finish would be retried forever.
                if (failed > CrashRetryCap)
                    return BlockIntegration(task, log,
                        new InvalidOperationException($"review failed {failed} times"), verdict.End);

                var note = $"Review did not finish ({SnakeCaseEnum.ToSnakeCase(verdict.End)}); " +
                           $"retrying ({failed} of {CrashRetryCap}).";
                log.Event(EventType.ErrorProvider, note);
                return new TaskRunOutcome(task.Id, verdict.End, TaskStatus.InReview, note);
            }

            if (!verdict.Approved)
            {
                if (verdict.Convention is { Length: > 0 } convention) WriteConvention(convention, log);
                Transition(task.Id, TaskStatus.InProgress, log);   // back to the engineer
                return RequestRevision(task, log, "review", verdict.Feedback);
            }

            Transition(task.Id, TaskStatus.Merging, log);
            return new TaskRunOutcome(task.Id, verdict.End, TaskStatus.Merging, $"Task {task.Id} approved for merge.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BlockIntegration(task, log, ex, EndReason.Crash);
        }
    }

    /// <summary>Merges an approved task to trunk and closes it. No agent, no tokens.</summary>
    /// <remarks>
    /// Re-running is safe: merging a branch already in trunk is a no-op, so a worker that
    /// died between the merge and the transition simply repeats it on the next tick.
    /// </remarks>
    private TaskRunOutcome MergeApproved(TaskRecord task)
    {
        var log = _log.For(task.Id);
        var branch = task.BranchName ?? WorkspaceManager.BranchName(task);
        try
        {
            _workspaces.Prepare(task, branch);
            var sha = _workspaces.MergeToTrunk(task.Id, branch, $"merge {branch} into {WorkspaceManager.TrunkBranch}");
            var shortSha = sha[..Math.Min(8, sha.Length)];
            log.Event(EventType.GitMerge, $"{branch} → {WorkspaceManager.TrunkBranch} @ {shortSha}");

            // The per-task QA hop decides nothing (real QA is project-level, once the
            // board is quiescent); it survives as the documented path from merging to done.
            Transition(task.Id, TaskStatus.Qa, log);
            Transition(task.Id, TaskStatus.Done, log);
            _workspaces.Discard(task.Id);

            var summary = $"Reviewed, merged {branch} into {WorkspaceManager.TrunkBranch} at {shortSha}.";
            log.Message($"Task {task.Id} complete — {summary}");
            Notify(task.Id, MessageType.Status, "pm", $"{summary} {task.ProgressNote}".Trim());
            return new TaskRunOutcome(task.Id, EndReason.Done, TaskStatus.Done, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BlockIntegration(task, log, ex, EndReason.Crash);
        }
    }

    /// <summary>Parks a task whose post-engineer gate threw, keeping the branch to retry from.</summary>
    private TaskRunOutcome BlockIntegration(TaskRecord task, ForgeLogger log, Exception ex, EndReason end)
    {
        var note = $"Integration failed after the engineer finished: {ex.Message} " +
                   "The branch and workspace are intact; unblock the task to retry the gates.";
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Blocked && TaskTransitions.IsLegal(current, TaskStatus.Blocked))
            Transition(task.Id, TaskStatus.Blocked, log);
        _tasks.SetProgressNote(task.Id, note);
        log.Event(EventType.ErrorInternal, note);
        Notify(task.Id, MessageType.Escalation, "principal", note);
        return new TaskRunOutcome(task.Id, end, TaskStatus.Blocked, note);
    }

    /// <summary>
    /// Send the task back to the engineer. The feedback goes into the progress note
    /// so the resuming instance sees it in its packet immediately (the same resume
    /// mechanism as a kill), and a review discussion records why. The workspace is
    /// kept — the engineer revises the branch it already built.
    /// </summary>
    private TaskRunOutcome RequestRevision(TaskRecord task, ForgeLogger log, string stage, string feedback)
    {
        // CI failure leaves the task in_progress; a rejected review already stepped
        // it back to in_progress. Either way it must end claimable for the next run.
        var current = _tasks.Get(task.Id).Status;
        if (current == TaskStatus.InReview) Transition(task.Id, TaskStatus.InProgress, log);

        _tasks.SetProgressNote(task.Id, $"CHANGES REQUESTED ({stage}). {feedback}");
        new DiscussionRepository(conn).Open(task.Id, "system", $"[{stage}] {feedback}");
        log.Message($"Task {task.Id}: changes requested at {stage} — back to the engineer");
        return new TaskRunOutcome(task.Id, EndReason.Done, TaskStatus.InProgress, $"Changes requested ({stage}).");
    }

    /// <summary>
    /// The self-improving loop (spec §7): a reviewer's recurring-mistake rule is
    /// appended to CONVENTIONS.md on trunk, so it is in every future engineer's
    /// standing context — the same mistake is ruled out once, not caught repeatedly.
    /// </summary>
    private void WriteConvention(string convention, ForgeLogger log)
    {
        var wrote = _workspaces.AppendToTrunkFile(
            paths.RoleWorkspace(project, "conventions"),
            "CONVENTIONS.md", $"- {convention}", $"conventions: {Shorten(convention, 60)}");
        if (wrote) log.Event(EventType.GitCommit, $"convention added from review: {Shorten(convention, 80)}");
    }

    /// <summary>Bounded revision loop tripped: hand the task to the Principal to triage.</summary>
    /// <summary>Parks a task on the client once the engineer has run out of attempts.</summary>
    /// <remarks>
    /// Deliberately not `blocked`: the attempt count only ever rises, so a blocked task the
    /// Principal redirects is re-blocked by the very next engineer claim, and the loop spends
    /// a triage instance per cycle forever. Five failed attempts is a scope question, which
    /// is the client's to answer.
    /// </remarks>
    private TaskRunOutcome BlockExhausted(TaskRecord task, ForgeLogger log, int attempts)
    {
        var note = $"Task stopped after {attempts} engineer attempts that could not pass CI and review. " +
                   "It needs a decision on scope or approach.";
        _tasks.SetProgressNote(task.Id, note);
        log.Event(EventType.ErrorInternal, note);
        Notify(task.Id, MessageType.Escalation, "pm", note);
        return ParkOnClient(task.Id, note, log);
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// A non-`done` instance ended. The workspace is left on disk — it plus the
    /// progress note are what the next instance resumes from — and the failure class
    /// decides where the task goes:
    ///   - crash (transient): stay claimable (in_progress) and auto-resume, bounded;
    ///   - budget/iteration (out of resources): OutOfBudget → the Principal's queue;
    ///   - escalate (needs a decision): Blocked → the Principal's queue.
    /// </summary>
    private TaskRunOutcome Park(
        TaskRecord task, AgentRunResult result, ForgeLogger log, TaskStatus statusBeforeClaim)
    {
        _workspaces.CommitAll(task.Id, $"wip(task {task.Id}): {result.End} after {result.Iterations} turns");

        return result switch
        {
            // The PROJECT cap, not this task's budget: the task did nothing wrong, so it
            // is neither struck nor transitioned — the whole build pauses instead.
            { ProjectBudgetExhausted: true } => PauseForProjectBudget(task, result, log, statusBeforeClaim),
            { End: EndReason.Crash } when CrashCount(task.Id) <= CrashRetryCap => ResumeAfterCrash(task, result, log),
            { End: EndReason.Budget or EndReason.Iterations or EndReason.Crash } => ParkOutOfBudget(task, result, log),
            _ => ParkBlocked(task, result, log),
        };
    }

    /// <summary>
    /// Rolls the task back to the status it held before this run claimed it, and reports
    /// the run as paused rather than failed.
    /// </summary>
    /// <remarks>
    /// The cap being spent is a money decision, not the task's fault, so nothing is
    /// struck. Rolling back matters because claiming already moved the task to
    /// in_progress: leaving it there discards who owned it — a Principal takeover reads
    /// back as ordinary engineer work and the next run hands it to the wrong role.
    /// </remarks>
    private TaskRunOutcome PauseForProjectBudget(
        TaskRecord task, AgentRunResult result, ForgeLogger log, TaskStatus statusBeforeClaim)
    {
        var current = _tasks.Get(task.Id).Status;
        if (current != statusBeforeClaim && TaskTransitions.IsLegal(current, statusBeforeClaim))
            Transition(task.Id, statusBeforeClaim, log);

        var summary = $"Project budget exhausted — task {task.Id} left as-is to resume once the cap is raised.";
        log.Event(EventType.LlmRefused, summary);
        return new TaskRunOutcome(task.Id, result.End, _tasks.Get(task.Id).Status, summary,
            ProjectBudgetExhausted: true);
    }

    private int CrashCount(long taskId) =>
        _instances.ForTask(taskId).Count(i => i.EndReason == EndReason.Crash);

    /// <summary>
    /// A provider crash is transient: a fresh instance gets a fresh network attempt and
    /// fresh turns, and the WIP is intact. Leave the task `in_progress` (claimable) so the
    /// next run auto-resumes it — the very path a killed process already uses. No transition.
    /// </summary>
    private TaskRunOutcome ResumeAfterCrash(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        var summary = $"Instance {result.InstanceId} crashed after {result.Iterations} turns " +
                      $"(crash {CrashCount(task.Id)}/{CrashRetryCap}); left in progress to auto-resume.";
        log.Event(EventType.ErrorProvider, summary);
        return new TaskRunOutcome(task.Id, result.End, _tasks.Get(task.Id).Status, summary);
    }

    /// <summary>
    /// Out of resources after the forced last-turn message. Count a strike and move the
    /// task to the Principal's OutOfBudget queue — the Principal sets a new budget and
    /// direction, or (at the second strike) implements it directly.
    /// </summary>
    private TaskRunOutcome ParkOutOfBudget(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        var strike = _tasks.IncrementOutOfBudgetCount(task.Id);
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.OutOfBudget && TaskTransitions.IsLegal(current, TaskStatus.OutOfBudget))
            Transition(task.Id, TaskStatus.OutOfBudget, log);

        var summary = $"Instance {result.InstanceId} ran out of resources " +
                      $"({SnakeCaseEnum.ToSnakeCase(result.End)}) after {result.Iterations} turns — " +
                      $"strike {strike}. Handed to the Principal (out_of_budget).";
        log.Event(EventType.ErrorInternal, summary);
        Notify(task.Id, MessageType.Escalation, "principal", $"{summary} {result.Detail}".Trim());
        return new TaskRunOutcome(task.Id, result.End, TaskStatus.OutOfBudget, summary);
    }

    /// <summary>A deliberate escalate (or exhausted crash retries): the Principal triages.</summary>
    private TaskRunOutcome ParkBlocked(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Blocked && TaskTransitions.IsLegal(current, TaskStatus.Blocked))
            Transition(task.Id, TaskStatus.Blocked, log);

        var summary = $"Instance {result.InstanceId} ended {SnakeCaseEnum.ToSnakeCase(result.End)} " +
                      $"after {result.Iterations} turns. Handed to the Principal (blocked).";
        // A genuine escalate() already messaged the principal via the escalation ladder;
        // only self-notify when the harness itself parked it (e.g. exhausted crash retries).
        if (result.End is not EndReason.Escalated)
            Notify(task.Id, MessageType.Escalation, "principal", $"{summary} {result.Detail}".Trim());
        return new TaskRunOutcome(task.Id, result.End, _tasks.Get(task.Id).Status, summary);
    }

    // Blocked/out-of-budget recovery is now driven, not a dead end: RunNextByPriorityAsync
    // claims Principal-owned tasks first, TriageOrImplementAsync runs the Principal on
    // them (redirect / decompose / escalate, or implement directly at the second strike),
    // and NextPrincipalOwned skips tasks already waiting on a human. Escalations climb
    // engineer → principal → pm → client via the escalate ladder (AgentToolset.Escalate).
    // TODO: the last human-facing gap — a "pm"/"client"-addressed escalation (only when the
    // Principal kicks a requirements question upward) is still not surfaced in PmChat, whose
    // History() replays client-facing messages only. Surface those in the PM chat (widen the
    // filter or add an inbox view) so the client sees the questions the Principal escalates.
    private void Notify(long taskId, MessageType type, string to, string payload) =>
        _messages.Insert(Message.Create(type, "system", to, payload, taskId));
}
