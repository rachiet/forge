using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Forge.Core.Secrets;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// A task too big for one agent used to burn the whole strike ladder and park on the client,
/// who could only be asked "what now?" about work they cannot size. The Principal can now
/// replace it with smaller tasks: `break_and_relink` rewires the DAG and cancels the original,
/// so everything that was waiting on it waits on the replacements instead.
/// </summary>
public class SplitTaskTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-split-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;

    public SplitTaskTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
        _tasks = new TaskRepository(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { /* the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    private TaskRunner Runner(ILlmClient llm) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, TestPrices.Catalog),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve());

    private TaskRecord Task(string title, long? parentId = null) =>
        _tasks.Insert(TaskRecord.Create(
            TaskType.Task, title, $"objective for {title}", 100_000,
            assignedRole: AgentRole.Engineer, parentId: parentId));

    /// <summary>A task the Principal owns, at the given strike count.</summary>
    private TaskRecord Stuck(int strikes)
    {
        var task = Task("Build the whole API");
        _tasks.Transition(task.Id, TaskStatus.Ready);
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        _tasks.Transition(task.Id, TaskStatus.OutOfBudget);
        for (var i = 0; i < strikes; i++) _tasks.IncrementOutOfBudgetCount(task.Id);
        return _tasks.Get(task.Id);
    }

    /// <summary>
    /// The Principal's turn: create N replacements, then split the stuck task into them.
    /// The ids must be predicted, because a scripted reply is fixed text — so this reads the
    /// highest id already on the board and counts on from there, rather than assuming the
    /// stuck task is id 1 (it is not, in any test that sets up a feature or a dependency).
    /// </summary>
    private ScriptedLlmClient SplitInto(int count)
    {
        var creates = Enumerable.Range(0, count).Select(i =>
            ScriptedLlmClient.Tool("create_task",
                ("title", $"Piece {i + 1}"),
                ("objective", $"Build piece {i + 1}"),
                ("acceptance", $"Piece {i + 1} works")));

        var ids = string.Join(",", Enumerable.Range(0, count).Select(i => NextTaskId + i));
        return new ScriptedLlmClient(
            string.Join("\n", creates),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", ids), ("reason", "three endpoints, not one")));
    }

    /// <summary>The id the next inserted task will get.</summary>
    private long NextTaskId => _tasks.List().Max(t => t.Id) + 1;

    [Fact]
    public async Task Split_replaces_the_task_and_moves_its_dependents_onto_the_replacements()
    {
        // task 1 is stuck; task 7 waits on it. After the split, 7 waits on 2, 3 and 4 instead
        // — the whole point, since a dependency is only ever satisfied by a `done` task.
        var stuck = Stuck(strikes: 1);
        var waiting = Task("Depends on the API");
        _tasks.Transition(waiting.Id, TaskStatus.Ready);
        _tasks.AddDependency(waiting.Id, stuck.Id);

        var outcome = await Runner(SplitInto(3)).RunNextByPriorityAsync();

        Assert.NotNull(outcome);
        Assert.Equal(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        var replacements = _tasks.List().Where(t => t.Id != stuck.Id && t.Id != waiting.Id).ToList();
        Assert.Equal(3, replacements.Count);

        Assert.Equal(replacements.Select(r => r.Id), _tasks.DependenciesOf(waiting.Id));
        Assert.Empty(_tasks.DependentsOf(stuck.Id));
        Assert.All(replacements, r => Assert.Equal(TaskStatus.Ready, r.Status));
    }

    [Fact]
    public async Task Replacements_inherit_the_original_dependencies_and_feature()
    {
        // Everything the stuck task waited on, the replacements must wait on too, or they
        // start before their groundwork. The Feature comes from the task being replaced —
        // NOT from the task itself, which is about to be cancelled.
        var feature = _tasks.Insert(TaskRecord.Create(
            TaskType.Feature, "The feature", "objective", 100_000));
        var upstream = Task("Scaffold first");
        _tasks.Transition(upstream.Id, TaskStatus.Ready);

        var stuck = Stuck(strikes: 1);
        _tasks.SetParent(stuck.Id, feature.Id);
        _tasks.AddDependency(stuck.Id, upstream.Id);

        await Runner(SplitInto(2)).RunNextByPriorityAsync();

        var replacements = _tasks.List()
            .Where(t => t.Title.StartsWith("Piece", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, replacements.Count);
        Assert.All(replacements, r =>
        {
            Assert.Equal([upstream.Id], _tasks.DependenciesOf(r.Id));
            Assert.Equal(feature.Id, r.ParentId);        // under the feature, not the cancelled task
            Assert.Equal(1, r.SplitDepth);
        });
    }

    [Fact]
    public async Task Split_is_refused_with_fewer_than_two_replacements()
    {
        // Splitting into one is a redirect with extra steps; into none it would cancel the
        // work outright. The guard also protects the sweep, which needs children to exist.
        var stuck = Stuck(strikes: 1);
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("create_task",
                ("title", "Only piece"), ("objective", "o"), ("acceptance", "a")),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", NextTaskId.ToString())),
            ScriptedLlmClient.Tool("escalate", ("reason", "cannot size this")));

        await Runner(llm).RunNextByPriorityAsync();

        Assert.NotEqual(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
    }

    [Fact]
    public async Task Split_is_refused_for_tasks_it_did_not_just_create()
    {
        // The evidence rule file_bug and how_to_run already follow: a verdict may only cite
        // work the harness watched happen, so a split cannot retire a task in favour of
        // unrelated ones already on the board.
        var stuck = Stuck(strikes: 1);
        var other = Task("Someone else's task");
        var another = Task("And another");

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", $"{other.Id},{another.Id}")),
            ScriptedLlmClient.Tool("escalate", ("reason", "cannot size this")));
        await Runner(llm).RunNextByPriorityAsync();

        Assert.NotEqual(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        Assert.Empty(_tasks.DependenciesOf(other.Id));
    }

    [Fact]
    public async Task Splitting_is_depth_capped_so_the_ladder_still_reaches_a_human()
    {
        // Without this, every replacement could be split again and no task would ever
        // reach the client — the property that makes the strike ladder terminate.
        var stuck = Stuck(strikes: 1);
        _tasks.SetSplitDepth(stuck.Id, 2);
        var ids = $"{NextTaskId},{NextTaskId + 1}";

        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("create_task", ("title", "A"), ("objective", "o"), ("acceptance", "a")),
            ScriptedLlmClient.Tool("create_task", ("title", "B"), ("objective", "o"), ("acceptance", "a")),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", ids)),
            ScriptedLlmClient.Tool("escalate", ("reason", "genuinely needs a client decision")));
        await Runner(llm).RunNextByPriorityAsync();

        Assert.NotEqual(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
    }

    // --- The relink itself: what the DAG looks like afterwards ---------------------

    [Fact]
    public async Task Every_dependent_is_moved_and_nothing_further_downstream_is_touched()
    {
        // Fan-out plus a transitive dependent. 8 waits on 7 which waits on the stuck task;
        // only 7 is re-pointed. Rewiring 8 as well would be wrong — it never named the
        // stuck task, and its own edge already carries the ordering it asked for.
        var stuck = Stuck(strikes: 1);
        var first = Task("First dependent");
        var second = Task("Second dependent");
        var transitive = Task("Waits on the first dependent");
        foreach (var t in new[] { first, second, transitive }) _tasks.Transition(t.Id, TaskStatus.Ready);
        _tasks.AddDependency(first.Id, stuck.Id);
        _tasks.AddDependency(second.Id, stuck.Id);
        _tasks.AddDependency(transitive.Id, first.Id);

        await Runner(SplitInto(2)).RunNextByPriorityAsync();

        var pieces = _tasks.List().Where(t => t.Title.StartsWith("Piece", StringComparison.Ordinal))
            .Select(t => t.Id).ToList();
        Assert.Equal(pieces, _tasks.DependenciesOf(first.Id));
        Assert.Equal(pieces, _tasks.DependenciesOf(second.Id));
        Assert.Equal([first.Id], _tasks.DependenciesOf(transitive.Id));   // untouched
    }

    [Fact]
    public async Task Ordering_the_principal_set_between_the_replacements_survives_the_relink()
    {
        // The harness deliberately does not infer whether the pieces are sequential — the
        // Principal says so with add_dependency, and the conservative rewiring must not
        // flatten that. Piece 2 waits on piece 1; both still inherit the upstream task.
        var upstream = Task("Scaffold first");
        _tasks.Transition(upstream.Id, TaskStatus.Ready);
        var stuck = Stuck(strikes: 1);
        _tasks.AddDependency(stuck.Id, upstream.Id);

        var firstPiece = NextTaskId;
        var llm = new ScriptedLlmClient(
            string.Join("\n",
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 1"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 2"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("add_dependency",
                    ("task", (firstPiece + 1).ToString()), ("depends_on", firstPiece.ToString()))),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", $"{firstPiece},{firstPiece + 1}")));

        await Runner(llm).RunNextByPriorityAsync();

        Assert.Equal([upstream.Id], _tasks.DependenciesOf(firstPiece));

        // The second piece keeps the ordering the Principal asked for AND inherits the
        // upstream task the split one waited on.
        var secondPieceDeps = _tasks.DependenciesOf(firstPiece + 1);
        Assert.Contains(firstPiece, secondPieceDeps);
        Assert.Contains(upstream.Id, secondPieceDeps);
        Assert.Equal(2, secondPieceDeps.Count);
    }

    [Fact]
    public async Task A_replacement_that_was_itself_a_dependent_is_not_wired_to_itself()
    {
        // Guarded because the rewiring loops over dependents × replacements: a task that is
        // both would otherwise be told to wait for itself, which AddDependency refuses and
        // which would abort the split half-done.
        var stuck = Stuck(strikes: 1);
        var piece = NextTaskId;
        var llm = new ScriptedLlmClient(
            string.Join("\n",
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 1"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 2"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("add_dependency",
                    ("task", piece.ToString()), ("depends_on", stuck.Id.ToString()))),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", $"{piece},{piece + 1}")));

        await Runner(llm).RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        Assert.DoesNotContain(piece, _tasks.DependenciesOf(piece));
        Assert.Empty(_tasks.DependentsOf(stuck.Id));
    }

    [Fact]
    public async Task Both_directions_of_the_old_edges_are_dropped()
    {
        // A replacement left waiting on the cancelled task would wait forever: only a `done`
        // task satisfies a dependency, and a cancelled one never reaches it.
        var upstream = Task("Scaffold first");
        var waiting = Task("Waits on the API");
        foreach (var t in new[] { upstream, waiting }) _tasks.Transition(t.Id, TaskStatus.Ready);
        var stuck = Stuck(strikes: 1);
        _tasks.AddDependency(stuck.Id, upstream.Id);
        _tasks.AddDependency(waiting.Id, stuck.Id);

        await Runner(SplitInto(2)).RunNextByPriorityAsync();

        Assert.Empty(_tasks.DependenciesOf(stuck.Id));
        Assert.Empty(_tasks.DependentsOf(stuck.Id));
        Assert.DoesNotContain(stuck.Id, _tasks.DependenciesOf(waiting.Id));
    }

    [Fact]
    public async Task A_relink_that_would_close_a_cycle_is_refused_and_changes_nothing()
    {
        // The atomicity claim: every check runs before the first write, so a refusal leaves
        // the graph exactly as it was. A half-rewired DAG is worse than the deadlock the
        // whole tool exists to prevent.
        var stuck = Stuck(strikes: 1);
        var waiting = Task("Waits on the API");
        _tasks.Transition(waiting.Id, TaskStatus.Ready);
        _tasks.AddDependency(waiting.Id, stuck.Id);

        // The Principal makes a replacement depend on the very task that waits on the split
        // task, so re-pointing that task at the replacement would close a loop.
        var piece = NextTaskId;
        var llm = new ScriptedLlmClient(
            string.Join("\n",
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 1"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("create_task", ("title", "Piece 2"), ("objective", "o"), ("acceptance", "a")),
                ScriptedLlmClient.Tool("add_dependency",
                    ("task", piece.ToString()), ("depends_on", waiting.Id.ToString()))),
            ScriptedLlmClient.Tool("break_and_relink", ("new_tasks", $"{piece},{piece + 1}")),
            ScriptedLlmClient.Tool("escalate", ("reason", "cannot split this cleanly")));

        await Runner(llm).RunNextByPriorityAsync();

        Assert.NotEqual(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        Assert.Equal([stuck.Id], _tasks.DependenciesOf(waiting.Id));      // still the original edge
        Assert.Empty(_tasks.DependenciesOf(piece + 1));                   // second piece never wired
    }

    [Fact]
    public async Task A_task_with_no_edges_splits_cleanly()
    {
        var stuck = Stuck(strikes: 1);

        await Runner(SplitInto(2)).RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        Assert.Contains("Split into", _tasks.Get(stuck.Id).ProgressNote!);
        Assert.All(_tasks.List().Where(t => t.Id != stuck.Id),
            t => Assert.Empty(_tasks.DependenciesOf(t.Id)));
    }

    [Fact]
    public void The_final_triage_recipe_cannot_redirect()
    {
        // The mechanical half of "don't just hope it splits": on the last strike the option
        // already proven not to work is not on the menu, enforced by the dispatch allowlist.
        Assert.DoesNotContain("redirect", AgentRecipe.PrincipalFinalTriage.Tools);
        Assert.Contains("break_and_relink", AgentRecipe.PrincipalFinalTriage.Tools);
        Assert.Contains("escalate", AgentRecipe.PrincipalFinalTriage.Tools);
        Assert.Contains("redirect", AgentRecipe.PrincipalTriage.Tools);
    }

    [Fact]
    public async Task Past_the_last_strike_the_task_gets_a_final_triage_instead_of_giving_up()
    {
        // Previously strike 3 went straight to GiveUp → needs_human. The Principal having
        // just failed to implement it directly is the strongest signal it is too big, which
        // is exactly when the harness used to stop asking.
        var stuck = Stuck(strikes: 3);

        await Runner(SplitInto(2)).RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.Cancelled, _tasks.Get(stuck.Id).Status);
        Assert.Equal(2, _tasks.List().Count(t => t.SplitDepth == 1));
    }

    [Fact]
    public async Task A_final_triage_that_resolves_nothing_still_parks_on_the_client()
    {
        // GiveUp is not replaced, only deferred: it is now "triage produced no verdict"
        // rather than "out of budget three times".
        var stuck = Stuck(strikes: 3);
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("read_file", ("path", "PROJECT.md")));

        await Runner(llm).RunNextByPriorityAsync();

        Assert.Equal(TaskStatus.NeedsHuman, _tasks.Get(stuck.Id).Status);
    }
}
