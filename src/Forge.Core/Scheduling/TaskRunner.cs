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

/// <summary>What one step of the runner did: the task it touched, how it ended, and a summary.</summary>
public sealed record TaskRunOutcome(
    long TaskId, EndReason End, TaskStatus Status, string Summary,
    // The project's dollar cap refused a call; the loop stops pulling work until it is raised.
    bool ProjectBudgetExhausted = false);

/// <summary>
/// The serial worker that drives a project. Claims a task, gives an agent instance a jailed
/// workspace, then decides what happened from git and process output rather than from the
/// agent's report: CI, a Principal review, then merge, with a bounded revision loop back to
/// the engineer. It also decomposes Features, triages stuck tasks and bugs, runs QA once the
/// board is complete, and hands the finished project over.
///
/// The CI step is injectable so orchestration tests can run without a .NET toolchain.
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
    /// <summary>Engineer attempts on one task before it is blocked and escalated.</summary>
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
    /// Runs the given task, or the next claimable one. Abandoned work — a task left
    /// in_progress by a killed worker, with its workspace still on disk — is resumed before
    /// anything new is claimed.
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

    /// <summary>Runs the next task for a role, or returns null when it has none.</summary>
    public async Task<TaskRunOutcome?> RunNextAsync(AgentRole role, CancellationToken ct = default)
    {
        var task = NextTask(role);
        return task is null ? null : await RunAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one step of the autonomous loop, in priority order: work already part-way through
    /// the pipeline, then Principal-owned tasks, then claimable engineer work, then closing
    /// finished Features, then QA. Returns null when nothing is left to do.
    /// </summary>
    public async Task<TaskRunOutcome?> RunNextByPriorityAsync(CancellationToken ct = default)
    {
        DiscardCancelledWork();
        // Finish part-done work before starting anything new.
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
        // No task work left: close any finished Feature, which is what makes the board
        // quiescent, then run QA if there is new completed work to verify.
        CloseFinishedFeatures();
        return await MaybeRunQaAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs whatever the Principal owns on this task. A Feature is decomposed and a bug is
    /// triaged; a stuck task climbs a ladder — triage, then the Principal implementing it
    /// directly, then a final triage with `redirect` withheld.
    /// </summary>
    private async Task<TaskRunOutcome> TriageOrImplementAsync(TaskRecord task, CancellationToken ct)
    {
        var log = _log.For(task.Id);
        // Dispatch on type: a Feature decomposes, a bug is accepted or rejected.
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
            // Past the last strike: one final triage whose only options are splitting the
            // task or handing it to the client.
            if (task.OutOfBudgetCount > DirectImplementStrike)
                return await TriageAsync(task, ct, AgentRecipe.PrincipalFinalTriage).ConfigureAwait(false);
            if (task.OutOfBudgetCount >= DirectImplementStrike)
                return await ImplementDirectlyAsync(task, ct).ConfigureAwait(false);
        }
        return await TriageAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Decomposes a Feature into tasks by running the design phase, then makes each new task a
    /// child of it, gives it the Feature's milestone, and releases it to `ready`. The Feature
    /// moves to `active`, and closes to `done` later when every child has finished.
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

        // No tasks means the Principal escalated or the run crashed; leave the Feature in
        // triage rather than activating an empty one.
        if (outcome.TasksCreated == 0)
        {
            var note = $"Feature {feature.Id} produced no tasks ({SnakeCaseEnum.ToSnakeCase(outcome.End)}): {outcome.Summary}";
            log.Message(note);
            return new TaskRunOutcome(feature.Id, outcome.End, feature.Status, note);
        }

        // An unlinked plan is parked on the client rather than released. Design is not
        // idempotent, so re-running it would duplicate the tasks it already created.
        if (!outcome.Contract.Complete)
        {
            var gap = $"Design is incomplete: {outcome.Contract.Describe()}.";
            log.Message(gap);
            _messages.Insert(Message.Create(MessageType.Escalation, "principal", "pm", gap, feature.Id));
            Transition(feature.Id, TaskStatus.NeedsHuman, log);
            return new TaskRunOutcome(feature.Id, EndReason.Escalated, TaskStatus.NeedsHuman, gap);
        }

        // Every task created in this run becomes a child of the Feature and is released.
        foreach (var child in _tasks.List().Where(t => !before.Contains(t.Id) && t.Type != TaskType.Feature))
        {
            _tasks.SetParent(child.Id, feature.Id);

            // A child inherits the Feature's milestone unless it was given one of its own,
            // so its cost appears in the client's per-milestone view.
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
    /// The set of waiting task ids is recorded in project_meta, so each distinct set is
    /// asked about once.
    /// </summary>
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
    /// Closes any active Feature whose children have all reached a terminal state, read from
    /// the board via parent_id. This is what makes the board quiescent and arms QA.
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
    /// Runs a fresh Principal over a stuck task's work-in-progress and note, to resolve it with
    /// redirect, break_and_relink or escalate. A triage that resolves nothing hands the task to
    /// a human, so the loop cannot spin on it.
    /// </summary>
    /// <param name="finalTriage">
    /// <see cref="AgentRecipe.PrincipalFinalTriage"/> on the last strike, which has no
    /// `redirect`.
    /// </param>
    private async Task<TaskRunOutcome> TriageAsync(
        TaskRecord task, CancellationToken ct, AgentRecipe? finalTriage = null)
    {
        var log = _log.For(task.Id);
        var recipe = finalTriage ?? AgentRecipe.PrincipalTriage;
        log.Message($"Principal triaging {SnakeCaseEnum.ToSnakeCase(task.Status)} task {task.Id}: {task.Title}");

        // Budgets are per instance, so this triage starts at zero whatever the engineer spent.
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
        // Ready means redirect resolved it; an escalation parks it on the client; anything
        // else means the triage itself resolved nothing.
        if (result.End == EndReason.Escalated)
            return ParkOnClient(task.Id, result.ProgressNote ?? "Escalated to the client.", log);
        if (status is TaskStatus.OutOfBudget or TaskStatus.Blocked or TaskStatus.Triage)
            return GiveUp(task, log);

        return new TaskRunOutcome(task.Id, result.End, status,
            $"Triaged task {task.Id}: {result.ProgressNote ?? SnakeCaseEnum.ToSnakeCase(result.End)}.");
    }

    /// <summary>
    /// Parents and releases the tasks a triage created. A task created by break_and_relink
    /// keeps the parent and milestone that verdict gave it; anything else is filed under the
    /// task being triaged and inherits its milestone. Both are released to `ready`.
    /// </summary>
    private void ReleaseTriageSubtasks(TaskRecord triaged, IReadOnlySet<long> before, ForgeLogger log)
    {
        foreach (var child in _tasks.List().Where(t => !before.Contains(t.Id) && t.Type != TaskType.Feature))
        {
            // Depth 0 means the task did not come from break_and_relink, which files its own.
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
    /// Has the Principal implement a task itself, on a fresh budget, after redirecting the
    /// engineer failed. The result still goes through CI, review and merge.
    /// </summary>
    private async Task<TaskRunOutcome> ImplementDirectlyAsync(TaskRecord task, CancellationToken ct)
    {
        var log = _log.For(task.Id);
        var recipe = AgentRecipe.PrincipalImplementer;
        log.Message($"Principal implementing task {task.Id} directly (strike {task.OutOfBudgetCount}).");

        // Raise the task's budget to what the Principal's recipe asks for, if it is lower.
        if (task.TokenBudget < recipe.DefaultBudget) _tasks.SetBudget(task.Id, recipe.DefaultBudget);
        return await RunAsync(_tasks.Get(task.Id), recipe, ct).ConfigureAwait(false);
    }

    /// <summary>Blocks a task nothing could land and puts the decision to the client via the PM.</summary>
    private TaskRunOutcome GiveUp(TaskRecord task, ForgeLogger log)
    {
        var note = $"Task {task.Id} still unresolved after Principal triage/implementation — needs a human decision.";
        _tasks.SetProgressNote(task.Id, note);
        log.Event(EventType.ErrorInternal, note);
        Notify(task.Id, MessageType.Escalation, "pm", note);
        return ParkOnClient(task.Id, note, log);
    }

    /// <summary>
    /// Moves a task to needs_human, which takes it out of every queue so the loop drains the
    /// rest of the board, and reports it as escalated.
    /// </summary>
    private TaskRunOutcome ParkOnClient(long taskId, string note, ForgeLogger log)
    {
        var current = _tasks.Get(taskId).Status;
        if (current != TaskStatus.NeedsHuman && TaskTransitions.IsLegal(current, TaskStatus.NeedsHuman))
            Transition(taskId, TaskStatus.NeedsHuman, log);

        // Clear the watermark so the PM raises this task even if it asked about it before.
        _meta.Set(AskedKey, "");
        return new TaskRunOutcome(taskId, EndReason.Escalated, _tasks.Get(taskId).Status, note);
    }

    /// <summary>
    /// The triage briefing used as the Principal's opening turn: what the task is, how it is
    /// blocked, and which verdicts are available.
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
    /// Runs a phase that has no task to park, retrying a provider crash in place up to the
    /// crash cap. Returns the last result, which is still a Crash if it never cleared.
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
    /// Runs QA when the board is quiescent and more work has finished than the watermark
    /// records as verified. Once a round leaves the count unchanged the project is complete
    /// and this hands it over; a project that never converges escalates after the round cap.
    /// </summary>
    private async Task<TaskRunOutcome?> MaybeRunQaAsync(CancellationToken ct)
    {
        if (!_tasks.BoardQuiescent()) return null;
        if (MetaInt("qa_escalated") == 1) return null;

        var rounds = MetaInt("qa_rounds");
        var newWorkToVerify = _tasks.CountDone() > MetaInt("qa_verified_count");
        // Verified with nothing finished since: the project is complete.
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
    /// Clones trunk to a build directory the client can open and run, records the run command,
    /// and has the PM tell them. Returns null when the handover has already happened; it is
    /// re-armed when a new Feature is decomposed.
    /// </summary>
    private async Task<TaskRunOutcome?> DeliverAsync(CancellationToken ct)
    {
        if (MetaInt(DeliveredKey) == 1) return null;

        var checkout = paths.ProjectBuild(project);
        _workspaces.PrepareTrunkClone(checkout);

        // QA's recorded command wins, since it really started the app and knows its port.
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
    /// Runs one QA round on a fresh trunk clone: a QA instance writes or updates the acceptance
    /// suite against the contract, the harness runs it, and the result decides the round. The
    /// instance is project-scoped, with no task attached.
    /// </summary>
    private async Task<TaskRunOutcome> RunQaAsync(CancellationToken ct)
    {
        var recipe = AgentRecipe.Qa;
        _log.Message("QA phase: verifying the finished project against the client's requirements");

        var workspace = _workspaces.PrepareTrunkClone(paths.RoleWorkspace(project, "qa"));

        // The project, its framework and its package versions are the harness's to decide, not
        // QA's to remember. A round once invented a Microsoft.NET.Test.Sdk version that does not
        // exist, and the next two wrote a differently-named project beside the broken one.
        if (AcceptanceSuite.EnsureScaffold(workspace) is { } scaffoldError)
        {
            _log.Message($"QA round incomplete — {scaffoldError}");
            return new TaskRunOutcome(0, EndReason.Crash, TaskStatus.Qa, scaffoldError);
        }

        var bugsBefore = _tasks.List().Count(t => t.Type == TaskType.Bug);

        // Nothing to write: the suite already covers the contract and the contract has not moved
        // since it was written, so this round is a regression run and needs no model at all.
        if (NothingForQaToWrite(workspace))
        {
            _log.Message("QA: the suite already covers the contract; re-running it without an agent.");
            if (SuiteVerdict(workspace) is { } regressionGap)
            {
                _log.Message(regressionGap);
                return new TaskRunOutcome(0, EndReason.Done, TaskStatus.Qa, regressionGap);
            }
            _meta.Set("qa_rounds", (MetaInt("qa_rounds") + 1).ToString());
            _meta.Set("qa_verified_count", _tasks.CountDone().ToString());
            var filedNow = _tasks.List().Count(t => t.Type == TaskType.Bug) - bugsBefore;
            var regression = filedNow == 0
                ? "QA passed — the existing acceptance suite is green against the contract."
                : $"QA failed — the existing acceptance suite is red; {filedNow} bug(s) to triage.";
            _log.Message($"QA round complete — {regression}");
            return new TaskRunOutcome(0, EndReason.Done, TaskStatus.Qa, regression);
        }

        var executor = new ToolExecutor(workspace, recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);
        var result = await RunWithCrashRetryAsync(() =>
            loop.RunChatAsync([new LlmMessage("user", QaBrief(workspace))], executor, ct)).ConfigureAwait(false);

        // The round never ran, so the watermark must not move and nothing is escalated:
        // raising the budget re-runs QA as if this had not happened.
        if (result.ProjectBudgetExhausted)
            return new TaskRunOutcome(0, result.End, TaskStatus.Qa,
                "QA paused — project budget exhausted.", ProjectBudgetExhausted: true);

        // A provider outage that outlasted the retries: the watermark stays put and the
        // client is told. The flag is cleared when a new Feature is decomposed.
        if (result.End == EndReason.Crash)
        {
            var crashNote = "QA could not complete — the provider failed after retries. Re-run once it's healthy.";
            _messages.Insert(Message.Create(MessageType.Escalation, "system", "pm", crashNote));
            _meta.Set("qa_escalated", "1");
            _log.Event(EventType.ErrorProvider, crashNote);
            return new TaskRunOutcome(0, EndReason.Crash, TaskStatus.Qa, crashNote);
        }

        _meta.Set("qa_rounds", (MetaInt("qa_rounds") + 1).ToString());

        // A project with a contract is judged by its suite. One without has no operations to
        // cover, so it keeps the older rule: QA exercised it and filed what failed.
        if (ApiContract.Load(workspace) is not null
            && SuiteVerdict(workspace) is { } incomplete)
        {
            // No verdict, so the watermark stays put and the next tick re-runs QA.
            _log.Message(incomplete);
            return new TaskRunOutcome(0, result.End, TaskStatus.Qa, incomplete);
        }

        // The watermark records how much finished work QA has now verified. Newly filed bugs
        // are not done, so it rises again only when a fix or a change request lands.
        _meta.Set("qa_verified_count", _tasks.CountDone().ToString());

        var filed = _tasks.List().Count(t => t.Type == TaskType.Bug) - bugsBefore;
        var summary = filed == 0
            ? "QA passed — every requirement met; the project is accepted."
            : $"QA filed {filed} bug(s) for the Principal to triage.";
        _log.Message($"QA round complete — {summary}");
        return new TaskRunOutcome(0, result.End, TaskStatus.Qa, summary);
    }

    /// <summary>project_meta key holding the contract sha the committed suite was written against.</summary>
    private const string SuiteContractShaKey = "qa_suite_contract_sha";

    /// <summary>
    /// Whether this round needs a QA instance at all. It does not when a suite already exists,
    /// covers every operation in the contract, and the contract has not changed since that suite
    /// was committed — then the round is a regression run and the harness simply re-runs it.
    /// </summary>
    private bool NothingForQaToWrite(string workspace)
    {
        if (ApiContract.Load(workspace) is not { } contract) return false;
        if (!AcceptanceSuite.Exists(workspace)) return false;

        var declared = AcceptanceSuite.DeclaredOperations(workspace);
        if (contract.OperationIds.Any(id => !declared.Contains(id))) return false;

        var written = _meta.Get(SuiteContractShaKey);
        return written is { Length: > 0 } && written == ContractSha(workspace);
    }

    /// <summary>
    /// The sha of the commit that last touched the contract, so a changed contract is told apart
    /// from an unchanged one. Empty when git cannot answer, which forces a QA instance.
    /// </summary>
    private static string ContractSha(string workspace)
    {
        var log = Git.Run(workspace, "log", "-1", "--format=%H", "--", ApiContract.Path);
        return log.Ok ? log.Stdout.Trim() : "";
    }

    /// <summary>
    /// Checks the suite covers every contract operation and runs it, filing a bug if it is red.
    /// Returns why the round produced no verdict — uncovered operations, or a suite that did
    /// not run — or null when it did.
    /// </summary>
    private string? SuiteVerdict(string workspace)
    {
        // Compile first. A suite that does not build is not worth keeping, so nothing is
        // committed and the next round's clone starts from the scaffold again.
        if (AcceptanceSuite.Build(workspace) is { Passed: false } broken)
            return $"QA round incomplete — the acceptance suite does not compile:\n{broken.Output}";

        // It builds, so keep it whatever the run says: a red suite is still the regression
        // suite, and the next round must not have to write it again.
        if (_workspaces.CommitAndPushTrunk(workspace, "test(qa): acceptance suite"))
        {
            _log.Event(EventType.GitCommit, "committed the acceptance suite to trunk");
            _meta.Set(SuiteContractShaKey, ContractSha(workspace));
        }

        var contract = ApiContract.Load(workspace)!;
        var declared = AcceptanceSuite.DeclaredOperations(workspace);
        if (contract.OperationIds.Where(id => !declared.Contains(id)).ToList() is { Count: > 0 } uncovered)
            return "QA round incomplete — no acceptance test covers " + string.Join(", ", uncovered);

        var acceptance = AcceptanceSuite.Run(workspace, alreadyBuilt: true);
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

    /// <summary>Files one bug for a red acceptance suite, carrying the test output verbatim.</summary>
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

    /// <summary>Truncates to the last <paramref name="max"/> characters, where the failure summary sits.</summary>
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
    /// The contract section of QA's brief: the path to the document and every operation it must
    /// cover. Explains instead that there is nothing to cover when the project has no contract.
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
    /// The startup section of QA's brief: the project to run and the port it declares, read out
    /// of the checkout by <see cref="AgentToolset.Discover"/>.
    /// </summary>
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

        // Only engineer instances that ended `done` count as revisions — those reached the
        // gates and were sent back. A budget kill, crash or iteration cap is a park-and-resume.
        // The Principal implementing directly is the escalation past this cap, so it is exempt.
        if (recipe.Role == AgentRole.Engineer)
        {
            // Counted since the last new direction — a triage, or the client answering — not
            // for the life of the task, so fresh guidance clears the count.
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
    /// Claims a task for work: a guarded status transition, so it stays safe with more than
    /// one worker. A task already in_progress is being resumed and is left alone.
    /// </summary>
    private TaskRecord Claim(TaskRecord task, ForgeLogger log)
    {
        // Ready is the normal claim; OutOfBudget or Blocked is the Principal taking it over.
        var status = _tasks.Get(task.Id).Status;
        if (status is TaskStatus.Ready or TaskStatus.OutOfBudget or TaskStatus.Blocked)
            Transition(task.Id, TaskStatus.Claimed, log);
        if (_tasks.Get(task.Id).Status == TaskStatus.Claimed)
            Transition(task.Id, TaskStatus.InProgress, log);
        return _tasks.Get(task.Id);
    }

    /// <summary>Transitions a task and writes one log line for it.</summary>
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
    /// Commits and pushes what the agent produced, runs CI over it, and hands it to review by
    /// leaving it in_review for the next tick. Whether it advances is read from git and from
    /// CI's exit code, not from the agent's claim.
    /// </summary>
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
            // CI runs before review, so the Principal never reviews code that does not build.
            log.Event(EventType.CiRun, "dotnet build/test");
            var ci = _ci(_workspaces.Path(task.Id));
            if (!ci.Passed)
            {
                log.Event(EventType.CiFailed, ci.Summary);
                return RequestRevision(task, log, "CI",
                    $"CI failed at `{ci.Step}`. Fix the build/tests and call done again.\n\n{Shorten(ci.Output, 2000)}");
            }
            log.Event(EventType.CiPassed, ci.Summary);

            // Review is the next tick's work, so a worker that dies here strands nothing.
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

            // The reviewer rejected the bug itself, so there is nothing to merge or revise.
            if (verdict.RejectedBugReason is { } rejectReason)
            {
                _workspaces.Discard(task.Id);
                log.Message($"Task {task.Id}: bug rejected in review — {rejectReason}");
                return new TaskRunOutcome(task.Id, EndReason.Done, TaskStatus.Rejected, $"Bug rejected: {rejectReason}");
            }

            // No verdict says nothing about the code: leave it in_review for another attempt
            // rather than sending the engineer feedback no reviewer gave.
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

    /// <summary>
    /// Merges an approved task to trunk, deletes its workspace and closes it. No agent is
    /// involved, and re-running is a no-op, so a worker that dies mid-merge simply repeats it.
    /// </summary>
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

            // The per-task QA hop decides nothing; real QA is project-level.
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
    /// Sends a task back to the engineer with feedback, written into the progress note so the
    /// resuming instance sees it, and recorded as a discussion. The workspace and branch are
    /// kept for it to revise.
    /// </summary>
    private TaskRunOutcome RequestRevision(TaskRecord task, ForgeLogger log, string stage, string feedback)
    {
        // Both paths leave the task in_progress, which is what makes it claimable again.
        var current = _tasks.Get(task.Id).Status;
        if (current == TaskStatus.InReview) Transition(task.Id, TaskStatus.InProgress, log);

        _tasks.SetProgressNote(task.Id, $"CHANGES REQUESTED ({stage}). {feedback}");
        new DiscussionRepository(conn).Open(task.Id, "system", $"[{stage}] {feedback}");
        log.Message($"Task {task.Id}: changes requested at {stage} — back to the engineer");
        return new TaskRunOutcome(task.Id, EndReason.Done, TaskStatus.InProgress, $"Changes requested ({stage}).");
    }

    /// <summary>Appends a reviewer's rule to CONVENTIONS.md on trunk, where every engineer reads it.</summary>
    private void WriteConvention(string convention, ForgeLogger log)
    {
        var wrote = _workspaces.AppendToTrunkFile(
            paths.RoleWorkspace(project, "conventions"),
            "CONVENTIONS.md", $"- {convention}", $"conventions: {Shorten(convention, 60)}");
        if (wrote) log.Event(EventType.GitCommit, $"convention added from review: {Shorten(convention, 80)}");
    }

    /// <summary>
    /// Parks a task on the client once the engineer has used every attempt. Not `blocked`,
    /// because the attempt count only rises: a redirected task would re-block on the next claim.
    /// </summary>
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
    /// Parks a task whose instance ended without finishing, keeping the workspace and progress
    /// note for the next one. A crash stays claimable and auto-resumes up to the crash cap; a
    /// spent budget or iteration cap goes to out_of_budget; an escalation goes to blocked.
    /// </summary>
    private TaskRunOutcome Park(
        TaskRecord task, AgentRunResult result, ForgeLogger log, TaskStatus statusBeforeClaim)
    {
        _workspaces.CommitAll(task.Id, $"wip(task {task.Id}): {result.End} after {result.Iterations} turns");

        return result switch
        {
            // The project cap, not this task's budget: the build pauses, the task is untouched.
            { ProjectBudgetExhausted: true } => PauseForProjectBudget(task, result, log, statusBeforeClaim),
            { End: EndReason.Crash } when CrashCount(task.Id) <= CrashRetryCap => ResumeAfterCrash(task, result, log),
            { End: EndReason.Budget or EndReason.Iterations or EndReason.Crash } => ParkOutOfBudget(task, result, log),
            _ => ParkBlocked(task, result, log),
        };
    }

    /// <summary>
    /// Rolls the task back to the status it held before this run claimed it, and reports the
    /// run as paused rather than failed. No strike is counted. Rolling back preserves which
    /// role owned it, which claiming to in_progress would otherwise erase.
    /// </summary>
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
    /// Leaves a crashed task in_progress so the next run auto-resumes it, until the crash cap
    /// is reached, after which it goes to the Principal.
    /// </summary>
    private TaskRunOutcome ResumeAfterCrash(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        var summary = $"Instance {result.InstanceId} crashed after {result.Iterations} turns " +
                      $"(crash {CrashCount(task.Id)}/{CrashRetryCap}); left in progress to auto-resume.";
        log.Event(EventType.ErrorProvider, summary);
        return new TaskRunOutcome(task.Id, result.End, _tasks.Get(task.Id).Status, summary);
    }

    /// <summary>
    /// Counts a strike and moves a task that ran out of budget or turns to the Principal's
    /// out_of_budget queue.
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

    /// <summary>Blocks a task the agent escalated, or one whose crash retries ran out, for triage.</summary>
    private TaskRunOutcome ParkBlocked(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Blocked && TaskTransitions.IsLegal(current, TaskStatus.Blocked))
            Transition(task.Id, TaskStatus.Blocked, log);

        var summary = $"Instance {result.InstanceId} ended {SnakeCaseEnum.ToSnakeCase(result.End)} " +
                      $"after {result.Iterations} turns. Handed to the Principal (blocked).";
        // escalate() already messaged the Principal; only notify when the harness parked it.
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
