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

/// <summary>What a design run produced.</summary>
/// <param name="End">Why the Principal's instance stopped.</param>
/// <param name="TasksCreated">How many tasks it added to the board.</param>
/// <param name="Coverage">Which requirements map to a task.</param>
/// <param name="Contract">Gaps in the requirement-operation-task links.</param>
/// <param name="Summary">The Principal's plain-language account of the design.</param>
/// <param name="ProjectBudgetExhausted">
/// The project's dollar cap refused the run; the build pauses rather than treating the design
/// as failed.
/// </param>
public sealed record DesignOutcome(
    EndReason End,
    int TasksCreated,
    CoverageReport Coverage,
    ContractReport Contract,
    string Summary,
    bool ProjectBudgetExhausted = false);

/// <summary>
/// Runs the Principal over the requirements to produce the project structure, its
/// CONVENTIONS.md section, the OpenAPI contract and the task DAG, then checks the coverage
/// gates. The same agent loop as everywhere else, seeded with a design brief and run on a
/// long-lived clone of trunk, which it commits to. A project that already has finished work
/// gets the change-request brief instead, and plans only the delta.
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

    /// <summary>The long-lived clone of trunk the Principal designs in.</summary>
    public string WorkspacePath => paths.RoleWorkspace(project, "principal");

    /// <summary>
    /// Runs one design phase and returns what it produced, having committed the design to trunk
    /// and checked the coverage gates.
    /// </summary>
    public async Task<DesignOutcome> RunAsync(CancellationToken ct = default)
    {
        var existing = _tasks.List();
        var before = existing.Count;

        // Finished work means the project exists, so this run is a change request.
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

        // The design docs go straight to trunk; the caller releases the tasks it created.
        var committed = new WorkspaceManager(paths, project).CommitAndPushTrunk(WorkspacePath,
            isChangeRequest ? "design: change-request impact analysis and delta tasks"
                            : "design: structure, conventions, contracts, task plan");
        if (committed) _log.Event(EventType.GitCommit, "committed design to trunk");

        var created = _tasks.List().Count - before;

        // Read from the freshly committed workspace, so the gates see what this run wrote.
        var coverage = CoverageGate.Check(conn, workspace);
        _log.Message(coverage.Complete
            ? $"Coverage gate: all requirements mapped to tasks ({created} tasks created)"
            : $"Coverage gate: {coverage.Uncovered.Count} requirement(s) with no task: "
              + string.Join(", ", coverage.Uncovered));

        var contract = CoverageGate.CheckContract(conn, workspace);
        _log.Message(contract.Complete
            ? "Contract gate: requirements, operations and tasks all linked"
            : $"Contract gate: {contract.Describe()}");

        // The Principal has no reply tool, so its summary arrives as the progress note.
        var summary = result.Reply ?? result.ProgressNote
            ?? $"Design ended ({SnakeCaseEnum.ToSnakeCase(result.End)}) after {result.Iterations} turns.";
        return new DesignOutcome(
            result.End, created, coverage, contract, summary, result.ProjectBudgetExhausted);
    }

    /// <summary>
    /// The milestone section of the brief: the PM's plan with each milestone's id, which is
    /// what `create_task(milestone: N)` takes. Empty when there are no milestones.
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

    /// <summary>The brief for a new project: build the structure, the contract and the DAG.</summary>
    private static string Brief() => $"""
        # Design brief

        The requirements for this project are in `docs/requirements/`. Read the
        INDEX and every section first, then design the system.

        Produce, on disk and on the board:
        - The project's `CONVENTIONS.md` section for what only this build needs.
        - The folder tree with a `MODULE.md` per module.
        - `{ApiContract.Path}` — the HTTP contract, as OpenAPI 3. Every operation
          carries an `operationId` (kebab-case, e.g. `shorten-create`), an
          `{ApiContract.RequirementExtension}` naming the requirement file it serves, and
          its error responses as well as its success one. The harness parses this
          document and refuses one it cannot use, so write it before the tasks.
          A requirement with NO endpoint at all — a user interface, a performance
          target, a refactoring — goes in the document-level
          `{ApiContract.NonHttpExtension}` list instead. Never invent an operation to
          cover one; describe it under `docs/design/03-contracts/` in prose.
        - Acceptance criteria per feature.
        - A task DAG: one `create_task` per unit of work, each naming the
          requirement it implements and passing `contract_ops` for the operations it
          builds, with `add_dependency` edges for ordering.

        Two things are checked mechanically before any engineer starts, and design is
        handed back if either fails: every requirement file must be named by at least
        one operation, and every operation must be claimed by at least one task. Call
        `done` with a plain-language summary when the plan is complete.
        """;

    /// <summary>
    /// The brief for a project that already exists: read the code, write an impact note, update
    /// the contract, and create only the delta tasks — or escalate if the change is ill-advised.
    /// </summary>
    private static string ChangeRequestBrief() => $"""
        # Change request — impact analysis

        This project already exists: its structure, contracts, code, `CONVENTIONS.md`
        and `MODULE.md` files are on disk. Read them. The client has requested a change,
        and the PM has updated the affected requirement(s) in `docs/requirements/` —
        compare the updated requirement to what the code currently does.

        This is impact analysis, NOT a fresh design. Do exactly this:
        - Work out what the change affects: which modules, which contracts, and which
          existing features could regress if a shared contract changes. Write a short
          impact note to `docs/design/impact/` naming the affected pieces and the risk.
        - Update `{ApiContract.Path}` for the change: add, alter or remove operations so
          the document describes the system as it will be. It is what QA's acceptance
          suite is checked against, so an endpoint missing from it will not be verified.
          If the change adds a requirement with no endpoint at all, list that file in the
          document-level `{ApiContract.NonHttpExtension}` rather than inventing an
          operation for it.
        - Plan ONLY the delta: one `create_task` per new or modified unit of work, each
          naming the requirement it serves and the `contract_ops` it touches, with a
          budget and `add_dependency` edges. Do NOT recreate tasks for work that is
          already done — reuse the existing code.
        - If the change is ill-advised, contradicts an existing requirement, or is far
          larger than it appears, do NOT create tasks — `escalate(reason)` with your
          concern so it goes back to the client to decide.

        Call `done` with a plain-language summary: what is affected, the tasks you
        created, and any regression risk. The new tasks reach engineers as soon as
        you finish.
        """;
}
