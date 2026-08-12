using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class ToolCallTests
{
    private static ToolCall Call(params (string Name, string Value)[] args) =>
        new("read_file", args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));

    [Fact]
    public void Missing_and_malformed_arguments_are_reported_not_guessed()
    {
        var call = Call(("path", "a.txt"), ("start", "soon"));

        Assert.Equal("a.txt", call.Arg("path"));
        Assert.Throws<ToolCallException>(() => call.Arg("pattern"));
        Assert.Throws<ToolCallException>(() => call.OptionalInt("start"));
        Assert.Null(call.Optional("end"));
    }

    [Fact]
    public void An_integer_argument_is_read_as_one_and_a_blank_counts_as_absent()
    {
        Assert.Equal(3, Call(("start", "3")).OptionalInt("start"));
        Assert.Null(Call(("start", "   ")).OptionalInt("start"));
    }
}

public class AgentToolsetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-tools-{Guid.NewGuid():N}");
    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");
    private readonly AgentToolset _toolset;
    private readonly TaskRepository _tasks;
    private readonly TaskRecord _task;

    public AgentToolsetTests()
    {
        Directory.CreateDirectory(_root);
        _tasks = new TaskRepository(_conn);
        _task = _tasks.Insert(TaskRecord.Create(TaskType.Task, "T", "O", 10_000));
        var executor = new ToolExecutor(_root, ["echo"], new SecretsVault(Path.Combine(_root, ".vault")));
        _toolset = new AgentToolset(executor, _conn, AgentRecipe.Engineer, _task);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private Task<ToolOutcome> Run(string name, params (string Name, string Value)[] args) =>
        _toolset.ExecuteAsync(new ToolCall(name,
            args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal)));

    [Fact]
    public async Task Write_then_read_round_trips_through_the_workspace()
    {
        var wrote = await Run("write_file", ("path", "src/Foo.cs"), ("content", "class Foo { }"));
        Assert.Contains("Wrote src/Foo.cs", wrote.Observation);
        Assert.Equal("class Foo { }\n", File.ReadAllText(Path.Combine(_root, "src", "Foo.cs")));

        var read = await Run("read_file", ("path", "src/Foo.cs"));
        Assert.Contains("1\tclass Foo { }", read.Observation);
    }

    [Fact]
    public async Task File_bug_refuses_without_a_prior_run_then_attaches_the_captured_trace()
    {
        var exec = new ToolExecutor(_root, ["echo"], new SecretsVault(Path.Combine(_root, ".vault")));
        var qa = new AgentToolset(exec, _conn, AgentRecipe.Qa, task: null); // QA is project-scoped
        Task<ToolOutcome> Qa(string name, params (string Name, string Value)[] args) =>
            qa.ExecuteAsync(new ToolCall(name,
                args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal)));

        // No run yet → the bug is refused and nothing is filed. Evidence is mandatory.
        var refused = await Qa("file_bug", ("title", "T"), ("expected", "E"));
        Assert.Contains("needs evidence", refused.Observation);
        Assert.Empty(_tasks.List().Where(t => t.Type == TaskType.Bug));

        // After a real run, file_bug embeds that run's actual output verbatim — the model
        // never gets to type the "actual", so it cannot fabricate one.
        await Qa("run", ("command", "echo boom"));
        await Qa("file_bug", ("title", "T"), ("expected", "E"));
        var bug = Assert.Single(_tasks.List().Where(t => t.Type == TaskType.Bug));
        Assert.Contains("## Observed", bug.Objective);
        Assert.Contains("boom", bug.Objective);
        Assert.Equal(TaskStatus.Triage, bug.Status);
    }

    [Fact]
    public async Task Grep_and_list_dir_see_only_the_workspace()
    {
        await Run("write_file", ("path", "a.txt"), ("content", "alpha\nbeta"));
        await Run("write_file", ("path", "b.txt"), ("content", "gamma"));

        var hits = await Run("grep", ("pattern", "^bet"));
        Assert.Contains("a.txt:2: beta", hits.Observation);

        var listing = await Run("list_dir");
        Assert.Contains("a.txt", listing.Observation);
        Assert.Contains("b.txt", listing.Observation);
    }

    [Fact]
    public async Task Jail_violations_come_back_as_observations_not_crashes()
    {
        var escape = await Run("read_file", ("path", "../../../etc/passwd"));
        Assert.StartsWith("REFUSED:", escape.Observation);
        Assert.Null(escape.End);

        var disallowed = await Run("run", ("command", "python evil.py"));
        Assert.StartsWith("REFUSED:", disallowed.Observation);

        var missing = await Run("read_file", ("path", "nope.txt"));
        Assert.StartsWith("ERROR:", missing.Observation);
    }

    [Fact]
    public async Task Progress_note_persists_immediately_so_it_survives_a_kill()
    {
        await Run("progress_note", ("note", "Wrote the parser; tests next."));
        Assert.Equal("Wrote the parser; tests next.", _tasks.Get(_task.Id).ProgressNote);
        Assert.Equal("Wrote the parser; tests next.", _toolset.LastProgressNote);
    }

    [Fact]
    public async Task Done_and_escalate_end_the_loop_and_an_engineer_escalation_climbs_to_the_principal()
    {
        var done = await Run("done", ("summary", "Implemented and verified."));
        Assert.Equal(EndReason.Done, done.End);

        var escalated = await Run("escalate", ("reason", "The contract is ambiguous."));
        Assert.Equal(EndReason.Escalated, escalated.End);

        // The ladder is engineer → principal (not straight to the PM); the Principal is
        // the one who can re-scope, and only bumps it to the PM if the client must decide.
        Assert.Empty(new MessageRepository(_conn).Pending("pm"));
        var message = Assert.Single(new MessageRepository(_conn).Pending("principal"));
        Assert.IsType<EscalationMessage>(message);
        Assert.Contains("ambiguous", message.Payload);
    }

    [Fact]
    public async Task Unknown_tools_are_named_back_to_the_model()
    {
        var result = await Run("delete_everything", ("path", "/"));
        Assert.Contains("no tool 'delete_everything' is available to you", result.Observation);
        Assert.Contains("write_file", result.Observation);
    }

    [Fact]
    public async Task A_tool_outside_the_roles_recipe_is_refused_even_though_it_exists()
    {
        // `reply` is a real tool — the PM's. An engineer asking for it gets nothing.
        var result = await Run("reply", ("message", "Here's my status update!"));
        Assert.Contains("no tool 'reply' is available to you", result.Observation);
        Assert.Empty(new MessageRepository(_conn).Log());
    }
}

