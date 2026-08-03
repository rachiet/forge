using Forge.Core.Llm;
using Forge.Core.Model;

namespace Forge.Core.Agents;

/// <summary>
/// Personas are data, not classes (spec §11). An "agent" is a model choice, a
/// system prompt, context-assembly rules, a tool allowlist and a file-access
/// scope — nothing more. Adding a role is adding a record and a prompt file.
/// </summary>
public sealed record AgentRecipe
{
    public required AgentRole Role { get; init; }

    /// <summary>
    /// How much model this role needs. Not a model id: the configured ILlmClient
    /// resolves the tier, so a recipe never names a provider's model and swapping
    /// providers does not touch this file.
    /// </summary>
    public required ModelTier Tier { get; init; }

    /// <summary>Layer A: prompts/roles/&lt;name&gt;.md, versioned in git.</summary>
    public required string RolePrompt { get; init; }

    /// <summary>Repo-relative files loaded into every turn's system prompt, if present.</summary>
    public IReadOnlyList<string> AlwaysInContext { get; init; } = [];

    /// <summary>Which tools exist for this role. Anything else is refused by the toolset.</summary>
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>
    /// What this role may read and write inside its workspace. Required, not
    /// defaulted: an unrestricted scope is a real decision, and defaulting to it
    /// would hand whole-workspace access to any role someone forgets to think about.
    /// </summary>
    public required PathScope Scope { get; init; }

    /// <summary>Binaries the run() tool may execute. Everything else is refused.</summary>
    public IReadOnlyList<string> ToolAllowlist { get; init; } = [];

    public int DefaultBudget { get; init; } = 60_000;

    /// <summary>Spec §4.3: max loop turns per instance, v1 default 40.</summary>
    public int IterationCap { get; init; } = 40;

    public int MaxTokens { get; init; } = 8_192;

    /// <summary>Prefix for agent_instances ids ('eng-20260718-093012').</summary>
    public required string InstancePrefix { get; init; }

    // Every recipe below states the same fields in the same order, so two roles can
    // be diffed by eye. Where a value equals the default it is still written out —
    // "this role has no shell access" is worth reading, and an empty allowlist that
    // appears by omission cannot be told apart from one nobody considered.

