using Forge.Core.Llm;
using Forge.Core.Model;

namespace Forge.Core.Agents;

/// <summary>
/// What one role is: a model tier, a role prompt, the files it always carries, its tools and
/// binaries, its file-access scope, and its budget and turn caps. Roles are data, so adding one
/// is a record here and a prompt file.
/// </summary>
public sealed record AgentRecipe
{
    public required AgentRole Role { get; init; }

    /// <summary>How much model this role needs. The configured ILlmClient resolves it to an id.</summary>
    public required ModelTier Tier { get; init; }

    /// <summary>Which role prompt to load: prompts/roles/&lt;name&gt;.md.</summary>
    public required string RolePrompt { get; init; }

    /// <summary>Repo-relative files loaded into every turn's system prompt, if present.</summary>
    public IReadOnlyList<string> AlwaysInContext { get; init; } = [];

    /// <summary>The tools this role may call. Any other name is refused at dispatch.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>
    /// What this role may read and write inside its workspace. Required rather than defaulted,
    /// so an unrestricted scope is always an explicit choice.
    /// </summary>
    public required PathScope Scope { get; init; }

    /// <summary>Binaries run() and serve() may execute. Everything else is refused.</summary>
    public IReadOnlyList<string> ToolAllowlist { get; init; } = [];

    public int DefaultBudget { get; init; } = 60_000;

    /// <summary>How many turns one instance of this role may take.</summary>
    public int IterationCap { get; init; } = 40;

    public int MaxTokens { get; init; } = 8_192;

    /// <summary>Prefix for this role's agent_instances ids, e.g. `eng-20260718-093012`.</summary>
    public required string InstancePrefix { get; init; }

    // Every recipe below states the same fields in the same order, so two roles can
    // be diffed by eye. Where a value equals the default it is still written out —
    // "this role has no shell access" is worth reading, and an empty allowlist that
    // appears by omission cannot be told apart from one nobody considered.

    /// <summary>
    /// The engineer: implements one task on the coding tier. Its standing context is
    /// CONVENTIONS.md and the task packet, and it may read and write anywhere in its workspace.
    /// </summary>
    public static AgentRecipe Engineer => (new AgentRecipe
    {
        Role = AgentRole.Engineer,
        Tier = ModelTier.Coding,
        RolePrompt = "engineer",
        InstancePrefix = "eng",
        AlwaysInContext = ["CONVENTIONS.md"],
        Tools = ["read_file", "list_dir", "grep", "write_file", "run", "progress_note", "done", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = ["dotnet", "git"],
        DefaultBudget = 600_000,
        IterationCap = 40,
    }).Validate();

    /// <summary>
    /// The PM: the client's only contact, on the reasoning tier. Authors the requirements tree
    /// and STATUS.md, and is scoped away from code so it cannot make technical calls. No run().
    /// </summary>
    public static AgentRecipe Pm => (new AgentRecipe
    {
        Role = AgentRole.Pm,
        Tier = ModelTier.Reasoning,
        RolePrompt = "pm",
        InstancePrefix = "pm",
        AlwaysInContext = ["PROJECT.md", "STATUS.md", "docs/requirements/INDEX.md"],
        // No create_task: the PM hands work over only by proposing requirements the client
        // approves. The bug and task tools resolve what the client was asked about in chat.
        Tools = ["read_file", "list_dir", "grep", "write_file", "add_milestone", "propose_requirements",
                 "reject_bug", "retriage_bug", "retriage_task", "cancel_task", "reply", "escalate"],
        Scope = new PathScope(["PROJECT.md", "STATUS.md", "docs/"]),
        ToolAllowlist = [],
        DefaultBudget = 400_000,
        IterationCap = 20,
    }).Validate();

    /// <summary>
    /// The Principal designing: reads the requirements and writes the project structure, the
    /// project's CONVENTIONS.md section, the OpenAPI contract and the task DAG. Sees the whole
    /// workspace; has no run(), since designing is not executing.
    /// </summary>
    public static AgentRecipe Principal => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "principal",
        InstancePrefix = "prin",
        AlwaysInContext = ["PROJECT.md", "CONVENTIONS.md", "docs/requirements/INDEX.md"],
        Tools = ["read_file", "list_dir", "grep", "write_file", "create_task", "add_dependency", "done", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = [],
        DefaultBudget = 900_000,
        IterationCap = 60,
    }).Validate();

