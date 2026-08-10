using System.Data;
using System.Text.Json;
using Forge.Core.Db;
using Forge.Core.Model;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Board;

/// <summary>
/// A Feature the PM has drafted and the client has not yet approved. It holds the
/// arguments a Feature would be opened with, staged in project_meta until the
/// client approves; approving turns it into the Feature row, declining discards it.
/// </summary>
public sealed record RequirementsProposal(
    string Title,
    string Objective,
    string? Acceptance = null,
    string? RequirementsRef = null)
{
    private const string MetaKey = "requirements_proposal";

    /// <summary>
    /// SCHEMA SUPPORT ONLY — this number is never inferred as a budget and nothing reads it.
    /// `tasks.token_budget` is NOT NULL CHECK(> 0) and a Feature shares that table, so a row
    /// cannot be inserted without one; a Feature is only ever decomposed, and the design
    /// phase that decomposes it runs project-scoped with no task attached, so
    /// <see cref="Llm.MeteredLlmClient"/> never enforces against it. Do not read it as a
    /// cost estimate or a cap. The caps that bite are the Principal's per-task budgets in
    /// create_task, and the project's USD budget, fixed when the project was created.
    /// </summary>
    private const int FeatureBudget = 60_000;

    public static RequirementsProposal? Load(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Get(MetaKey) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<RequirementsProposal>(json)
            : null;

    /// <summary>Stages this proposal, replacing any already waiting.</summary>
    public void Save(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Set(MetaKey, JsonSerializer.Serialize(this));

    /// <summary>Discards the staged proposal.</summary>
    public static void Clear(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Set(MetaKey, "");

    /// <summary>
    /// Opens the Feature this proposal describes and discards the proposal. Born in
    /// Triage and owned by the Principal — the Principal's queue, where
    /// <c>TaskRunner.DecomposeFeatureAsync</c> picks it up and breaks it into tasks.
    /// </summary>
    public TaskRecord Approve(IDbConnection conn)
    {
        var created = new TaskRepository(conn).Insert(TaskRecord.Create(
            TaskType.Feature,
            Title,
            Objective,
            FeatureBudget,
            acceptanceCriteria: Acceptance,
            requirementsRef: RequirementsRef is { Length: > 0 } r ? Model.RequirementsRef.Parse(r) : null,
            assignedRole: AgentRole.Principal,
            createdBy: "pm") with { Status = TaskStatus.Triage });

        Clear(conn);
        return created;
    }
}
