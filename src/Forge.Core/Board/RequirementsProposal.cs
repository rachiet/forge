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
    string? RequirementsRef = null,
    int? Budget = null,
    long? MilestoneId = null)
{
    private const string MetaKey = "requirements_proposal";

    public static RequirementsProposal? Load(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Get(MetaKey) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<RequirementsProposal>(json)
            : null;

    public void Save(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Set(MetaKey, JsonSerializer.Serialize(this));

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
            Budget ?? 60_000,
            acceptanceCriteria: Acceptance,
            requirementsRef: RequirementsRef is { Length: > 0 } r ? Model.RequirementsRef.Parse(r) : null,
            milestoneId: MilestoneId,
            assignedRole: AgentRole.Principal,
            createdBy: "pm") with { Status = TaskStatus.Triage });

        Clear(conn);
        return created;
    }
}