    /// <summary>
    /// Cheap/fast coding tier per spec §3. CONVENTIONS.md plus the task packet is
    /// the whole standing context — engineers never see the requirements doc.
    /// Unrestricted inside the workspace: an engineer's job is the code.
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
        DefaultBudget = 60_000,
        IterationCap = 40,
    }).Validate();

    /// <summary>
    /// High-reasoning tier per spec §3. The client's only contact. Owns requirement
    /// fidelity, so it authors the requirements tree and STATUS.md — and is scoped
    /// away from code, because a PM that reads src/ starts making technical calls
    /// that belong to the Principal. No run(): it has nothing to execute.
    /// </summary>
    public static AgentRecipe Pm => (new AgentRecipe
    {
        Role = AgentRole.Pm,
        Tier = ModelTier.Reasoning,
        RolePrompt = "pm",
        InstancePrefix = "pm",
        AlwaysInContext = ["PROJECT.md", "STATUS.md", "docs/requirements/INDEX.md"],
        // propose_requirements presents the finished requirements for the client to approve;
        // approving is what opens the Feature, so the PM hands nothing to engineering on its
        // own and never creates tasks itself. reject_bug / retriage_bug let the PM close out a
        // bug the client reviewed in chat — reject it, or send it back to the Principal with
        // the client's guidance. resolve_task / cancel_task do the same for a stuck task the
        // Principal could not resolve: the client's answer either redirects it or drops it.
        Tools = ["read_file", "list_dir", "grep", "write_file", "add_milestone", "propose_requirements",
                 "reject_bug", "retriage_bug", "resolve_task", "cancel_task", "reply", "escalate"],
        Scope = new PathScope(["PROJECT.md", "STATUS.md", "docs/"]),
        ToolAllowlist = [],
        DefaultBudget = 120_000,
        IterationCap = 20,
    }).Validate();

    /// <summary>
    /// Highest-reasoning tier per spec §3 — the strongest model authors the
    /// structure, the system's highest-leverage artifact. Reads the requirements,
    /// writes CONVENTIONS.md / the tree / contracts / acceptance criteria, and
    /// breaks the work into a task DAG. Sees the whole workspace (unlike the PM):
    /// the Principal owns technical decisions and lays out the code. No run() —
    /// designing is not executing; the engineers it creates tasks for do that.
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
        DefaultBudget = 200_000,
        IterationCap = 60,
    }).Validate();

    /// <summary>
    /// The Principal wearing its review hat (spec §12, M4). Same role and model as
    /// the design recipe, different job: read a diff that already passed CI and
    /// decide if it solves the problem or just the examples. Reviewer ≠ author —
    /// it did not write this code. It may write CONVENTIONS.md (the self-improving
    /// write-back) but not create tasks; no run() (CI already built and tested).
    /// </summary>
    public static AgentRecipe PrincipalReview => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "principal-review",
        InstancePrefix = "rev",
        AlwaysInContext = ["PROJECT.md", "CONVENTIONS.md"],
        // reject_bug is here so a bug-fix review can close an invalid bug (the reported
        // defect isn't real) instead of looping request_changes; it no-ops on non-bug tasks.
        Tools = ["read_file", "list_dir", "grep", "write_file", "approve", "request_changes", "reject_bug", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = [],
        DefaultBudget = 80_000,
        IterationCap = 30,
    }).Validate();

    /// <summary>
    /// The Principal triaging a stuck task (blocked/out-of-budget). It reads the WIP
    /// and the engineer's note to diagnose, then decides: break the task down
    /// (create_task + add_dependency), redirect it to the engineer with direction, or
    /// escalate a requirements question to the PM. It does not write code — no
    /// write_file, no run, no done — so a triage cannot be mistaken for an implementation.
    /// The situational how-to is injected as a JIT packet, not baked into the role prompt.
    /// </summary>
    public static AgentRecipe PrincipalTriage => (new AgentRecipe
    {
        Role = AgentRole.Principal,
        Tier = ModelTier.Reasoning,
        RolePrompt = "principal",
        InstancePrefix = "triage",
        AlwaysInContext = ["PROJECT.md", "CONVENTIONS.md"],
        Tools = ["read_file", "list_dir", "grep", "create_task", "add_dependency", "redirect",
                 "accept_bug", "reject_bug", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = [],
        DefaultBudget = 80_000,
        IterationCap = 20,
    }).Validate();

    /// <summary>
    /// Black-box QA (spec §12, M5). Runs only once the whole board is complete: reads
    /// the requirements and the Principal's contracts, exercises the project through
    /// its observable side-channel (HTTP/CLI) — not the engineer's own unit tests —
    /// and files a bug for each failure. It is seeded with the bug ledger so it does
    /// not re-file what has already been filed or rejected.
    /// </summary>
    public static AgentRecipe Qa => (new AgentRecipe
    {
        Role = AgentRole.Qa,
        // Verifying behaviour against a fixed contract is not high-reasoning work — the
        // cheaper coding tier is plenty, and QA runs a full pass every cycle.
        Tier = ModelTier.Coding,
        RolePrompt = "qa",
        InstancePrefix = "qa",
        AlwaysInContext = ["PROJECT.md", "docs/requirements/INDEX.md"],
        Tools = ["read_file", "list_dir", "grep", "write_file", "run", "file_bug", "done", "escalate"],
        Scope = PathScope.Workspace,
        ToolAllowlist = ["dotnet", "git"],
        DefaultBudget = 120_000,
        IterationCap = 40,
    }).Validate();

    /// <summary>
    /// The Principal implementing a task directly (the second out-of-budget strike):
    /// redirecting twice did not land it, so the strongest model does the work itself
    /// with real headroom — the engineer's implementation how-to, opus's reasoning, and
    /// a larger budget and turn cap. It flows through the same CI + review + merge path
    /// as an engineer, so the result is still verified against ground truth.
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
        DefaultBudget = 120_000,
        IterationCap = 80,
    }).Validate();

    public static AgentRecipe For(AgentRole role) => role switch
    {
        AgentRole.Engineer => Engineer,
        AgentRole.Pm => Pm,
        AgentRole.Principal => Principal,
        AgentRole.Qa => Qa,
        _ => throw new NotSupportedException(
            $"No recipe for {role} yet — roles are introduced one milestone at a time (spec §12)."),
    };

    /// <summary>
    /// Recipes are data, so their mistakes are typos rather than compile errors.
    /// Called by the factories below and again by the agent loop, because `with`
    /// produces a new recipe without going near a factory — validating at the
    /// point of use is what makes the check unavoidable.
    /// </summary>
    public AgentRecipe Validate()
    {
        var recipe = this;
        if (recipe.Tools.Count == 0)
            throw new ArgumentException($"{recipe.Role} has no tools and could never act.");

        if (recipe.Tools.FirstOrDefault(t => !AgentToolset.Catalogue.ContainsKey(t)) is { } unknown)
            throw new ArgumentException(
                $"{recipe.Role} lists tool '{unknown}', which the toolset does not implement.");

        // The allowlist only means something alongside run(); an allowlist without
        // it, or run() without one, is a half-made decision.
        var canRun = recipe.Tools.Contains("run");
        if (canRun && recipe.ToolAllowlist.Count == 0)
            throw new ArgumentException($"{recipe.Role} has run() but no binaries allowed.");
        if (!canRun && recipe.ToolAllowlist.Count > 0)
            throw new ArgumentException($"{recipe.Role} allows binaries but has no run() to use them.");

        if (recipe.DefaultBudget <= 0 || recipe.IterationCap <= 0 || recipe.MaxTokens <= 0)
            throw new ArgumentException($"{recipe.Role} has a non-positive budget, cap or max_tokens.");

        return recipe;
    }
}