    /// <summary>
    /// The Principal reviewing: reads a diff that has already passed CI and decides whether it
    /// solves the problem. A fresh instance, so the reviewer is never the author. It may append
    /// to CONVENTIONS.md but not create tasks, and has no run().
    /// </summary>
    public static AgentRecipe PrincipalReview => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "principal-review",
        InstancePrefix = "rev",
        AlwaysInContext = ["PROJECT.md", "CONVENTIONS.md"],
        // reject_bug lets a bug-fix review close a bug whose reported defect is not real.
        Tools = ["read_file", "list_dir", "grep", "write_file", "approve", "request_changes", "reject_bug", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = [],
        DefaultBudget = 300_000,
        IterationCap = 30,
    }).Validate();

    /// <summary>
    /// The Principal triaging a stuck task: reads the work-in-progress and the engineer's note,
    /// then redirects, splits, or escalates. It has no write_file, run or done, so a triage
    /// cannot be mistaken for an implementation.
    /// </summary>
    public static AgentRecipe PrincipalTriage => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "principal",
        InstancePrefix = "triage",
        AlwaysInContext = ["PROJECT.md", "CONVENTIONS.md"],
        Tools = ["read_file", "list_dir", "grep", "create_task", "add_dependency", "redirect",
                 "break_and_relink", "accept_bug", "reject_bug", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = [],
        DefaultBudget = 300_000,
        IterationCap = 20,
    }).Validate();

    /// <summary>
    /// The last triage, after the engineer and the Principal have both failed on a task.
    /// <see cref="PrincipalTriage"/> without `redirect`, so the only ways out are splitting the
    /// task or handing the decision to the client.
    /// </summary>
    public static AgentRecipe PrincipalFinalTriage => (PrincipalTriage with
    {
        InstancePrefix = "final-triage",
        Tools = ["read_file", "list_dir", "grep", "create_task", "add_dependency",
                 "break_and_relink", "escalate"],
    }).Validate();

    /// <summary>
    /// QA: runs once the board is complete, and writes the black-box acceptance suite against
    /// the contract for the harness to run. Seeded with the bug ledger so it does not re-file
    /// what is already open or rejected.
    /// </summary>
    public static AgentRecipe Qa => (new AgentRecipe
    {
        Role = AgentRole.Qa,
        // Writing tests against a fixed contract is coding-tier work.
        Tier = ModelTier.Coding,
        RolePrompt = "qa",
        InstancePrefix = "qa",
        AlwaysInContext = ["PROJECT.md", "docs/requirements/INDEX.md"],
        // serve/stop_server/http are QA's alone; only its processes need managing.
        Tools = ["read_file", "list_dir", "grep", "write_file", "run", "serve", "stop_server", "http",
                 "file_bug", "how_to_run", "done", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = ["dotnet", "git"],
        DefaultBudget = 500_000,
        IterationCap = 40,
    }).Validate();

    /// <summary>
    /// The Principal implementing a task itself, after redirecting the engineer failed twice.
    /// The engineer's instructions on the reasoning tier, with a larger budget and turn cap,
    /// through the same CI, review and merge path.
    /// </summary>
    public static AgentRecipe PrincipalImplementer => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "engineer",
        InstancePrefix = "prin-impl",
        AlwaysInContext = ["CONVENTIONS.md"],
        Tools = ["read_file", "list_dir", "grep", "write_file", "run", "progress_note", "done", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = ["dotnet", "git"],
        DefaultBudget = 900_000,
        IterationCap = 80,
    }).Validate();

    public static AgentRecipe For(AgentRole role) => role switch
    {
        AgentRole.Engineer => Engineer,
        AgentRole.Pm => Pm,
        AgentRole.Principal => Principal,
        AgentRole.Qa => Qa,
        _ => throw new NotSupportedException(
            $"No recipe for {role}."),
    };

    /// <summary>
    /// Checks a recipe is coherent — a known role prompt, tools that exist, an allowlist that
    /// matches whether it can start a process — and returns it. Called by every factory and
    /// again by the agent loop, since `with` bypasses the factories.
    /// </summary>
    public AgentRecipe Validate()
    {
        var recipe = this;
        if (recipe.Tools.Count == 0)
            throw new ArgumentException($"{recipe.Role} has no tools and could never act.");

        if (recipe.Tools.FirstOrDefault(t => !AgentToolset.Catalogue.ContainsKey(t)) is { } unknown)
            throw new ArgumentException(
                $"{recipe.Role} lists tool '{unknown}', which the toolset does not implement.");

        // An allowlist and a process-starting tool only make sense together.
        var canRun = recipe.Tools.Contains("run") || recipe.Tools.Contains("serve");
        if (canRun && recipe.ToolAllowlist.Count == 0)
            throw new ArgumentException($"{recipe.Role} has run() but no binaries allowed.");
        if (!canRun && recipe.ToolAllowlist.Count > 0)
            throw new ArgumentException($"{recipe.Role} allows binaries but has no run() to use them.");

        if (recipe.DefaultBudget <= 0 || recipe.IterationCap <= 0 || recipe.MaxTokens <= 0)
            throw new ArgumentException($"{recipe.Role} has a non-positive budget, cap or max_tokens.");

        return recipe;
    }
}
