using Dapper;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Forge.Core.Secrets;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// The M1 acceptance tests: a task goes on the board and a commit comes out of
/// the bare repo — and a killed instance is replaced by a fresh one that resumes
/// from nothing but the workspace and the progress note.
/// </summary>
public class TaskRunnerTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-run-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;
    private readonly WorkspaceManager _workspaces;

    public TaskRunnerTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
        _tasks = new TaskRepository(_conn);
        _workspaces = new WorkspaceManager(_paths, Project);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_dataRoot, recursive: true);
    }

    private TaskRecord ReadyTask(int budget = 100_000) =>
        _tasks.Transition(
            _tasks.Insert(TaskRecord.Create(
                TaskType.Task, "Add greeting", "Create greeting.txt containing 'hello'", budget,
                assignedRole: AgentRole.Engineer, createdBy: "human")).Id,
            TaskStatus.Ready);

    /// <summary>Metered, as in production — an agent never sees an undecorated client.</summary>
    private TaskRunner Runner(ILlmClient llm, Forge.Core.Logging.ForgeLogger? logger = null) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger);

    /// <summary>Read a file out of the bare repo — the source of truth, not the workspace.</summary>
    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"{WorkspaceManager.TrunkBranch}:{path}").Stdout;

    /// <summary>Commit a file to trunk directly, standing in for work that already landed.</summary>
    private void WriteToTrunk(string path, string content)
    {
        var clone = Path.Combine(_dataRoot, $"seed-{Guid.NewGuid():N}");
        _workspaces.PrepareTrunkClone(clone);
        var file = Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        _workspaces.CommitAndPushTrunk(clone, $"seed: {path}");
    }

    private const string Contract = """
        openapi: 3.0.0
        info: { title: demo, version: "1.0" }
        paths:
          /api/notes:
            get:
              operationId: notes-list
              x-requirement: 01-notes.md
              responses:
                "200": { description: ok }
                "404": { description: missing }
        """;

    [Fact]
    public void Bootstrap_seeds_a_trunk_commit_so_the_first_task_has_something_to_branch_from()
    {
        Assert.Contains("# demo", ShowFromTrunk("PROJECT.md"));
        Assert.True(Directory.Exists(_paths.WorkspacesDir(Project)));

        // The executor points HOME at the jail, so the SDK's caches land inside the
        // workspace; the seeded .gitignore keeps commit-all from sweeping them in.
        var ignore = ShowFromTrunk(".gitignore");
        Assert.Contains("obj/", ignore);
        Assert.Contains(".nuget/", ignore);
    }

    [Fact]
    public async Task A_task_run_emits_a_correlated_stream_queryable_at_project_and_task_scope()
    {
        var sink = new MemoryLogSink();
        var logger = new Forge.Core.Logging.ForgeLogger(sink, Project);

        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Created greeting.txt.")),
            ScriptedLlmClient.Tool("approve", ("note", "Good.")));

        // Stop at the merge: draining further would run QA, whose lines are project-
        // scoped and would break the "every line belongs to this task" assertion below.
        var runner = Runner(llm, logger);
        await runner.RunAsync(_tasks.Get(task.Id));
        await runner.RunNextByPriorityAsync();   // review
        await runner.RunNextByPriorityAsync();   // merge

        // The whole story is present, in order: claim → workspace → instance →
        // the model's calls and tool actions → merge → done.
        Assert.Contains("lifecycle.task_transition", sink.Types);   // claimed / in_progress / …
        Assert.Contains("lifecycle.instance_start", sink.Types);
        Assert.Contains("llm.call", sink.Types);
        Assert.Contains("tool.write_file", sink.Types);
        Assert.Contains("git.merge", sink.Types);
        Assert.Contains("lifecycle.instance_end", sink.Types);

        // The file-creation line reads like the client's own example.
        Assert.Contains(sink.Entries, e =>
            e.Type == Forge.Core.Logging.EventType.ToolWriteFile && e.Message.Contains("greeting.txt"));

        // Correlation: every one of these lines is scoped to this task, so a
        // task-level query returns them and a project-level query is a superset.
        Assert.NotEmpty(sink.ForTask(task.Id));
        Assert.All(sink.ForTask(task.Id), e => Assert.Equal(Project, e.Project));
        Assert.All(sink.Entries, e => Assert.Equal(task.Id, e.Task));  // all task-scoped in this run
    }

    [Fact]
    public void Initialising_a_project_that_already_exists_is_refused()
    {
        // The setup already created 'demo'; a second init must not clobber it.
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectBootstrap.Init(_paths, Project));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void A_registry_row_with_no_directory_still_reports_the_project_as_existing()
    {
        // Simulate a half-finished init: 'demo' is registered but its directory is
        // gone. A directory-only check would let a re-init through and then blow up
        // on the registry INSERT; the three-way check reports it cleanly instead.
        _conn.Dispose(); // release the file handle so the directory can be removed
        Directory.Delete(_paths.ProjectDir(Project), recursive: true);
        Assert.False(Directory.Exists(_paths.ProjectDir(Project)));

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectBootstrap.Init(_paths, Project));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task A_completed_task_lands_in_the_bare_repo_and_the_workspace_is_cleaned_up()
    {
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            // engineer
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Created greeting.txt with 'hello'.")),
            // Principal review (CI skips — no .csproj — so review runs, then approves)
            ScriptedLlmClient.Tool("approve", ("note", "Correct and simple.")));

        // Engineer, then review, then merge — three ticks since the pipeline became resumable.
        var runner = Runner(llm);
        await runner.RunNextAsync(AgentRole.Engineer);
        await DrainAsync(runner);

        Assert.Equal(TaskStatus.Done, _tasks.Get(task.Id).Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
        Assert.False(_workspaces.Exists(task.Id));

        var record = _tasks.Get(task.Id);
        Assert.Equal(TaskStatus.Done, record.Status);
        Assert.Equal($"task/{task.Id}-add-greeting", record.BranchName);
        Assert.True(record.TokensSpent > 0);
    }

    [Fact]
    public async Task Merge_state_is_read_from_git_so_a_false_done_claim_blocks_instead_of_merging()
    {
        var task = ReadyTask();
        // The agent claims success without ever writing anything.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("list_dir"),
            ScriptedLlmClient.Tool("done", ("summary", "All done — the feature works great.")));

        var outcome = await Runner(llm).RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.Stalled, outcome.Status);
        Assert.Contains("no commits", outcome.Summary);
        Assert.Contains("produced no commits", _tasks.Get(task.Id).ProgressNote!);

        var escalation = new MessageRepository(_conn).Pending("principal").Last();
        Assert.IsType<EscalationMessage>(escalation);
    }

    [Fact]
    public async Task Tasks_are_claimed_in_order_and_a_drained_board_returns_nothing()
    {
        var first = ReadyTask();
        var second = ReadyTask();
        var runner = Runner(new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("done", ("summary", "ok")) });

        Assert.Equal(first.Id, (await runner.RunNextAsync(AgentRole.Engineer))!.TaskId);
        Assert.Equal(second.Id, (await runner.RunNextAsync(AgentRole.Engineer))!.TaskId);
        Assert.Null(await runner.RunNextAsync(AgentRole.Engineer));
    }

    [Fact]
    public async Task A_killed_instance_is_resumed_by_a_fresh_one_carrying_only_the_note_and_the_workspace()
    {
        var task = ReadyTask();

        // --- Instance 1: does half the work, writes a note, then burns its turns. ---
        var dying = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("progress_note",
                ("note", "greeting.txt written. Still to do: add farewell.txt containing 'bye'.")))
        {
            Fallback = ScriptedLlmClient.Tool("list_dir"),
        };
        // Stand in for the process being killed: the loop is cut off mid-task.
        var recipe = AgentRecipe.Engineer with { IterationCap = 4 };
        var killed = await new AgentLoop(dying, _conn, new PromptAssembler(PromptLibrary.Resolve()), recipe)
            .RunAsync(Claim(task), new Forge.Core.Tools.ToolExecutor(
                _workspaces.Path(task.Id), recipe.ToolAllowlist, new SecretsVault(_paths.VaultDir)));

        Assert.Equal(EndReason.Iterations, killed.End);
        Assert.True(_workspaces.Exists(task.Id), "the workspace must survive so work isn't lost");
        _tasks.Transition(task.Id, TaskStatus.Stalled);

        // --- Instance 2: a genuinely fresh client. It has never seen the conversation. ---
        var resuming = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "farewell.txt"), ("content", "bye")),
            ScriptedLlmClient.Tool("done", ("summary", "Resumed and added farewell.txt.")),
            ScriptedLlmClient.Tool("approve", ("note", "Both files present; approved.")));

        _tasks.Transition(task.Id, TaskStatus.Ready);
        var runner = Runner(resuming);
        await runner.RunNextAsync(AgentRole.Engineer);
        await DrainAsync(runner);

        // It was handed the predecessor's note and nothing else.
        Assert.Contains("Still to do: add farewell.txt", resuming.Requests[0].Messages[0].Content);
        Assert.Single(resuming.Requests[0].Messages); // a fresh conversation, not a continued one

        // Both halves of the work are in the bare repo.
        Assert.Equal(TaskStatus.Done, _tasks.Get(task.Id).Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
        Assert.Equal("bye\n", ShowFromTrunk("farewell.txt"));

        // Two distinct engineer instances worked the task (killed + resumed); the
        // reviewer is a separate Principal instance and is not counted here.
        var engineerInstances = new AgentInstanceRepository(_conn).ForTask(task.Id)
            .Where(i => i.Role == AgentRole.Engineer).ToList();
        Assert.Equal(2, engineerInstances.Count);
        Assert.Equal([EndReason.Iterations, EndReason.Done], engineerInstances.Select(i => i.EndReason));
    }

    /// <summary>Drive the claim + workspace half of the runner without running the loop.</summary>
    private TaskRecord Claim(TaskRecord task)
    {
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        _workspaces.Prepare(task, WorkspaceManager.BranchName(task));
        return _tasks.Get(task.Id);
    }

    // ---- OutOfBudget + Principal-triage ladder (the autonomous recovery path) ----

    /// <summary>Put a task straight into the Principal's OutOfBudget queue with N strikes.</summary>
    private TaskRecord OutOfBudgetTask(int strikes = 1, int budget = 100_000)
    {
        var task = ReadyTask(budget);
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        _tasks.Transition(task.Id, TaskStatus.Stalled);
        for (var i = 0; i < strikes; i++) _tasks.IncrementStallCount(task.Id);
        return _tasks.Get(task.Id);
    }

    [Fact]
    public async Task Budget_exhaustion_parks_the_task_out_of_budget_and_hands_it_to_the_principal()
    {
        // One scripted call costs 150 tokens; a 150-budget refuses the second call.
        var task = ReadyTask(budget: 150);
        var llm = new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("list_dir") };

        var outcome = await Runner(llm).RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.Stalled, outcome.Status);
        var record = _tasks.Get(task.Id);
        Assert.Equal(1, record.StallCount);        // one strike counted
        Assert.True(_workspaces.Exists(task.Id));        // workspace kept for the Principal
        Assert.Contains(new MessageRepository(_conn).Pending("principal"),
            m => m.Payload.Contains("out_of_budget"));   // handed up the ladder, not to the PM
        Assert.Empty(new MessageRepository(_conn).Pending("pm"));
    }

    [Fact]
    public async Task A_provider_crash_leaves_the_task_claimable_to_auto_resume_then_gives_up_after_the_cap()
    {
        var task = ReadyTask();
        var runner = Runner(new ThrowingLlmClient());

        // First two crashes are transient: the task stays in_progress so the next run resumes it.
        for (var i = 1; i <= 2; i++)
        {
            var outcome = await runner.RunAsync(_tasks.Get(task.Id));
            Assert.Equal(EndReason.Crash, outcome.End);
            Assert.Equal(TaskStatus.InProgress, _tasks.Get(task.Id).Status);
            Assert.True(_workspaces.Exists(task.Id));
        }

        // The third crash exceeds the retry cap: hand it to the Principal.
        var final = await runner.RunAsync(_tasks.Get(task.Id));
        Assert.Equal(TaskStatus.Stalled, _tasks.Get(task.Id).Status);
        Assert.Equal(TaskStatus.Stalled, final.Status);
    }

    [Fact]
    public async Task Crashes_are_counted_since_the_task_last_reached_the_gates_not_for_its_whole_life()
    {
        // A task that gets through an instance is not the thing failing, so unrelated outages
        // must not accumulate across its life and walk it up the strike ladder on network luck.
        var task = ReadyTask();
        var crashing = Runner(new ThrowingLlmClient());

        for (var i = 1; i <= 2; i++) await crashing.RunAsync(_tasks.Get(task.Id));
        Assert.Equal(TaskStatus.InProgress, _tasks.Get(task.Id).Status);

        // An instance that calls done reaches CI and review; the count starts again from there.
        await Runner(new ScriptedLlmClient(
                ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
                ScriptedLlmClient.Tool("done", ("summary", "wrote the file"))))
            .RunAsync(_tasks.Get(task.Id));
        Assert.Equal(TaskStatus.InReview, _tasks.Get(task.Id).Status);

        // Back with the engineer after a rejection: two more crashes are absorbed, as before.
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        for (var i = 1; i <= 2; i++) await crashing.RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.InProgress, _tasks.Get(task.Id).Status);
        Assert.Equal(0, _tasks.Get(task.Id).StallCount);
    }

    [Fact]
    public async Task Final_triage_is_given_the_play_and_can_narrow_the_task_to_what_it_can_finish()
    {
        // Past the last strike the engineer and the Principal have both failed, so the
        // question stops being "how do we finish this" and becomes "what can ship".
        var stuck = OutOfBudgetTask(strikes: 3);
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("descope",
                ("criteria", "The endpoint returns 200 with the poll body."),
                ("reason", "The animation is cosmetic and nothing depends on it.")));

        await Runner(llm).RunNextByPriorityAsync();

        // The play reached the instance, and only once.
        Assert.Contains("cut it down", llm.Requests[0].Messages[^1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(new DiscussionRepository(_conn).PlayUsed(stuck.Id, "cut-it-down"));

        // The task carries the narrowed contract and goes back to be finished against it.
        var after = _tasks.Get(stuck.Id);
        Assert.Equal("The endpoint returns 200 with the poll body.", after.AcceptanceCriteria);
        Assert.Contains("DESCOPED", after.ProgressNote);
    }

    [Fact]
    public async Task A_task_that_reaches_final_triage_twice_is_closed_by_the_harness_not_asked_about()
    {
        // The Principal has already had its one decision. Cancelling would take every task
        // waiting on this one off the board, so it is closed instead and the build goes on.
        var stuck = OutOfBudgetTask(strikes: 3);
        new DiscussionRepository(_conn).RecordPlay(stuck.Id, "cut-it-down");

        var outcome = await Runner(new ScriptedLlmClient()).RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.Done, _tasks.Get(stuck.Id).Status);
        Assert.Contains("Closed short", outcome!.Summary, StringComparison.Ordinal);
        // Nothing was put to the client.
        Assert.Empty(new MessageRepository(_conn).Pending("pm").Where(m => m is EscalationMessage));
    }

    [Fact]
    public async Task A_stuck_task_is_cleared_before_a_ready_engineer_task_and_triage_redirects_it()
    {
        var stuck = OutOfBudgetTask(strikes: 1);   // Principal-owned
        var ready = ReadyTask();                    // an ordinary engineer task, also claimable

        // Triage: the Principal reads the task and redirects it back to the engineer.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("redirect", ("guidance", "Split the parser out first, then retry.")));
        var outcome = await Runner(llm).RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        Assert.Equal(stuck.Id, outcome.TaskId);                       // the stuck task won priority
        Assert.Equal(TaskStatus.Ready, _tasks.Get(stuck.Id).Status);  // redirected back onto the board
        Assert.Contains("PRINCIPAL GUIDANCE", _tasks.Get(stuck.Id).ProgressNote!);
        Assert.Equal(TaskStatus.Ready, _tasks.Get(ready.Id).Status);  // the engineer task was not touched
    }

    [Fact]
    public async Task Redirect_makes_the_task_claimable_again_and_can_raise_the_budget()
    {
        // Nothing to reset: the allowance is per instance, so the next engineer starts
        // from zero however much the one that got stuck had spent.
        var stuck = OutOfBudgetTask(strikes: 1, budget: 5_000);
        _tasks.AddTokensSpent(stuck.Id, 5_000);

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("redirect", ("guidance", "Try a smaller step."), ("budget", "88888")));
        await Runner(llm).RunNextByPriorityAsync();

        var record = _tasks.Get(stuck.Id);
        Assert.Equal(TaskStatus.Ready, record.Status);
        Assert.Equal(88_888, record.TokenBudget);   // Principal raised the ceiling
    }

    [Fact]
    public async Task Second_strike_makes_the_principal_implement_the_task_directly_and_it_merges()
    {
        var stuck = OutOfBudgetTask(strikes: 2);   // two strikes → direct implementation

        // The Principal-implementer recipe is engineer-shaped: it writes, builds, and finishes,
        // then a fresh Principal reviews and it merges — verified against ground truth as usual.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Implemented directly.")),
            ScriptedLlmClient.Tool("approve", ("note", "Correct.")));

        var runner = Runner(llm);
        await runner.RunNextByPriorityAsync();
        await DrainAsync(runner);

        Assert.Equal(TaskStatus.Done, _tasks.Get(stuck.Id).Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
        // The instance that did the work was the Principal, not an engineer.
        Assert.Contains(new AgentInstanceRepository(_conn).ForTask(stuck.Id),
            i => i.Role == AgentRole.Principal && i.Id.StartsWith("prin-impl"));
    }

    /// <summary>A provider adapter that always fails — stands in for a 429 / outage.</summary>
    private sealed class ThrowingLlmClient : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider unavailable (429)");
    }

    /// <summary>Throws on the first N calls (a transient blip), then replays a script.</summary>
    private sealed class FlakyThenScriptedLlmClient(int throwsFirst, params ScriptedTurn[] turns) : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        private int _thrown;
        private readonly Queue<ScriptedTurn> _turns = new(turns);
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            if (_thrown < throwsFirst) { _thrown++; throw new InvalidOperationException("transient 503"); }
            var turn = _turns.Count > 0 ? _turns.Dequeue() : "done";
            return Task.FromResult(new LlmResponse
            {
                Content = turn.Text,
                StopReason = "end_turn",
                ToolCalls = turn.Calls,
                Usage = new LlmUsage(2, 10),
            });
        }
    }

    [Fact]
    public async Task A_transient_crash_during_bug_triage_is_retried_not_escalated_to_a_human()
    {
        var bug = _tasks.Insert(TaskRecord.Create(
            TaskType.Bug, "Flaky triage", "## Expected\ne\n## Observed\no", 50_000,
            assignedRole: AgentRole.Engineer) with { Status = TaskStatus.Triage });

        // The provider blips once, then the triage runs and rejects the bug.
        var llm = new FlakyThenScriptedLlmClient(throwsFirst: 1,
            ScriptedLlmClient.Tool("reject_bug", ("reason", "Not a real defect.")));

        var outcome = await Runner(llm).RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        // Retry cleared the blip and the Principal decided — not parked for a human.
        Assert.Equal(TaskStatus.Rejected, _tasks.Get(bug.Id).Status);
    }

    // ---- QA (M5a): the project-level acceptance gate ----

    /// <summary>A task inserted straight to Done — a completed build for QA to verify.</summary>
    private TaskRecord DoneTask() =>
        _tasks.Insert(TaskRecord.Create(
            TaskType.Task, "Seeded feature", "Already built and merged", 100_000,
            assignedRole: AgentRole.Engineer) with { Status = TaskStatus.Done });

    /// <summary>Drive the autonomous loop until it drains (returns null).</summary>
    private async Task DrainAsync(TaskRunner runner, int maxSteps = 15)
    {
        for (var i = 0; i < maxSteps; i++)
            if (await runner.RunNextByPriorityAsync() is null) return;
        throw new Xunit.Sdk.XunitException("loop did not drain — possible QA/fix loop");
    }

    [Fact]
    public async Task Qa_that_writes_no_suite_for_a_contract_does_not_accept_the_project()
    {
        // The failure this gate exists for: a QA round that observes nothing, files nothing,
        // and is read as "every requirement met". With a contract on trunk the verdict is the
        // suite's, so calling `done` without writing one leaves the project unverified.
        WriteToTrunk(Forge.Core.Design.ApiContract.Path, Contract);
        DoneTask();

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("done", ("summary", "Everything looks fine to me.")))
        { Fallback = ScriptedLlmClient.Tool("done", ("summary", "still fine")) };

        await DrainAsync(Runner(llm), maxSteps: 10);

        var meta = new ProjectMetaRepository(_conn);
        // Never verified, so never delivered — and escalated to the client once the rounds ran out.
        Assert.Null(meta.Get("qa_verified_count"));
        Assert.Equal("1", meta.Get("qa_escalated"));
        Assert.Null(meta.Get("project_delivered"));
    }

    [Fact]
    public async Task Qa_on_a_project_with_a_contract_writes_the_suite_and_never_starts_the_app()
    {
        // The app is the harness's to start: QA writes the tests, the harness runs them and
        // files what fails. A round that tries to drive the app itself is refused the tool.
        WriteToTrunk(Forge.Core.Design.ApiContract.Path, Contract);
        DoneTask();

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("serve", ("command", "dotnet run --project src/App/App.csproj")),
            ScriptedLlmClient.Tool("done", ("summary", "Nothing to run, then.")))
        { Fallback = ScriptedLlmClient.Tool("done", ("summary", "done")) };

        await DrainAsync(Runner(llm), maxSteps: 10);

        var instanceIds = _conn.Query<string>("SELECT id FROM agent_instances").ToList();
        Assert.Contains(instanceIds, id => id.StartsWith("qa-suite-", StringComparison.Ordinal));
        Assert.Contains("no tool 'serve' is available", llm.Observations(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qa_files_a_bug_that_is_accepted_fixed_then_re_runs_and_accepts_the_project()
    {
        DoneTask(); // the build is complete → QA should run

        var llm = new ScriptedLlmClient(
            // QA round 1: run the check (its output is captured as evidence), file the bug, finish.
            ScriptedLlmClient.Tool("run", ("command", "git status")),
            ScriptedLlmClient.Tool("file_bug", ("title", "Greeting missing"),
                ("expected", "the greeting should read hello"), ("requirements_ref", "01-notes.md")),
            ScriptedLlmClient.Tool("done", ("summary", "Found 1 issue.")),
            // Principal triage: accept the bug.
            ScriptedLlmClient.Tool("accept_bug", ("note", "Real defect.")),
            // Engineer fixes it.
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Added greeting.")),
            // Review approves the fix.
            ScriptedLlmClient.Tool("approve", ("note", "Correct.")),
            // QA round 2: everything now passes → file nothing.
            ScriptedLlmClient.Tool("done", ("summary", "All requirements met.")))
        { Fallback = ScriptedLlmClient.Tool("done", ("summary", "nothing to do")) };

        await DrainAsync(Runner(llm));

        var bug = Assert.Single(_tasks.List().Where(t => t.Type == TaskType.Bug));
        Assert.Equal(TaskStatus.Done, bug.Status);                 // filed → accepted → fixed → merged
        Assert.Contains("## Observed", bug.Objective);              // harness-captured evidence, not prose
        Assert.Contains("git status", bug.Objective);               // the exact command QA ran is the repro
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));     // the fix reached trunk
        Assert.Equal("2", new ProjectMetaRepository(_conn).Get("qa_rounds"));             // ran, then re-ran and accepted
    }

    [Fact]
    public async Task A_rejected_bug_is_kept_and_does_not_re_trigger_qa_so_the_project_completes()
    {
        DoneTask();

        var llm = new ScriptedLlmClient(
            // QA runs a check (captured as evidence), then files one bug…
            ScriptedLlmClient.Tool("run", ("command", "git status")),
            ScriptedLlmClient.Tool("file_bug", ("title", "Cosmetic nit"), ("expected", "it should look nicer")),
            ScriptedLlmClient.Tool("done", ("summary", "One nit.")),
            // …which the Principal rejects.
            ScriptedLlmClient.Tool("reject_bug", ("reason", "Aesthetic — not QA's call.")))
        { Fallback = ScriptedLlmClient.Tool("done", ("summary", "nothing to do")) };

        await DrainAsync(Runner(llm));

        var bug = Assert.Single(_tasks.List().Where(t => t.Type == TaskType.Bug));
        Assert.Equal(TaskStatus.Rejected, bug.Status);          // kept on record, not deleted
        Assert.Contains("REJECTED", bug.ProgressNote!);
        // A pure-rejection cycle accepts nothing new, so QA ran exactly once and stopped —
        // no create-bug / reject / re-QA loop.
        Assert.Equal("1", new ProjectMetaRepository(_conn).Get("qa_rounds"));
    }

    [Fact]
    public async Task A_change_requests_completed_work_re_triggers_qa()
    {
        DoneTask(); // the initial build

        // First QA cycle: nothing to file, project accepted.
        var runner = Runner(new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("done", ("summary", "all good")) });
        await DrainAsync(runner);
        Assert.Equal("1", new ProjectMetaRepository(_conn).Get("qa_rounds"));

        // A change request's task lands as done. Nothing else is claimable, but the
        // done-count now exceeds what QA verified, so QA runs again — same trigger as a fix.
        DoneTask();
        var outcome = await runner.RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        Assert.Equal(TaskStatus.Qa, outcome!.Status);
        Assert.Equal("2", new ProjectMetaRepository(_conn).Get("qa_rounds"));
    }

    [Fact]
    public async Task A_task_stopped_between_the_gates_is_picked_up_again_rather_than_stranded()
    {
        // The failure this exists for: review and merge ran inline after the engineer,
        // so a worker killed between them left the task in a status no queue selected —
        // pressing resume skipped it and it stayed on the board forever.
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Created greeting.txt.")),
            ScriptedLlmClient.Tool("approve", ("note", "Correct.")));

        // Tick once: the engineer submits and the task stops at in_review — the worker
        // dying here is what used to strand it.
        var runner = Runner(llm);
        await runner.RunNextAsync(AgentRole.Engineer);
        Assert.Equal(TaskStatus.InReview, _tasks.Get(task.Id).Status);

        // A fresh runner, as if the process had been restarted, finds and finishes it.
        var resumed = Runner(llm);
        Assert.Equal(task.Id, (await resumed.RunNextByPriorityAsync())!.TaskId);   // review
        Assert.Equal(TaskStatus.Merging, _tasks.Get(task.Id).Status);
        Assert.Equal(task.Id, (await resumed.RunNextByPriorityAsync())!.TaskId);   // merge

        Assert.Equal(TaskStatus.Done, _tasks.Get(task.Id).Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
    }

    [Fact]
    public async Task A_reviewer_rejects_an_invalid_bug_instead_of_looping_a_fix()
    {
        // An accepted bug (ready → an engineer works it) that turns out not to be real.
        var bug = _tasks.Insert(TaskRecord.Create(
            TaskType.Bug, "Phantom defect", "## Repro\nx\n## Expected\ny\n## Actual\nz", 50_000,
            assignedRole: AgentRole.Engineer) with { Status = TaskStatus.Ready });

        var llm = new ScriptedLlmClient(
            // The engineer produces a diff (it can't really fix a non-bug).
            ScriptedLlmClient.Tool("write_file", ("path", "note.txt"), ("content", "investigated")),
            ScriptedLlmClient.Tool("done", ("summary", "Could not reproduce; added a note.")),
            // The reviewer determines the reported defect isn't real → rejects the bug,
            // rather than looping request_changes on a fix for a non-bug.
            ScriptedLlmClient.Tool("reject_bug", ("reason", "Not reproducible; code already meets the contract.")));

        var runner = Runner(llm);
        await runner.RunAsync(_tasks.Get(bug.Id));
        var outcome = await runner.RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.Rejected, outcome!.Status);         // closed, not looped
        Assert.Equal(TaskStatus.Rejected, _tasks.Get(bug.Id).Status);
        Assert.Contains("REJECTED", _tasks.Get(bug.Id).ProgressNote!);
        Assert.False(_workspaces.Exists(bug.Id));                    // nothing merged; branch discarded
    }
}
