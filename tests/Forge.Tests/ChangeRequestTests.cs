using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// A change to a built project is shown, recorded and planned as the delta: the client reads
/// what changed rather than the whole specification, the ask is kept forever under
/// docs/requirements/changes/, and the requirement files stay a living spec.
/// </summary>
public class ChangeRequestTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-cr-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;

    public ChangeRequestTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PmChat Chat(ILlmClient llm) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());

    /// <summary>Commits a requirement file to trunk the way the PM would, and returns trunk's head.</summary>
    private string SeedRequirement(string body, string message)
    {
        var seed = Path.Combine(_dataRoot, $"seed-{Guid.NewGuid():N}");
        Git.Require(_paths.ProjectDir(Project), "clone", _paths.ProjectBareRepo(Project), seed);
        var dir = Path.Combine(seed, "docs", "requirements");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "01-todos.md"), body);
        Git.Require(seed, "add", "-A");
        Git.Require(seed, "commit", "-m", message);
        Git.Require(seed, "push", "origin", "master");
        Directory.Delete(seed, recursive: true);
        return Git.Require(_paths.ProjectBareRepo(Project), "rev-parse", "master").Stdout.Trim();
    }

    /// <summary>A delivered project: the Feature that was built, and the spec the client received.</summary>
    private string Delivered(string requirement)
    {
        new TaskRepository(_conn).Insert(TaskRecord.Create(
            TaskType.Feature, "The todo app", "build it", 60_000,
            assignedRole: AgentRole.Principal, createdBy: "pm"));
        var sha = SeedRequirement(requirement, "docs(pm): requirements");
        new ProjectMetaRepository(_conn).Set(SpecBaseline.Key, sha);
        return sha;
    }

    [Fact]
    public void The_client_is_shown_only_the_lines_the_change_adds_and_removes()
    {
        Delivered("# 01 Todos\n\n- A todo can be added.\n- A todo can be deleted.\n");
        SeedRequirement(
            "# 01 Todos\n\n- A todo can be added.\n- A todo can be archived.\n",
            "docs(pm): archive instead of delete");

        var changes = SpecReader.Changes(_paths, Project, SpecBaseline.Get(_conn));

        var change = Assert.Single(changes);
        Assert.Equal("01-todos.md", change.File);
        Assert.Contains("- A todo can be archived.", change.Added);
        Assert.Contains("- A todo can be deleted.", change.Removed);
        // The untouched line is in neither list: that is the whole point of showing the delta.
        Assert.DoesNotContain("- A todo can be added.", change.Added);
        Assert.DoesNotContain("- A todo can be added.", change.Removed);
    }

    [Fact]
    public void A_project_that_has_never_been_delivered_has_no_delta_to_show()
    {
        SeedRequirement("# 01 Todos\n\n- A todo can be added.\n", "docs(pm): requirements");

        // No baseline: the first build's whole specification is the change, and the review
        // dialog falls back to rendering it in full.
        Assert.Empty(SpecReader.Changes(_paths, Project, SpecBaseline.Get(_conn)));
    }

    [Fact]
    public async Task A_change_to_a_built_project_is_refused_until_the_clients_own_words_are_recorded()
    {
        Delivered("# 01 Todos\n\n- A todo can be added.\n");

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("propose_requirements",
                ("title", "Archive todos"), ("objective", "todos can be archived")),
            ScriptedLlmClient.Tool("reply", ("message", "Recorded — please take a look.")));

        await Chat(llm).SendAsync("I'd rather archive todos than delete them.");

        Assert.Contains("change_request", llm.Observations(1));
        Assert.Null(RequirementsProposal.Load(_conn));
    }

    [Fact]
    public async Task An_accepted_change_is_kept_as_a_numbered_entry_naming_what_the_client_asked_for()
    {
        Delivered("# 01 Todos\n\n- A todo can be added.\n");

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("propose_requirements",
                ("title", "Archive todos"),
                ("objective", "todos can be archived instead of deleted"),
                ("requirements_ref", "01-todos.md"),
                ("change_request", "I'd rather archive todos than delete them."),
                ("changes", "§1: deleting a todo becomes archiving it."),
                ("removed", "§1: deleting a todo removes it for good.")),
            ScriptedLlmClient.Tool("reply", ("message", "Drafted — please take a look.")));

        await Chat(llm).SendAsync("I'd rather archive todos than delete them.");

        var proposal = RequirementsProposal.Load(_conn);
        Assert.Equal("docs/requirements/changes/001-archive-todos.md", proposal!.ChangeEntry);

        var entry = Assert.Single(ChangeLog.Read(_paths, Project));
        Assert.Equal(1, entry.Number);
        Assert.Equal("Archive todos", entry.Title);
        Assert.Contains("I'd rather archive todos than delete them.", entry.Markdown);
        Assert.Contains("§1: deleting a todo removes it for good.", entry.Markdown);

        // Still a draft until the client accepts it.
        Assert.False(entry.Approved);
        Assert.Contains(ChangeLog.Proposed, entry.Markdown);
    }

    [Fact]
    public void Accepting_a_change_stamps_its_entry_approved_on_trunk()
    {
        var markdown = ChangeLog.Render(
            1, "Archive todos", "I'd rather archive them.", "§1 now archives.", null, "01-todos.md");

        var approved = ChangeLog.Approve(markdown);

        Assert.NotNull(approved);
        Assert.DoesNotContain(ChangeLog.Proposed, approved);
        Assert.Contains("Status: approved", approved);
        // Stamping twice would rewrite a date that is already a matter of record.
        Assert.Null(ChangeLog.Approve(approved!));
    }

    [Fact]
    public async Task Redrafting_a_pending_change_replaces_its_entry_instead_of_numbering_a_second_one()
    {
        Delivered("# 01 Todos\n\n- A todo can be added.\n");

        ScriptedTurn Propose(string title) => ScriptedLlmClient.Tool("propose_requirements",
            ("title", title), ("objective", "todos can be archived"),
            ("change_request", "archive rather than delete"), ("changes", "§1 archives"));

        await Chat(new ScriptedLlmClient(
            Propose("Archive todos"),
            ScriptedLlmClient.Tool("reply", ("message", "Drafted.")))).SendAsync("archive them");

        await Chat(new ScriptedLlmClient(
            Propose("Archive and restore todos"),
            ScriptedLlmClient.Tool("reply", ("message", "Redrafted.")))).SendAsync("also let me restore");

        // One ask, one entry — still CR-001, under the new title.
        var entry = Assert.Single(ChangeLog.Read(_paths, Project));
        Assert.Equal(1, entry.Number);
        Assert.Equal("docs/requirements/changes/001-archive-and-restore-todos.md", entry.File);
        Assert.Equal(entry.File, RequirementsProposal.Load(_conn)!.ChangeEntry);
    }
}
