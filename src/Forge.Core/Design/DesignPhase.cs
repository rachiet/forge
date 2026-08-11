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

        var brief = (isChangeRequest ? ChangeRequestBrief() : Brief())
                  + ProgressSection() + PlanSection() + ThemeSection();

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
    /// The progress section of the brief: the client watches the build by requirement, so
    /// every task that serves one must name it in `requirements_ref`.
    /// </summary>
    private static string ProgressSection() => """


        ## What the client sees

        Progress is reported per requirement file, counted from the `requirements_ref` you
        give each task. Pass it on every `create_task` that serves a requirement, exactly as
        the file is named in `docs/requirements/`. Work that serves none — scaffolding, a
        chore — is reported separately, so leave its ref off rather than guessing at one.
        """;

    /// <summary>
    /// The plan section of the brief: how the tasks are grouped and named for the client, who
    /// reads the board and never sees a task title.
    /// </summary>
    private string PlanSection()
    {
        var existing = new MilestoneRepository(conn).Names();
        var reuse = existing.Count == 0 ? "" :
            $"\n\nPhases already in the plan — reuse a name EXACTLY to add to it: "
            + string.Join(", ", existing.Select(n => $"`{n}`")) + ".";

        return $"""


            ## How the plan reads to the client

            The client watches this build as a list of phases with the work under each. Every
            `create_task` therefore needs two things beyond the engineering detail:

            - `display_name` — the task in their words, a short sentence. `Adding, listing and
              deleting books`, never `implement-books-http-api`. They are not technical and
              they never see the title.
            - `milestone` — the phase it belongs to, also in their words: `Books API`,
              `Library interface`. Tasks in the same phase repeat the SAME name and appear as
              one group, ordered by when you first used it.

            Group the work into a handful of phases that each mean something to someone paying
            for it — three to six for a project this size, not one per task and not one for
            everything. `{MilestoneRepository.GettingStarted}` and `{MilestoneRepository.Testing}`
            are the harness's and are filled in for you; do not create tasks under them.{reuse}
            """;
    }

    /// <summary>
    /// The theme section of the brief: the themes the kit ships, each with what it is for, and
    /// the instruction to pick one with `choose_theme`. Empty when the kit ships none.
    /// </summary>
    private string ThemeSection()
    {
        var themes = Ui.UiKit.ThemeCatalogue(prompts);
        if (themes.Count == 0) return "";

        return $"""


            ## How the interface looks

            If this project has a user interface, call `choose_theme` before you create any
            tasks. Forge ships a component library — buttons, forms, cards, lists, tables,
            modals, tabs, menus, carousels, empty states, layout and motion — and installs it
            into the repo itself. Engineers build pages from its classes; nobody writes CSS,
            and no task should ask them to.

            The themes, each with what it is for:

            {string.Join("\n", themes.Select(t => $"- {t}"))}

            Pick for what is being built, not for taste. `mode`, `accent`, `density` and
            `radius` adjust it; pass only what the client actually asked for and leave the
            rest at the theme's own character.

            A project with no interface at all — a library, a CLI tool — skips this.
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

        You are decomposing a Feature, which is already on the board — that is why the
        ids you are given start above 1. The harness makes every task you create a child
        of it and marks it done once they are all done, so it is not part of your DAG:
        `add_dependency` edges run between the tasks you create and never point at the
        Feature.

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