public class AgentRecipeTests
{
    [Fact]
    public void Every_built_role_is_internally_consistent()
    {
        foreach (var recipe in new[] { AgentRecipe.Engineer, AgentRecipe.Pm, AgentRecipe.Principal })
        {
            Assert.NotEmpty(recipe.Tools);
            Assert.All(recipe.Tools, t => Assert.Contains(t, AgentToolset.Catalogue.Keys));
            Assert.Equal(recipe.Tools.Contains("run"), recipe.ToolAllowlist.Count > 0);
            Assert.True(recipe.DefaultBudget > 0 && recipe.IterationCap > 0);
        }
    }

    [Fact]
    public void A_role_with_no_recipe_throws_rather_than_half_working()
    {
        var ex = Assert.Throws<NotSupportedException>(() => AgentRecipe.For(AgentRole.Researcher));
        Assert.Contains("Researcher", ex.Message);
    }

    [Fact]
    public void A_recipe_with_a_typo_in_its_tool_list_is_rejected_at_first_use()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            (AgentRecipe.Engineer with { Tools = ["read_file", "wirte_file"] }).Validate());
        Assert.Contains("wirte_file", ex.Message);
    }

    [Fact]
    public void Run_and_its_binary_allowlist_must_agree()
    {
        Assert.Throws<ArgumentException>(() => (AgentRecipe.Engineer with { ToolAllowlist = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (AgentRecipe.Pm with { ToolAllowlist = ["dotnet"] }).Validate());
    }
}

public class PromptAssemblerTests
{
    private static readonly PromptAssembler Assembler = new(PromptLibrary.Resolve());

    private static TaskRecord Task(string? progressNote = null) => TaskRecord.Create(
        TaskType.Task, "Add login", "Users can log in", 60_000,
        acceptanceCriteria: "POST /login returns 200",
        contextPaths: ["src/auth/"],
        requirementsRef: RequirementsRef.Parse("01-users-auth.md@v2")) with
    {
        Id = 7,
        ProgressNote = progressNote,
    };

    [Fact]
    public void System_prompt_layers_role_then_task_type_then_tool_protocol()
    {
        var jail = new PathJail(Path.GetTempPath());
        var prompt = Assembler.SystemPrompt(AgentRecipe.Engineer, Task(), jail);

        Assert.Contains("Role: Software Engineer", prompt);   // Layer A
        Assert.Contains("Task type: Task", prompt);        // Layer B
        Assert.Contains("Your entire reply is tool calls", prompt); // generated protocol
        Assert.True(prompt.IndexOf("Role: Software Engineer", StringComparison.Ordinal)
                  < prompt.IndexOf("Task type: Task", StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_protocol_advertises_exactly_the_recipes_allowlist()
    {
        var protocol = PromptAssembler.ToolProtocol(AgentRecipe.Engineer);
        Assert.Contains("Allowed: dotnet, git", protocol);
        Assert.Contains("{{secret:NAME}}", protocol);
    }

    [Fact]
    public void Task_packet_carries_the_whole_layer_c_row()
    {
        var packet = PromptAssembler.TaskPacket(Task());

        Assert.Contains("# Task 7: Add login", packet);
        Assert.Contains("Users can log in", packet);
        Assert.Contains("POST /login returns 200", packet);
        Assert.Contains("01-users-auth.md@v2", packet);
        Assert.Contains("src/auth/", packet);
        Assert.Contains("0 of 60000 tokens", packet);
        Assert.DoesNotContain("predecessor", packet);
    }

    [Fact]
    public void Resuming_hands_the_successor_the_note_and_tells_it_to_verify()
    {
        var packet = PromptAssembler.TaskPacket(Task("Parser done; wire up the CLI next."));

        Assert.Contains("Progress note from your predecessor", packet);
        Assert.Contains("Parser done; wire up the CLI next.", packet);
        Assert.Contains("the repo says what is true", packet);
    }
}

public class AgentLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-loop-{Guid.NewGuid():N}");
    private readonly SqliteConnection _conn = Database.OpenProject(":memory:");
    private readonly TaskRepository _tasks;
    private readonly ToolExecutor _executor;

    public AgentLoopTests()
    {
        Directory.CreateDirectory(_root);
        _tasks = new TaskRepository(_conn);
        _executor = new ToolExecutor(_root, ["echo"], new SecretsVault(Path.Combine(_root, ".vault")));
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private TaskRecord StartTask(int budget = 100_000)
    {
        var task = _tasks.Insert(TaskRecord.Create(
            TaskType.Task, "Add greeting", "Write hello.txt", budget, assignedRole: AgentRole.Engineer));
        _tasks.Transition(task.Id, TaskStatus.Ready);
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        return _tasks.Get(task.Id);
    }

    private AgentLoop Loop(ILlmClient llm, AgentRecipe? recipe = null) =>
        new(llm, _conn, new PromptAssembler(PromptLibrary.Resolve()), recipe ?? AgentRecipe.Engineer);

    [Fact]
    public async Task Acts_observes_and_stops_when_the_agent_reports_done()
    {
        var task = StartTask();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "hello.txt"), ("content", "hi")),
            ScriptedLlmClient.Tool("read_file", ("path", "hello.txt")),
            ScriptedLlmClient.Tool("done", ("summary", "Wrote hello.txt and read it back.")));

        var result = await Loop(llm).RunAsync(task, _executor);

        Assert.Equal(EndReason.Done, result.End);
        Assert.Equal(3, result.Iterations);
        Assert.Equal("hi\n", File.ReadAllText(Path.Combine(_root, "hello.txt")));

        // The observation from turn 1 must be visible to turn 2 — as a tool result now.
        Assert.Contains("[write_file]", llm.Observations(1));
        Assert.Equal("Wrote hello.txt and read it back.", _tasks.Get(task.Id).ProgressNote);

        var instance = Assert.Single(new AgentInstanceRepository(_conn).ForTask(task.Id));
        Assert.Equal(EndReason.Done, instance.EndReason);
        Assert.StartsWith("eng-", instance.Id);
    }

    [Fact]
    public async Task Iteration_cap_stops_a_looping_agent_and_a_note_is_written_regardless()
    {
        var task = StartTask();
        var recipe = AgentRecipe.Engineer with { IterationCap = 4 };
        var llm = new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("list_dir") };

        var result = await Loop(llm, recipe).RunAsync(task, _executor);

        Assert.Equal(EndReason.Iterations, result.End);
        Assert.Equal(4, llm.Calls);
        // The agent never wrote a note; the harness captures its final output as the
        // resume note (ProgressStatus) so a successor sees what it was doing.
        var note = _tasks.Get(task.Id).ProgressNote!;
        Assert.Contains("ProgressStatus", note);
        Assert.Contains("ended iterations", note);
    }

    [Fact]
    public async Task The_final_turn_is_handed_a_forced_stop_message_demanding_done_or_a_note()
    {
        var task = StartTask();
        var recipe = AgentRecipe.Engineer with { IterationCap = 2 };
        var llm = new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("list_dir") };

        await Loop(llm, recipe).RunAsync(task, _executor);

        // The message injected before the last turn (turn 2) is imperative and mandatory —
        // it is added by the loop just-in-time, not carried in the static role prompt.
        var lastTurnUserMessage = llm.Observations(llm.Requests.Count - 1);
        Assert.Contains("LAST turn", lastTurnUserMessage);
        Assert.Contains("MUST", lastTurnUserMessage);
        // It rides the conversation (a just-in-time user turn), never the system prompt.
        Assert.DoesNotContain("LAST turn", llm.Requests[^1].System);
    }

    [Fact]
    public async Task An_agent_that_never_acts_is_nudged_then_cut_off()
    {
        var task = StartTask();
        var llm = new ScriptedLlmClient { Fallback = "Let me think about the best approach here." };

        var result = await Loop(llm).RunAsync(task, _executor);

        Assert.Equal(EndReason.Crash, result.End);
        Assert.Equal(3, llm.Calls); // three strikes, not the full 40-turn cap
        // The nudge shows the syntax rather than naming it, since a model that got the
        // shape wrong cannot act on being told a call was missing.
        var nudge = llm.Requests[1].Messages[^1].Content;
        Assert.Contains("no tool call", nudge, StringComparison.OrdinalIgnoreCase);
        // Names the call to make rather than a syntax — the provider owns the shape now.
        Assert.Contains("read_file", nudge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_whose_every_call_is_refused_is_cut_off_long_before_the_iteration_cap()
    {
        var task = StartTask();
        // A binary that is not on this recipe's allowlist, so every call is refused. A model
        // that keeps re-emitting it would otherwise burn all 40 turns achieving nothing.
        var llm = new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("run", ("command", "dotnet build")) };

        var result = await Loop(llm).RunAsync(task, _executor);

        Assert.Equal(EndReason.Crash, result.End);
        Assert.Equal(5, llm.Calls); // MaxRefusedTurns, not the 40-turn cap
        // The note says the model could not act, so triage sees a formatting failure
        // rather than a task that merely ran out of room.
        Assert.Contains("refused", _tasks.Get(task.Id).ProgressNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_accepted_call_clears_the_refusal_count()
    {
        var task = StartTask();
        // Four refusals, then real work, then four more: neither streak reaches the cap,
        // so the run continues — a boundary hit mid-task must not end the instance.
        var bad = ScriptedLlmClient.Tool("run", ("command", "rm -rf /"));   // not on the allowlist
        var llm = new ScriptedLlmClient(
            bad, bad, bad, bad,
            ScriptedLlmClient.Tool("list_dir"),
            bad, bad, bad, bad,
            ScriptedLlmClient.Tool("done", ("summary", "finished")));

        var result = await Loop(llm).RunAsync(task, _executor);

        Assert.Equal(EndReason.Done, result.End);
        Assert.Equal(10, llm.Calls);
    }

    [Fact]
    public async Task A_refusal_logs_the_arguments_the_model_actually_sent()
    {
        var task = StartTask();
        var sink = new MemoryLogSink();
        // A call naming the wrong argument: the schema lets it through, the tool does not.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("read_file", ("file", "src/Whatever.cs")),
            ScriptedLlmClient.Tool("done", ("summary", "done")));
        var loop = new AgentLoop(llm, _conn, new PromptAssembler(PromptLibrary.Resolve()),
            AgentRecipe.Engineer, new ForgeLogger(sink, "proj"));

        await loop.RunAsync(task, _executor);

        var refusal = Assert.Single(sink.Entries, e => e.Type == EventType.ToolRefused);
        Assert.Contains("requires a non-empty", refusal.Message);
        // Without the arguments a refusal names only what was missing, never what was sent
        // instead — which is what makes a malformed-call loop diagnosable.
        Assert.Contains("emitted:", refusal.Message);
        Assert.Contains("src/Whatever.cs", refusal.Message);
    }

    [Fact]
    public async Task A_turn_with_no_tool_call_is_logged_with_the_providers_reason_for_stopping()
    {
        var task = StartTask();
        var sink = new MemoryLogSink();
        var llm = new ScriptedLlmClient("I will write the file next.") { Fallback = "still thinking" };
        var loop = new AgentLoop(llm, _conn, new PromptAssembler(PromptLibrary.Resolve()),
            AgentRecipe.Engineer, new ForgeLogger(sink, "proj"));

        var result = await loop.RunAsync(task, _executor);

        var first = sink.Entries.First(e => e.Type == EventType.LlmNoToolCall);
        Assert.Contains("stop reason end_turn", first.Message);
        Assert.Contains("I will write the file", first.Message);

        // And the exit itself is on the record, which is what was missing.
        Assert.Equal(EndReason.Crash, result.End);
        var exit = Assert.Single(sink.Entries,
            e => e.Type == EventType.ErrorInternal && e.Message.Contains("consecutive turns"));
        Assert.Contains("end_turn", exit.Message);
    }

    [Fact]
    public async Task A_turn_cut_off_at_the_output_limit_is_told_to_send_it_again_smaller()
    {
        var task = StartTask();
        var sink = new MemoryLogSink();
        // The provider stopped at our ceiling, so the write_file it was emitting is half a
        // JSON object. Running it would write a truncated file; parsing it used to throw.
        var llm = new ScriptedLlmClient(
            new ScriptedTurn("", [new LlmToolCall("call_1", "write_file",
                "{\"path\":\"Program.cs\",\"content\":\"public class P { // cut off he")]))
        {
            Fallback = ScriptedLlmClient.Tool("done", ("summary", "Wrote it in pieces.")),
            StopReason = "max_output_tokens",
            FallbackStopReason = "end_turn",
        };
        var loop = new AgentLoop(llm, _conn, new PromptAssembler(PromptLibrary.Resolve()),
            AgentRecipe.Engineer, new ForgeLogger(sink, "proj"));

        var result = await loop.RunAsync(task, _executor);

        // The instance carried on and finished: the truncated turn cost one round trip.
        Assert.Equal(EndReason.Done, result.End);
        Assert.False(File.Exists(Path.Combine(_root, "Program.cs")));
        Assert.Contains(sink.Entries,
            e => e.Type == EventType.LlmNoToolCall && e.Message.Contains("cut off at the output limit"));
        // The model was told what to do about it, and the partial call was not carried back.
        var retry = llm.Requests[1].Messages[^1];
        Assert.Contains("smaller pieces", retry.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(llm.Requests[1].Messages, m => m.ToolCalls.Count > 0);
    }

    [Fact]
    public async Task Tool_arguments_that_are_not_valid_json_are_refused_rather_than_thrown()
    {
        var task = StartTask();
        // Malformed with no stop reason admitting to it: the loop must still survive it.
        var llm = new ScriptedLlmClient(
            new ScriptedTurn("", [new LlmToolCall("call_1", "write_file", "{\"path\":\"a.txt\",")]))
        {
            Fallback = ScriptedLlmClient.Tool("done", ("summary", "Recovered.")),
        };
        var loop = new AgentLoop(llm, _conn, new PromptAssembler(PromptLibrary.Resolve()),
            AgentRecipe.Engineer);

        var result = await loop.RunAsync(task, _executor);

        Assert.Equal(EndReason.Done, result.End);
        Assert.Contains("not valid JSON", llm.Observations(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_engineer_cannot_change_the_project_layout_with_dotnet_new()
    {
        var toolset = new AgentToolset(_executor, _conn, AgentRecipe.Engineer, StartTask());

        // Padded, since the check collapses whitespace before matching.
        var outcome = await toolset.ExecuteAsync(
            new ToolCall("run", new Dictionary<string, string> { ["command"] = "dotnet  new   classlib" }));

        Assert.True(outcome.Refused);
        Assert.Contains("escalate", outcome.Observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_only_starts_like_a_refused_one_still_runs()
    {
        var toolset = new AgentToolset(_executor, _conn, AgentRecipe.Engineer, StartTask());

        var outcome = await toolset.ExecuteAsync(
            new ToolCall("run", new Dictionary<string, string> { ["command"] = "echo dotnet new" }));

        Assert.False(outcome.Refused);
    }

    [Fact]
    public async Task Budget_exhaustion_ends_the_loop_without_the_model_being_asked_to_stop()
    {
        var task = StartTask(budget: 300); // one call costs 150; the second is refused
        var llm = new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("list_dir") };
        var metered = new MeteredLlmClient(llm, _conn, TestPrices.Catalog);

        var result = await Loop(metered).RunAsync(task, _executor);

        Assert.Equal(EndReason.Budget, result.End);
        Assert.Equal(2, llm.Calls);
        // The loop just stops; parking the task (OutOfBudget, strike, notify) is the
        // runner's job, verified in TaskRunnerTests — the supervisor only refuses.
        var note = _tasks.Get(task.Id).ProgressNote!;
        Assert.Contains("ProgressStatus", note);
    }

    [Fact]
    public async Task A_provider_failure_parks_the_task_instead_of_taking_the_process_down()
    {
        var task = StartTask();
        var failing = new FailingLlmClient("Status Code: TooManyRequests");

        var result = await Loop(failing).RunAsync(task, _executor);

        Assert.Equal(EndReason.Crash, result.End);
        Assert.Contains("TooManyRequests", result.Detail);
        Assert.Contains("LLM call failed", _tasks.Get(task.Id).ProgressNote!);

        // The instance is closed out rather than left dangling mid-run.
        var instance = Assert.Single(new AgentInstanceRepository(_conn).ForTask(task.Id));
        Assert.Equal(EndReason.Crash, instance.EndReason);
        Assert.NotNull(instance.EndedAt);
    }

    private sealed class FailingLlmClient(string message) : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new HttpRequestException(message);
    }

    [Fact]
    public async Task A_provider_timeout_surfacing_as_cancellation_is_a_crash_not_a_process_kill()
    {
        var task = StartTask();
        // A network timeout reaches us as TaskCanceledException even though we never
        // cancelled. Before the fix it escaped the crash handler and killed the run.
        var timingOut = new CancelThrowingLlmClient();

        var result = await Loop(timingOut).RunAsync(task, _executor); // must not throw

        Assert.Equal(EndReason.Crash, result.End);
        Assert.Contains("LLM call failed", _tasks.Get(task.Id).ProgressNote!);
    }

    [Fact]
    public async Task Genuine_cancellation_propagates_and_stops_the_run()
    {
        var task = StartTask();
        var llm = new CancelThrowingLlmClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // our own token is tripped — this is a real stop, not a timeout

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loop(llm).RunAsync(task, _executor, cts.Token));
    }

    /// <summary>Always raises TaskCanceledException — stands in for an HTTP-stack timeout.</summary>
    private sealed class CancelThrowingLlmClient : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new TaskCanceledException("simulated provider timeout");
    }

    [Fact]
    public async Task The_supervisors_70_percent_nudge_is_delivered_into_the_conversation()
    {
        var task = StartTask(budget: 400); // 150/call → the second call crosses the 280-token line
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("list_dir"),
            ScriptedLlmClient.Tool("list_dir"),
            ScriptedLlmClient.Tool("done", ("summary", "Wrapped up as instructed.")));
        var metered = new MeteredLlmClient(llm, _conn, TestPrices.Catalog);

        await Loop(metered).RunAsync(task, _executor);

        var observations = llm.Observations(2);
        Assert.Contains("[message: system_nudge from system]", observations);
        Assert.Contains("Wrap up now", observations);
    }
}
