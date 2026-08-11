using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Design;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// M3 acceptance: the Principal reads requirements and authors structure,
/// conventions, contracts, and a task DAG; the coverage gate catches a
/// requirement with no task; the sign-off gate holds tasks until approval.
///
/// Every model turn is hardcoded — the harness around the model is under test.
/// </summary>
public class DesignPhaseTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-design-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;

    public DesignPhaseTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
        _tasks = new TaskRepository(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_dataRoot, recursive: true);
    }

    /// <summary>Seed requirement files into trunk, the way the PM's chat would have.</summary>
    private void SeedRequirements(params string[] fileNames)
    {
        var seed = Path.Combine(_dataRoot, "seed");
        Git.Require(_paths.ProjectDir(Project), "clone", _paths.ProjectBareRepo(Project), seed);
        var reqDir = Path.Combine(seed, "docs", "requirements");
        Directory.CreateDirectory(reqDir);
        File.WriteAllText(Path.Combine(reqDir, "INDEX.md"), "# Requirements\n\nVERSION: 1\n");
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(reqDir, name), $"# {name}\n\nVERSION: 1\n\nA requirement.\n");
        Git.Require(seed, "add", "-A");
        Git.Require(seed, "commit", "-m", "docs: requirements");
        Git.Require(seed, "push", "origin", "master");
        Directory.Delete(seed, recursive: true);
    }

    private DesignPhase Design(ILlmClient llm) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());

    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"master:{path}").Stdout;

    private static string CreateTask(string title, string objective, string requirement) =>
        ScriptedLlmClient.Tool("create_task", ("display_name", "Some work"), ("milestone", "Build"),
            ("title", title), ("objective", objective), ("requirements_ref", requirement),
            ("acceptance", $"{title} is implemented and its tests pass."));

    [Fact]
    public async Task The_principal_authors_structure_and_a_covered_task_dag()
    {
        SeedRequirements("01-todos.md", "02-accounts.md");

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file",
                ("path", "CONVENTIONS.md"), ("content", "# Conventions\n\nC#/.NET. xUnit. One class per file.")),
            ScriptedLlmClient.Tool("write_file",
                ("path", "docs/design/03-contracts/cli.md"), ("content", "# CLI\n\n`todo add <text>`")),
            CreateTask("Todo storage", "Add and complete todos", "01-todos.md@v1"),
            CreateTask("Accounts", "Sign up and per-user lists", "02-accounts.md@v1"),
            ScriptedLlmClient.Tool("add_dependency", ("task", "2"), ("depends_on", "1")),
            ScriptedLlmClient.Tool("done", ("summary", "Two modules: todos, then accounts on top.")));

        var outcome = await Design(llm).RunAsync();

        Assert.Equal(EndReason.Done, outcome.End);
        Assert.Equal(2, outcome.TasksCreated);

        // The structure landed in the bare repo.
        Assert.Contains("C#/.NET", ShowFromTrunk("CONVENTIONS.md"));
        Assert.Contains("todo add", ShowFromTrunk("docs/design/03-contracts/cli.md"));

        // The task DAG is on the board, born `created`, each naming its requirement.
        var tasks = _tasks.List();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(TaskStatus.Created, t.Status));
        Assert.All(tasks, t => Assert.Equal(AgentRole.Engineer, t.AssignedRole));
        Assert.Equal(new RequirementsRef("01-todos.md", 1), tasks[0].RequirementsRef);

        // The dependency edge exists: accounts waits on todos.
        Assert.Equal([tasks[0].Id], _tasks.DependenciesOf(tasks[1].Id));

        // Coverage gate: every requirement mapped.
        Assert.True(outcome.Coverage.Complete);
        Assert.Empty(outcome.Coverage.Uncovered);
    }

    [Fact]
    public async Task A_project_with_completed_work_gets_the_change_request_impact_brief()
    {
        SeedRequirements("01-todos.md");
        // A prior build already finished a task — so a fresh design run is a change request,
        // and the Principal should do impact analysis rather than design from scratch.
        _tasks.Insert(TaskRecord.Create(
            TaskType.Task, "Existing feature", "Already built and merged", 100_000,
            assignedRole: AgentRole.Engineer) with { Status = TaskStatus.Done });

        var llm = new ScriptedLlmClient(
            CreateTask("Add due dates", "Todos gain an optional due date", "01-todos.md@v1"),
            ScriptedLlmClient.Tool("done", ("summary", "One delta task for due dates; low risk.")));

        var outcome = await Design(llm).RunAsync();

        // The Principal got the impact-analysis brief, not the greenfield one.
        var brief = llm.Requests[0].Messages[0].Content;
        Assert.Contains("impact analysis", brief, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already exists", brief);

        // Only the delta task was created; the existing done task is left alone.
        Assert.Equal(1, outcome.TasksCreated);
        Assert.Equal(TaskStatus.Created, _tasks.List().Single(t => t.Title == "Add due dates").Status);
        Assert.Equal(TaskStatus.Done, _tasks.List().Single(t => t.Title == "Existing feature").Status);
    }

    [Fact]
    public async Task The_coverage_gate_catches_a_requirement_with_no_task()
    {
        SeedRequirements("01-todos.md", "02-accounts.md");

        // The Principal only covers todos and forgets accounts.
        var llm = new ScriptedLlmClient(
            CreateTask("Todo storage", "Add and complete todos", "01-todos.md@v1"),
            ScriptedLlmClient.Tool("done", ("summary", "Did the todos module.")));

        var outcome = await Design(llm).RunAsync();

        Assert.False(outcome.Coverage.Complete);
        Assert.Equal(["02-accounts.md"], outcome.Coverage.Uncovered);
    }

    [Fact]
    public async Task Design_tasks_are_not_claimable_on_their_own()
    {
        SeedRequirements("01-todos.md");
        var llm = new ScriptedLlmClient(
            CreateTask("Todo storage", "Add and complete todos", "01-todos.md@v1"),
            ScriptedLlmClient.Tool("done", ("summary", "Designed the todos module.")));

        await Design(llm).RunAsync();

        // DesignPhase.RunAsync only authors the plan — the tasks it creates are born
        // `created`, not `ready`. Releasing them to the board is the caller's job
        // (TaskRunner.DecomposeFeatureAsync, run autonomously once a Feature reaches
        // it — there is no separate client sign-off step on the design itself).
        var runner = new Forge.Core.Scheduling.TaskRunner(
            _paths, Project, _conn,
            new MeteredLlmClient(new ScriptedLlmClient(), _conn, TestPrices.Catalog),
            new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());
        Assert.Null(runner.NextTask(AgentRole.Engineer));
        Assert.Equal(TaskStatus.Created, _tasks.List().Single().Status);
    }

    [Fact]
    public async Task A_malformed_task_packet_is_refused_and_reported_not_created()
    {
        SeedRequirements("01-todos.md");

        // First create_task has an empty objective (the factory rejects it); the
        // second names the Feature type — the Principal's own parent unit, never an
        // engineer task, so it has no engineer template and is refused here instead of
        // crashing the runner at claim time. Then it recovers.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("create_task", ("display_name", "Some work"), ("milestone", "Build"), ("title", "Bad"), ("objective", "")),
            ScriptedLlmClient.Tool("create_task", ("display_name", "Some work"), ("milestone", "Build"),
                ("title", "Also bad"), ("objective", "Investigate options"),
                ("acceptance", "Options are documented."), ("type", "feature")),
            // No acceptance — refused.
            ScriptedLlmClient.Tool("create_task", ("display_name", "Some work"), ("milestone", "Build"),
                ("title", "Review and merge"), ("objective", "Review the router, then merge it")),
            CreateTask("Todo storage", "Add and complete todos", "01-todos.md@v1"),
            ScriptedLlmClient.Tool("done", ("summary", "Recovered and created the task.")));

        var outcome = await Design(llm).RunAsync();

        // Only the valid task exists; the malformed ones never hit the board.
        Assert.Equal(1, outcome.TasksCreated);
        Assert.Equal("Todo storage", _tasks.List().Single().Title);

        // The refusals were delivered to the model as observations, not crashes.
        var observations = string.Join("\n", llm.Requests.Skip(1).Select(r => r.Messages[^1].Content));
        Assert.Contains("ERROR:", observations);
        Assert.Contains("task, bug, or chore", observations);

        // The done(summary) is the design phase's client-facing summary.
        Assert.Equal("Recovered and created the task.", outcome.Summary);
    }

    [Fact]
    public async Task The_principal_sees_the_whole_workspace_including_code()
    {
        // Unlike the PM, the Principal is a technical role and may read src/.
        var seed = Path.Combine(_dataRoot, "seed");
        Git.Require(_paths.ProjectDir(Project), "clone", _paths.ProjectBareRepo(Project), seed);
        Directory.CreateDirectory(Path.Combine(seed, "src"));
        File.WriteAllText(Path.Combine(seed, "src", "Existing.cs"), "class Existing { }");
        Git.Require(seed, "add", "-A");
        Git.Require(seed, "commit", "-m", "feat: seed code");
        Git.Require(seed, "push", "origin", "master");
        Directory.Delete(seed, recursive: true);

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("read_file", ("path", "src/Existing.cs")),
            ScriptedLlmClient.Tool("done", ("summary", "Reviewed the existing code before designing.")));

        await Design(llm).RunAsync();

        // The read succeeded (no REFUSED) — the Principal's scope is the whole workspace.
        var observation = llm.Requests[1].Messages[^1].Content;
        Assert.Contains("class Existing", observation);
        Assert.DoesNotContain("REFUSED", observation);
    }
}
