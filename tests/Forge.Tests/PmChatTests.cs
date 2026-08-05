using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// M2 acceptance: the client talks to the PM, requirements land in the project
/// repo as versioned files, a milestone plan lands in the database, and the
/// conversation survives the instance that produced it.
///
/// Every model turn below is hardcoded — what is under test is the harness
/// around the model, not the model.
/// </summary>
public class PmChatTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-pm-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;

    public PmChatTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_dataRoot, recursive: true);
    }

    private PmChat Chat(ILlmClient llm) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());

    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"master:{path}").Stdout;

    private static string Reply(string text) => ScriptedLlmClient.Tool("reply", ("message", text));

    [Fact]
    public async Task A_plain_answer_is_recorded_as_conversation_and_changes_nothing_in_the_repo()
    {
        var llm = new ScriptedLlmClient(Reply("Happy to help — what problem does this solve, and for whom?"));

        var turn = await Chat(llm).SendAsync("I want to build a todo app.");

        Assert.Equal(EndReason.Done, turn.End);
        Assert.StartsWith("Happy to help", turn.Reply);
        Assert.False(turn.DocumentsChanged);

        // The exchange is in the log after Iris's opening greeting: the client's question
        // and the PM's answer.
        var log = new MessageRepository(_conn).Log();
        Assert.Collection(log,
            m => Assert.Equal(ProjectBootstrap.Greeting, m.Payload),
            m => { Assert.IsType<QuestionMessage>(m); Assert.Equal("client", m.FromAgent); },
            m => { Assert.IsType<AnswerMessage>(m); Assert.Equal("client", m.ToAgent); });
    }

    [Fact]
    public async Task Requirements_the_pm_writes_are_committed_to_the_project_repo()
    {
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file",
                ("path", "docs/requirements/INDEX.md"),
                ("content", "# Requirements\n\nVERSION: 1\n\n- 01-todos.md@v1 — creating and completing todos")),
            ScriptedLlmClient.Tool("write_file",
                ("path", "docs/requirements/01-todos.md"),
                ("content", "# Todos\n\nVERSION: 1\n\nA user can add a todo and mark it done.")),
            Reply("I've written the first requirement section covering todos. Does that match what you meant?"));

        var turn = await Chat(llm).SendAsync("Users should be able to add todos and tick them off.");

        Assert.True(turn.DocumentsChanged);
        Assert.Contains("01-todos.md@v1", ShowFromTrunk("docs/requirements/INDEX.md"));
        Assert.Contains("mark it done", ShowFromTrunk("docs/requirements/01-todos.md"));
        Assert.Contains("first requirement section", turn.Reply);
    }

    [Fact]
    public async Task The_milestone_plan_lands_in_the_database_not_only_in_prose()
    {
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("add_milestone",
                ("name", "Working todo list"), ("description", "Add, list and complete todos")),
            ScriptedLlmClient.Tool("add_milestone",
                ("name", "Accounts"), ("description", "Sign-up and per-user lists")),
            Reply("Two milestones: a working todo list first, then accounts."));

        await Chat(llm).SendAsync("Can you sketch a plan?");

        var milestones = new MilestoneRepository(_conn).List();
        Assert.Equal(["Working todo list", "Accounts"], milestones.Select(m => m.Name));
        Assert.Equal([1, 2], milestones.Select(m => m.Ordinal));
    }

    [Fact]
    public async Task Each_turn_replays_the_conversation_from_the_database_to_a_fresh_instance()
    {
        var chat = Chat(new ScriptedLlmClient { Fallback = Reply("Noted.") });
        await chat.SendAsync("Build me a todo app.");

        var second = new ScriptedLlmClient(Reply("Understood — mobile first."));
        await Chat(second).SendAsync("It should work on mobile.");

        // The second instance never saw turn one, but the messages table did.
        var conversation = second.Requests[0].Messages;
        Assert.Equal(["user", "assistant", "user"], conversation.Select(m => m.Role));
        Assert.Equal("Build me a todo app.", conversation[0].Content);
        Assert.Equal("Noted.", conversation[1].Content);
        Assert.Equal("It should work on mobile.", conversation[2].Content);

        // Two instances, both retired, both attributed to the PM.
        var instances = new AgentInstanceRepository(_conn).ForTask(0);
        Assert.Empty(instances); // chat turns are not task work
    }

    [Fact]
    public async Task The_pm_cannot_read_or_write_code_even_though_it_shares_the_repo()
    {
        // Seed a source file into the repo the way an engineer would.
        var seed = Path.Combine(_dataRoot, "seed");
        Git.Require(_paths.ProjectDir(Project), "clone", _paths.ProjectBareRepo(Project), seed);
        Directory.CreateDirectory(Path.Combine(seed, "src"));
        File.WriteAllText(Path.Combine(seed, "src", "Secret.cs"), "class Secret { const string Key = \"hunter2\"; }");
        Git.Require(seed, "add", "-A");
        Git.Require(seed, "commit", "-m", "feat: add source");
        Git.Require(seed, "push", "origin", "master");

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("read_file", ("path", "src/Secret.cs")),
            ScriptedLlmClient.Tool("write_file", ("path", "src/Sneaky.cs"), ("content", "// PM was here")),
            ScriptedLlmClient.Tool("grep", ("pattern", "hunter2"), ("path", "src")),
            Reply("I can't see the code — that's the Principal's area. What should the behaviour be?"));

        var turn = await Chat(llm).SendAsync("What does the code do?");

        // Three refusals, all delivered as observations rather than crashes.
        var observations = string.Join("\n", llm.Requests.Skip(1).Select(r => r.Messages[^1].Content));
        Assert.Equal(3, observations.Split("REFUSED:").Length - 1);
        Assert.DoesNotContain("hunter2", observations);

        Assert.False(turn.DocumentsChanged);
        Assert.Throws<GitException>(() => ShowFromTrunk("src/Sneaky.cs"));
    }

    [Fact]
    public async Task A_pm_that_never_replies_still_tells_the_client_what_happened()
    {
        // The model writes files all turn and never calls reply, hitting the cap.
        var llm = new ScriptedLlmClient
        {
            Fallback = ScriptedLlmClient.Tool("write_file", ("path", "docs/notes.md"), ("content", "thinking…")),
        };

        var turn = await Chat(llm).SendAsync("Are we on track?");

        Assert.Equal(EndReason.Iterations, turn.End);
        Assert.Contains("turn limit", turn.Reply);

        // The client sees the explanation in the log, not silence.
        var last = new MessageRepository(_conn).Log()[^1];
        Assert.Equal("client", last.ToAgent);
        Assert.IsType<StatusMessage>(last);
    }

    [Fact]
    public async Task A_provider_failure_is_reported_to_the_client_in_plain_language()
    {
        var turn = await Chat(new FailingLlmClient()).SendAsync("Hello?");

        Assert.Equal(EndReason.Crash, turn.End);
        Assert.Contains("couldn't complete that turn", turn.Reply);
        Assert.Contains("TooManyRequests", turn.Reply);
    }

    private sealed class FailingLlmClient : ILlmClient
    {
        public string ModelFor(ModelTier tier) => TestPrices.For(tier);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new HttpRequestException("Status Code: TooManyRequests");
    }

    // ---- Human review of escalated bugs, through the PM (M6 change-request follow-up) ----

    /// <summary>A bug the Principal escalated for a human decision: in triage, with a pending pm escalation.</summary>
    private long ParkedBug(string title = "Flagged bug")
    {
        var bug = new TaskRepository(_conn).Insert(TaskRecord.Create(
            TaskType.Bug, title, "## Expected\ne\n## Observed\no", 50_000,
            assignedRole: AgentRole.Engineer) with { Status = TaskStatus.Triage });
        new MessageRepository(_conn).Insert(Message.Create(
            MessageType.Escalation, "principal", "pm", $"Bug {bug.Id} needs a human decision: is this real?", bug.Id));
        return bug.Id;
    }

    [Fact]
    public async Task Open_escalations_are_surfaced_into_the_pms_turn()
    {
        var bugId = ParkedBug("Phantom");
        var llm = new ScriptedLlmClient(Reply("There's a bug flagged for your decision."));

        await Chat(llm).SendAsync("Anything need me?");

        var turnPrompt = llm.Requests[0].Messages[^1].Content;
        Assert.Contains($"task {bugId}", turnPrompt);   // the PM was told what's open
        Assert.Contains("reject_bug", turnPrompt);       // …and how to resolve it
    }

    [Fact]
    public async Task The_pm_rejects_a_bug_the_client_reviewed_and_clears_the_escalation()
    {
        var bugId = ParkedBug();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("reject_bug", ("task", bugId.ToString()), ("reason", "Client says it's expected behaviour.")),
            Reply("Rejected it, as you asked."));

        await Chat(llm).SendAsync("That's expected behaviour — reject it.");

        Assert.Equal(TaskStatus.Rejected, new TaskRepository(_conn).Get(bugId).Status);
        // The escalation is resolved, so the bug no longer strands the board or re-surfaces.
        Assert.DoesNotContain(new MessageRepository(_conn).Pending("pm"), m => m.TaskId == bugId);
    }

    [Fact]
    public async Task The_pm_retriages_a_bug_with_the_clients_guidance()
    {
        var bugId = ParkedBug();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("retriage_bug", ("task", bugId.ToString()),
                ("note", "Client says check the endpoint, not the service.")),
            Reply("Sent it back to the Principal with your note."));

        await Chat(llm).SendAsync("It might be real — have them look at the endpoint.");

        var bug = new TaskRepository(_conn).Get(bugId);
        Assert.Equal(TaskStatus.Triage, bug.Status);                 // back on the Principal's queue
        Assert.Contains("check the endpoint", bug.ProgressNote!);     // the client's guidance carried over
        Assert.DoesNotContain(new MessageRepository(_conn).Pending("pm"), m => m.TaskId == bugId);
    }
}
