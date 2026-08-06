namespace Forge.Core.Model;

/// <summary>
/// One row of the tasks table. Construct new tasks via <see cref="Create"/> —
/// never insert a naked record — and change status only through
/// <see cref="TaskTransitions"/> (repositories enforce this).
/// </summary>
public sealed record TaskRecord
{
    public long Id { get; init; }
    public long? MilestoneId { get; init; }
    /// <summary>
    /// The Feature this task belongs to: a self-reference to the parent Feature's
    /// task id. Null on a top-level task (a Feature itself, or any task created
    /// outside a Feature). The Principal decomposes a Feature into child tasks that
    /// carry its id here — that linkage is what "the Feature is complete" is read from.
    /// </summary>
    public long? ParentId { get; init; }
    public required TaskType Type { get; init; }
    public required string Title { get; init; }
    public required string Objective { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public IReadOnlyList<string> ContextPaths { get; init; } = [];
    public RequirementsRef? RequirementsRef { get; init; }
    public AgentRole? AssignedRole { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Created;
    public required int TokenBudget { get; init; }
    public int TokensSpent { get; init; }
    /// <summary>Times this task exhausted its budget/iterations. At 2 the Principal takes it over.</summary>
    public int OutOfBudgetCount { get; init; }
    /// <summary>
    /// How many splits deep this task is: 0 if the Principal planned it, 1 if it replaced a
    /// task too big to land. `break_and_relink` refuses to split past the cap, which is what
    /// keeps the ladder finite — without it every replacement could be split again.
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
        long? milestoneId = null,
        string? acceptanceCriteria = null,
        IReadOnlyList<string>? contextPaths = null,
        RequirementsRef? requirementsRef = null,
        AgentRole? assignedRole = null,
        string? createdBy = null,
        long? parentId = null,
        int splitDepth = 0)
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
            Objective = objective,
            TokenBudget = tokenBudget,
            MilestoneId = milestoneId,
            ParentId = parentId,
            AcceptanceCriteria = acceptanceCriteria,
            ContextPaths = contextPaths ?? [],
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
/// The only way a task status may change. Routing ("who acts next") is derived
/// from status via <see cref="RoleFor"/>, never stored — no "next actor" column.
/// </summary>
public static class TaskTransitions
{
    private static readonly IReadOnlyDictionary<TaskStatus, TaskStatus[]> Legal =
        new Dictionary<TaskStatus, TaskStatus[]>
        {
            [TaskStatus.Created] = [TaskStatus.Ready, TaskStatus.Cancelled],
            // Ready → NeedsHuman is how a task out of engineer attempts leaves the board:
            // the cap is checked before the claim, so it is still Ready when it gives up.
            [TaskStatus.Ready] =
                [TaskStatus.Claimed, TaskStatus.Blocked, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.Claimed] =
                [TaskStatus.InProgress, TaskStatus.Ready, TaskStatus.Blocked,
                 TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.InProgress] =
                [TaskStatus.InReview, TaskStatus.Blocked, TaskStatus.OutOfBudget,
                 TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            // Rejected is reachable from InReview too: a reviewer of a bug-fix can reject
            // the underlying bug when it turns out not to be a real defect.
            [TaskStatus.InReview] =
                [TaskStatus.Merging, TaskStatus.InProgress, TaskStatus.Blocked, TaskStatus.Rejected, TaskStatus.Cancelled],
            [TaskStatus.Merging] = [TaskStatus.Qa, TaskStatus.InProgress, TaskStatus.Blocked],
            [TaskStatus.Qa] = [TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Blocked],
            // Blocked and OutOfBudget are Principal-owned. The Principal triages them
            // back to the engineer (→ Ready), takes them over to implement directly
            // (→ Claimed/InProgress), or gives up (→ Blocked/Cancelled).
            // Triage/Rejected are reachable from Blocked so the PM can act on a bug the
            // human reviewed: re-triage it (back to the Principal) or reject it outright.
            [TaskStatus.Blocked] =
                [TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress, TaskStatus.Triage,
                 TaskStatus.Rejected, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.OutOfBudget] =
                [TaskStatus.Ready, TaskStatus.Claimed, TaskStatus.InProgress, TaskStatus.Blocked,
                 TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            // The Principal is out of options and the PM has put the task to the client.
            // Their answer either sends it back for another triage or drops it.
            [TaskStatus.NeedsHuman] = [TaskStatus.Triage, TaskStatus.Cancelled],
            // Triage is entered by two kinds of task. A QA-filed bug: the Principal
            // accepts it (→ Ready, an engineer fixes it) or rejects it (→ Rejected).
            // A PM-opened Feature: the Principal decomposes it (→ Active) once its
            // child tasks exist.
            [TaskStatus.Triage] =
                [TaskStatus.Ready, TaskStatus.Rejected, TaskStatus.Active, TaskStatus.Claimed,
                 TaskStatus.InProgress, TaskStatus.Blocked, TaskStatus.NeedsHuman, TaskStatus.Cancelled],
            [TaskStatus.Rejected] = [],
            // A Feature sits Active while its children build. The harness closes it
            // (→ Done) when every child is terminal; a decomposition failure can block
            // or cancel it. Nothing claims an Active task, so the loop never re-pulls it.
            [TaskStatus.Active] = [TaskStatus.Done, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Done] = [],
            [TaskStatus.Cancelled] = [],
        };

    public static bool IsLegal(TaskStatus from, TaskStatus to) => Legal[from].Contains(to);

    public static void Require(TaskStatus from, TaskStatus to)
    {
        if (!IsLegal(from, to)) throw new IllegalTaskTransitionException(from, to);
    }

    /// <summary>Static handoff map: which role acts on a task in this status.
    /// Null means the harness itself (or nobody) owns the state.</summary>
    public static AgentRole? RoleFor(TaskStatus status) => status switch
    {
        TaskStatus.InReview => AgentRole.Principal,
        TaskStatus.Qa => AgentRole.Qa,
        // The Principal authored the task DAG, structure and contracts, so a stalled
        // or blocked task is theirs to triage — not the PM's, who can neither set a
        // budget nor make a technical call. Escalations climb engineer → principal →
        // pm → client; the PM is only the client-facing rung.
        TaskStatus.Blocked => AgentRole.Principal,
        TaskStatus.OutOfBudget => AgentRole.Principal,
        // A filed bug is the Principal's to accept or reject before any engineer touches it.
        TaskStatus.Triage => AgentRole.Principal,
        // The one status the autonomous loop cannot clear: only the client can, and the
        // PM is the rung that talks to them.
        TaskStatus.NeedsHuman => AgentRole.Pm,
        TaskStatus.Created => AgentRole.Pm,
        _ => null,
    };
}
