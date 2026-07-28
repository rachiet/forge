using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Ci;
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
/// M4 acceptance: the harness runs CI itself (grounding), the Principal reviews the
/// diff (reviewer ≠ author), and CI failure or a rejected review sends the task back
/// to the engineer — with a bounded revision loop and a convention write-back.
///
/// The CI step is injected so these run without a .NET toolchain; a separate test
/// exercises the real CiRunner.
/// </summary>
public class ReviewAndCiTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-m4-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;

    public ReviewAndCiTests()
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

    private TaskRecord ReadyTask() =>
        _tasks.Transition(
            _tasks.Insert(TaskRecord.Create(
                TaskType.Task, "Add greeting", "Create greeting.txt", 100_000,
                assignedRole: AgentRole.Engineer, createdBy: "principal")).Id,
            TaskStatus.Ready);

    private TaskRunner Runner(ILlmClient llm, Func<string, CiResult> ci) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, ModelPricing.Default),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger: null, ci: ci);

    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"master:{path}").Stdout;

    private static Func<string, CiResult> CiPass => _ => CiResult.Skip("stub: pass");
    private static Func<string, CiResult> CiFail =>
        _ => new CiResult(false, "build", "error CS1002: ; expected");

    private static string Engineer(string file, string content, string summary) =>
        ScriptedLlmClient.Tool("write_file", ("path", file), ("content", content)) + "\n" +
        ScriptedLlmClient.Tool("done", ("summary", summary));

    [Fact]
    public async Task An_approved_task_that_passes_ci_is_reviewed_and_merged()
    {
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            Engineer("greeting.txt", "hello", "Wrote greeting.txt."),
            ScriptedLlmClient.Tool("approve", ("note", "Correct.")));

        var outcome = await Runner(llm, CiPass).RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.Done, outcome.Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));

        // A review discussion records the verdict.
        var discussion = Assert.Single(new DiscussionRepository(_conn).ForTask(task.Id));
        Assert.Equal("principal", discussion.Author);
    }

    [Fact]
    public async Task Ci_failure_sends_the_task_back_without_the_principal_ever_reviewing()
    {
        var task = ReadyTask();
        // The engineer runs and says done; the reviewer would approve — but CI fails
        // first, so the approve turn is never consumed.
        var llm = new ScriptedLlmClient(
            Engineer("greeting.txt", "hello", "Wrote greeting.txt."),
            ScriptedLlmClient.Tool("approve", ("note", "should never be reached")));

        var outcome = await Runner(llm, CiFail).RunAsync(_tasks.Get(task.Id));

        // Back to the engineer, not merged; the CI output is in the progress note.
        Assert.Equal(TaskStatus.InProgress, outcome.Status);
        Assert.Contains("CHANGES REQUESTED (CI)", _tasks.Get(task.Id).ProgressNote);
        Assert.Contains("CS1002", _tasks.Get(task.Id).ProgressNote);
        Assert.Throws<GitException>(() => ShowFromTrunk("greeting.txt"));  // never reached trunk

        // The reviewer never ran — no Principal instance for this task.
        Assert.DoesNotContain(new AgentInstanceRepository(_conn).ForTask(task.Id),
            i => i.Role == AgentRole.Principal);
        // And the approve turn was not consumed (CI short-circuited before review).
        Assert.Single(llm.Requests);  // only the engineer's one turn ran
    }

    [Fact]
    public async Task A_rejected_review_sends_the_task_back_and_writes_the_convention_to_trunk()
    {
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            Engineer("Todo.cs", "class Todo { }", "Implemented Todo."),
            ScriptedLlmClient.Tool("request_changes",
                ("reason", "Todo.cs hardcodes the example ids instead of looking them up."),
                ("convention", "Never special-case acceptance-test inputs; solve the general case.")));

        var outcome = await Runner(llm, CiPass).RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.InProgress, outcome.Status);
        Assert.Contains("CHANGES REQUESTED (review)", _tasks.Get(task.Id).ProgressNote);
        Assert.Contains("hardcodes the example ids", _tasks.Get(task.Id).ProgressNote);

        // The self-improving loop: the convention is now on trunk for every future engineer.
        Assert.Contains("Never special-case acceptance-test inputs", ShowFromTrunk("CONVENTIONS.md"));
    }

    [Fact]
    public async Task An_integration_failure_parks_the_task_as_blocked_instead_of_stranding_it()
    {
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(Engineer("greeting.txt", "hello", "Wrote greeting.txt."));

        // A gate blowing up (git failure, review crash) must not leave the task in
        // in_review/merging, which the claim query never picks up.
        var outcome = await Runner(llm, _ => throw new InvalidOperationException("git exploded"))
            .RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.Blocked, outcome.Status);
        Assert.Contains("Integration failed", _tasks.Get(task.Id).ProgressNote);
        // Technical failures climb to the Principal (who can fix them), not the PM.
        Assert.Contains(new MessageRepository(_conn).Pending("principal"),
            m => m.Payload.Contains("Integration failed"));
        // The branch survived: unblocking and re-running can retry the gates.
        Assert.True(Directory.Exists(_paths.TaskWorkspace(Project, task.Id)));
    }

    [Fact]
    public async Task A_task_that_keeps_failing_is_blocked_after_the_revision_cap()
    {
        var task = ReadyTask();

        // Every attempt: engineer writes, says done, CI fails → back to the engineer.
        var llm = new ScriptedLlmClient
        {
            Fallback = Engineer("greeting.txt", "hello", "Trying again."),
        };
        var runner = Runner(llm, CiFail);

        // Drive attempts until the task blocks. The cap is 5 engineer attempts.
        TaskRunOutcome outcome = default!;
        for (var i = 0; i < 8; i++)
        {
            var next = runner.NextTask(AgentRole.Engineer);
            if (next is null) break;
            outcome = await runner.RunAsync(next);
            if (outcome.Status == TaskStatus.Blocked) break;
        }

        Assert.Equal(TaskStatus.Blocked, outcome.Status);
        Assert.Equal(5, new AgentInstanceRepository(_conn).ForTask(task.Id)
            .Count(i => i.Role == AgentRole.Engineer));
        Assert.Contains(new MessageRepository(_conn).Pending("principal"),
            m => m.Payload.Contains("blocked after 5 engineer attempts"));
    }
}

/// <summary>
/// The real CiRunner against an actual .NET project. Slower (invokes dotnet), so
/// kept to the essential build-pass and build-fail cases.
/// </summary>
[Trait("Category", "Integration")]
public class CiRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"forge-ci-{Guid.NewGuid():N}");

    public CiRunnerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void No_project_present_is_a_skip_not_a_failure()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# docs only");
        var result = CiRunner.Run(_dir);
        Assert.True(result.Passed);
        Assert.True(result.Skipped);
    }

    [Fact]
    public void A_project_that_does_not_compile_fails_ci()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "Program.cs"), "this is not valid C#");

        var result = CiRunner.Run(_dir);

        Assert.False(result.Passed);
        Assert.Equal("build", result.Step);
    }
}
