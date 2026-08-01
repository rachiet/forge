using System.Data;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Design;

public sealed record DesignOutcome(
    EndReason End,
    int TasksCreated,
    CoverageReport Coverage,
    string Summary,
    // The project's dollar cap refused the run — pause the build, don't treat the
    // design (or a Feature decomposition reusing it) as having failed.
    bool ProjectBudgetExhausted = false);

/// <summary>
/// The design phase (spec §7, M3): the Principal reads the requirements and
/// authors the structure — tree, CONVENTIONS.md, contracts, acceptance criteria —
/// and breaks the work into a task DAG.
///
/// It runs the same agent loop as everyone else, seeded with a design brief
/// instead of a task packet or a chat, on a long-lived clone of trunk (like the
/// PM's doc work). The PM coverage gate is checked here mechanically. The
/// client's approval is the requirements the PM captured in chat: once the PM
/// opens a Feature with `create_feature`, the harness runs this phase and
/// releases the tasks it creates to `ready` on its own
/// (`TaskRunner.DecomposeFeatureAsync`).
/// </summary>
public sealed class DesignPhase(
    ForgePaths paths,
    string project,
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null)
{
    private readonly AgentRecipe _recipe = AgentRecipe.Principal;
    private readonly TaskRepository _tasks = new(conn);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    public string WorkspacePath => paths.RoleWorkspace(project, "principal");

    public async Task<DesignOutcome> RunAsync(CancellationToken ct = default)
    {
        var existing = _tasks.List();
        var before = existing.Count;

        // A project with completed work already exists — this run is a change request,
        // so the Principal does impact analysis against the existing code and plans only
        // the delta, rather than designing the whole system from a blank slate.
        var isChangeRequest = existing.Any(t => t.Status == TaskStatus.Done);
        _log.Message(isChangeRequest
            ? "Design phase: Principal starting a change-request impact analysis"
            : "Design phase: Principal starting");

        var workspace = new WorkspaceManager(paths, project).PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var brief = (isChangeRequest ? ChangeRequestBrief() : Brief()) + MilestoneSection();

        var result = await loop
            .RunChatAsync([new LlmMessage("user", brief)], executor, ct)
            .ConfigureAwait(false);

        // The design docs are the Principal's artifacts; they go straight to trunk,
        // the same way the PM's requirements do. Nothing further waits on a human:
        // the tasks this run creates are released to `ready` by the caller
        // (`TaskRunner.DecomposeFeatureAsync`) as soon as this method returns.
        var committed = new WorkspaceManager(paths, project).CommitAndPushTrunk(WorkspacePath,
            isChangeRequest ? "design: change-request impact analysis and delta tasks"
                            : "design: structure, conventions, contracts, task plan");
        if (committed) _log.Event(EventType.GitCommit, "committed design to trunk");

        var created = _tasks.List().Count - before;

        // PM coverage gate: read from the freshly-committed workspace so it reflects
        // exactly what the Principal wrote this run.
        var coverage = CoverageGate.Check(conn, workspace);
        _log.Message(coverage.Complete
            ? $"Coverage gate: all requirements mapped to tasks ({created} tasks created)"
            : $"Coverage gate: {coverage.Uncovered.Count} requirement(s) with no task: "
              + string.Join(", ", coverage.Uncovered));

        // The Principal ends with done(summary), not reply — its recipe has no reply
        // tool — so the plain-language summary for the PM and client rides the
        // progress note. Reply is kept first for any future chat-shaped design turn.
        var summary = result.Reply ?? result.ProgressNote
            ?? $"Design ended ({SnakeCaseEnum.ToSnakeCase(result.End)}) after {result.Iterations} turns.";
        return new DesignOutcome(result.End, created, coverage, summary, result.ProjectBudgetExhausted);
    }

    /// <summary>
    /// The PM's milestone plan, appended to whichever brief is running.
    ///
    /// Without this the Principal cannot attach a task to a milestone even though
    /// `create_task` has always accepted one: the ids live in a table it never sees,
    /// so every task was created with a null milestone and the client's progress view
    /// had nothing to group by. Ids are listed explicitly because that is what
    /// `create_task(milestone: N)` takes.
    /// </summary>
    private string MilestoneSection()
    {
        var milestones = new MilestoneRepository(conn).List();
        if (milestones.Count == 0) return "";

        var lines = string.Join("\n", milestones.Select(m => $"- id {m.Id}: {m.Name}"));
        return $"""


            ## Milestones

            The PM has agreed this milestone plan with the client:

            {lines}

            Pass `milestone: <id>` on every `create_task` so each unit of work belongs to
            the milestone it advances. The client watches progress and cost per milestone,
            so a task with no milestone is invisible to them. If a task genuinely serves no
            milestone (pure scaffolding, say), leave it off rather than guessing.
            """;
    }

    private static string Brief() => """
        # Design brief

        The requirements for this project are in `docs/requirements/`. Read the
        INDEX and every section first, then design the system.

        Produce, on disk and on the board:
        - `CONVENTIONS.md` — the rules engineers follow (under a page).
        - The folder tree with a `MODULE.md` per module.
        - External contracts under `docs/design/03-contracts/` — the observable
          boundary QA will test against.
        - Acceptance criteria per feature.
        - A task DAG: one `create_task` per unit of work, each naming the
          requirement it implements, with `add_dependency` edges for ordering.

        Every requirement section must map to at least one task. Call `done` with a
        plain-language summary of the design when the plan is complete; it goes
        through a mechanical coverage check and then straight to the board —
        engineers start on it as soon as you finish.
        """;

    /// <summary>
    /// The change-request brief: the project already exists, so this is impact analysis,
    /// not a fresh design. The Principal reads the existing code and plans only the delta —
    /// or pushes back if the change is ill-advised.
    /// </summary>
    private static string ChangeRequestBrief() => """
        # Change request — impact analysis

        This project already exists: its structure, contracts, code, `CONVENTIONS.md`
        and `MODULE.md` files are on disk. Read them. The client has requested a change,
        and the PM has updated the affected requirement(s) in `docs/requirements/` —
        compare the updated requirement to what the code currently does.

        This is impact analysis, NOT a fresh design. Do exactly this:
        - Work out what the change affects: which modules, which contracts, and which
          existing features could regress if a shared contract changes. Write a short
          impact note to `docs/design/impact/` naming the affected pieces and the risk.
        - Plan ONLY the delta: one `create_task` per new or modified unit of work, each
          naming the requirement it serves, with a budget and `add_dependency` edges.
          Do NOT recreate tasks for work that is already done — reuse the existing code.
        - If the change is ill-advised, contradicts an existing requirement, or is far
          larger than it appears, do NOT create tasks — `escalate(reason)` with your
          concern so it goes back to the client to decide.

        Call `done` with a plain-language summary: what is affected, the tasks you
        created, and any regression risk. The new tasks reach engineers as soon as
        you finish.
        """;
}
