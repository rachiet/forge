namespace Forge.Core.Model;

/// <summary>
/// One row of the tasks table. New tasks are built with <see cref="Create"/>, which enforces
/// the invariants, and their status changes only through <see cref="TaskTransitions"/>.
/// </summary>
public sealed record TaskRecord
{
    public long Id { get; init; }
    /// <summary>
    /// The id of the Feature this task belongs to, or null on a Feature itself and on any task
    /// created outside one. A Feature is complete when every task carrying its id is finished.
    /// </summary>
    public long? ParentId { get; init; }
    public required TaskType Type { get; init; }
    public required string Title { get; init; }
    /// <summary>
    /// This task as the client reads it on the progress board — a sentence, not an identifier.
    /// Null only on rows created before the board grouped work by milestone.
    /// </summary>
    public string? DisplayName { get; init; }
    /// <summary>The phase of the plan this task belongs to. Null on a Feature.</summary>
    public long? MilestoneId { get; init; }
    public required string Objective { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public IReadOnlyList<string> ContextPaths { get; init; } = [];
    /// <summary>OperationIds from the OpenAPI contract this task implements; empty for non-HTTP work.</summary>
    public IReadOnlyList<string> ContractOps { get; init; } = [];
    public RequirementsRef? RequirementsRef { get; init; }
    public AgentRole? AssignedRole { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Created;
    public required int TokenBudget { get; init; }
    public int TokensSpent { get; init; }
    /// <summary>How many times this task has exhausted its budget or turns.</summary>
    public int StallCount { get; init; }
    /// <summary>
    /// How many splits deep this task is: 0 if the Principal planned it, 1 if it replaced a
    /// task too big to land. `break_and_relink` refuses to split past its cap.
    /// </summary>
    public int SplitDepth { get; init; }
    public string? ProgressNote { get; init; }
    public string? BranchName { get; init; }
    public string? CreatedBy { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }

    public static TaskRecord Create(
        TaskType type,
        string title,
        string objective,
        int tokenBudget,
        string? displayName = null,
        long? milestoneId = null,
        string? acceptanceCriteria = null,
        IReadOnlyList<string>? contextPaths = null,
        RequirementsRef? requirementsRef = null,
        AgentRole? assignedRole = null,
        string? createdBy = null,
        long? parentId = null,
        int splitDepth = 0,
        IReadOnlyList<string>? contractOps = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Task title must be non-empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Task objective must be non-empty.", nameof(objective));
        if (tokenBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget,
                "Token budget must be positive.");

        return new TaskRecord
        {
            Type = type,
            Title = title,
            DisplayName = displayName,
            MilestoneId = milestoneId,
            Objective = objective,
            TokenBudget = tokenBudget,
            ParentId = parentId,
            AcceptanceCriteria = acceptanceCriteria,
            ContextPaths = contextPaths ?? [],
            ContractOps = contractOps ?? [],
            RequirementsRef = requirementsRef,
            AssignedRole = assignedRole,
            CreatedBy = createdBy,
            SplitDepth = splitDepth,
        };
    }
}

/// <summary>Thrown when a status change is not in the legal-transition map.</summary>
public sealed class IllegalTaskTransitionException(TaskStatus from, TaskStatus to)
    : InvalidOperationException($"Illegal task transition: {from} → {to}.")
{
    public TaskStatus From { get; } = from;
    public TaskStatus To { get; } = to;
}

/// <summary>
/// The legal task status transitions, and which role owns each status. Ownership is derived
/// from status here rather than stored, so there is no second copy to disagree.
/// </summary>
public static class TaskTransitions
{
    private static readonly IReadOnlyDictionary<TaskStatus, TaskStatus[]> Legal =
        new Dictionary<TaskStatus, TaskStatus[]>
        {
            [TaskStatus.Created] = [TaskStatus.Ready, TaskStatus.Cancelled],
            // Stalled is reachable from every live status, deliberately: it is the Principal's
            // queue rather than a step in the pipeline, and a task that cannot get there is a
            // task the loop will claim again and again with nothing changed.
            [TaskStatus.Ready] =
                [TaskStatus.Claimed, TaskStatus.Stalled, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.Claimed] =
                [TaskStatus.InProgress, TaskStatus.Ready, TaskStatus.Stalled,
                 TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.InProgress] =
                [TaskStatus.InReview, TaskStatus.Stalled, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            // A reviewer of a bug-fix can reject the underlying bug from InReview.
            [TaskStatus.InReview] =
                [TaskStatus.Merging, TaskStatus.InProgress, TaskStatus.Stalled,
                 TaskStatus.Rejected, TaskStatus.Cancelled],
            [TaskStatus.Merging] = [TaskStatus.Qa, TaskStatus.InProgress, TaskStatus.Stalled],
            [TaskStatus.Qa] = [TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Stalled],
            // The Principal's queue: it redirects the task to the engineer, takes it over, or
            // gives up. Triage and Rejected are reachable so the PM can act on a bug the
            // client reviewed.
            [TaskStatus.Stalled] =
                [TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress, TaskStatus.Triage,
                 TaskStatus.Rejected, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            // Parked on the client: their answer sends it back for triage, or drops it.
            [TaskStatus.NeedsHuman] = [TaskStatus.Triage, TaskStatus.Cancelled],
            // A bug in triage is accepted or rejected; a Feature is decomposed and activated.
            [TaskStatus.Triage] =
                [TaskStatus.Ready, TaskStatus.Rejected, TaskStatus.Active, TaskStatus.Claimed,
                 TaskStatus.InProgress, TaskStatus.Stalled, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.Rejected] = [],
            // A Feature stays Active while its children build; nothing claims it.
            [TaskStatus.Active] = [TaskStatus.Done, TaskStatus.Stalled, TaskStatus.Cancelled],
            [TaskStatus.Done] = [],
            [TaskStatus.Cancelled] = [],
        };

    /// <summary>Whether this status change is allowed.</summary>
    public static bool IsLegal(TaskStatus from, TaskStatus to) => Legal[from].Contains(to);

    public static void Require(TaskStatus from, TaskStatus to)
    {
        if (!IsLegal(from, to)) throw new IllegalTaskTransitionException(from, to);
    }

    /// <summary>
    /// Which role acts on a task in this status. Null when the harness itself owns it, or
    /// nobody does.
    /// </summary>
    public static AgentRole? RoleFor(TaskStatus status) => status switch
    {
        TaskStatus.InReview => AgentRole.Principal,
        TaskStatus.Qa => AgentRole.Qa,
        // Stuck work goes to the Principal, which authored the plan and can re-scope it.
        TaskStatus.Stalled => AgentRole.Principal,
        // A filed bug is the Principal's to accept or reject before any engineer touches it.
        TaskStatus.Triage => AgentRole.Principal,
        // The one status the loop cannot clear; the PM is the rung that talks to the client.
        TaskStatus.NeedsHuman => AgentRole.Pm,
        TaskStatus.Created => AgentRole.Pm,
        _ => null,
    };
}
