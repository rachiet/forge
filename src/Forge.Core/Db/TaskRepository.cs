using System.Data;
using Dapper;
using Forge.Core.Model;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Db;

public sealed class TaskRepository(IDbConnection conn)
{
    private sealed record Row
    {
        public long Id { get; init; }
        public long? MilestoneId { get; init; }
        public long? ParentId { get; init; }
        public string Type { get; init; } = "";
        public string Title { get; init; } = "";
        public string Objective { get; init; } = "";
        public string? AcceptanceCriteria { get; init; }
        public IReadOnlyList<string> ContextPaths { get; init; } = [];
        public IReadOnlyList<string> ContractOps { get; init; } = [];
        public RequirementsRef? RequirementsRef { get; init; }
        public string? AssignedRole { get; init; }
        public string Status { get; init; } = "";
        public int TokenBudget { get; init; }
        public int TokensSpent { get; init; }
        public int OutOfBudgetCount { get; init; }
        public int SplitDepth { get; init; }
        public string? ProgressNote { get; init; }
        public string? BranchName { get; init; }
        public string? CreatedBy { get; init; }
        public string? CreatedAt { get; init; }
        public string? UpdatedAt { get; init; }

        public TaskRecord ToRecord() => new()
        {
            Id = Id,
            MilestoneId = MilestoneId,
            ParentId = ParentId,
            Type = SnakeCaseEnum.Parse<TaskType>(Type),
            Title = Title,
            Objective = Objective,
            AcceptanceCriteria = AcceptanceCriteria,
            ContextPaths = ContextPaths,
            ContractOps = ContractOps,
            RequirementsRef = RequirementsRef,
            AssignedRole = AssignedRole is null ? null : SnakeCaseEnum.Parse<AgentRole>(AssignedRole),
            Status = SnakeCaseEnum.Parse<TaskStatus>(Status),
            TokenBudget = TokenBudget,
            TokensSpent = TokensSpent,
            OutOfBudgetCount = OutOfBudgetCount,
            SplitDepth = SplitDepth,
            ProgressNote = ProgressNote,
            BranchName = BranchName,
            CreatedBy = CreatedBy,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private const string SelectColumns = """
        SELECT id AS Id, milestone_id AS MilestoneId, parent_id AS ParentId, type AS Type, title AS Title,
               objective AS Objective, acceptance_criteria AS AcceptanceCriteria,
               context_paths AS ContextPaths, contract_ops AS ContractOps,
               requirements_ref AS RequirementsRef,
               assigned_role AS AssignedRole, status AS Status,
               token_budget AS TokenBudget, tokens_spent AS TokensSpent,
               out_of_budget_count AS OutOfBudgetCount, split_depth AS SplitDepth,
               progress_note AS ProgressNote, branch_name AS BranchName,
               created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM tasks
        """;

    public TaskRecord Insert(TaskRecord task)
    {
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO tasks (milestone_id, parent_id, type, title, objective, acceptance_criteria,
                               context_paths, contract_ops, requirements_ref, assigned_role, status,
                               token_budget, tokens_spent, split_depth, progress_note,
                               branch_name, created_by)
            VALUES (@MilestoneId, @ParentId, @Type, @Title, @Objective, @AcceptanceCriteria,
                    @ContextPaths, @ContractOps, @RequirementsRef, @AssignedRole, @Status,
                    @TokenBudget, @TokensSpent, @SplitDepth, @ProgressNote, @BranchName, @CreatedBy)
            RETURNING id
            """,
            new
            {
                task.MilestoneId,
                task.ParentId,
                Type = SnakeCaseEnum.ToSnakeCase(task.Type),
                task.Title,
                task.Objective,
                task.AcceptanceCriteria,
                task.ContextPaths,
                task.ContractOps,
                task.RequirementsRef,
                AssignedRole = task.AssignedRole is { } r ? SnakeCaseEnum.ToSnakeCase(r) : null,
                Status = SnakeCaseEnum.ToSnakeCase(task.Status),
                task.TokenBudget,
                task.TokensSpent,
                task.SplitDepth,
                task.ProgressNote,
                task.BranchName,
                task.CreatedBy,
            });
        return task with { Id = id };
    }

    public TaskRecord Get(long id) =>
        conn.QuerySingle<Row>($"{SelectColumns} WHERE id = @id", new { id }).ToRecord();

    public TaskRecord? Find(long id) =>
        conn.QuerySingleOrDefault<Row>($"{SelectColumns} WHERE id = @id", new { id })?.ToRecord();

    public IReadOnlyList<TaskRecord> List() =>
        conn.Query<Row>($"{SelectColumns} ORDER BY id").Select(r => r.ToRecord()).ToList();

    /// <summary>
    /// The only path for status changes — never raw UPDATE tasks SET status.
    /// Guards against both illegal transitions and lost updates (the WHERE clause
    /// re-checks the expected current status).
    /// </summary>
    public TaskRecord Transition(long taskId, TaskStatus to)
    {
        var current = Get(taskId);
        TaskTransitions.Require(current.Status, to);
        var updated = conn.Execute("""
            UPDATE tasks SET status = @to, updated_at = datetime('now')
            WHERE id = @taskId AND status = @from
            """,
            new
            {
                taskId,
                to = SnakeCaseEnum.ToSnakeCase(to),
                from = SnakeCaseEnum.ToSnakeCase(current.Status),
            });
        if (updated != 1)
            throw new InvalidOperationException(
                $"Task {taskId} changed status concurrently; transition to {to} not applied.");
        return current with { Status = to };
    }

    public void AddTokensSpent(long taskId, int tokens)
    {
        if (tokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        conn.Execute("""
            UPDATE tasks SET tokens_spent = tokens_spent + @tokens, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId, tokens });
    }

    /// <summary>The lowest-id task in a Principal-owned status, or null if there is none.</summary>
    /// <remarks>
    /// A stuck task usually gates the DAG, so this queue is served before the engineer's.
    /// needs_human is deliberately absent: only the client can clear it.
    /// </remarks>
    public TaskRecord? NextPrincipalOwned()
    {
        var id = conn.QueryFirstOrDefault<long?>("""
            SELECT id FROM tasks
            WHERE status IN ('out_of_budget','blocked','triage')
            ORDER BY id LIMIT 1
            """);
        return id is { } i ? Get(i) : null;
    }

    /// <summary>
    /// The bug ledger QA is seeded with so it does not re-file what has already been
    /// filed: every rejected bug (a durable "not a bug" verdict) and every bug still
    /// in flight. Fixed (done) bugs are excluded — a recurrence of one is a genuine
    /// regression that QA should be free to file again.
    /// </summary>
    public IReadOnlyList<TaskRecord> BugLedger() =>
        conn.Query<Row>($"""
            {SelectColumns} WHERE type = 'bug'
              AND status NOT IN ('done','cancelled')
            ORDER BY id
            """).Select(r => r.ToRecord()).ToList();

    /// <summary>Count bugs in a given status — the QA gate's raw material.</summary>
    public int CountBugs(TaskStatus status) =>
        conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM tasks WHERE type = 'bug' AND status = @s",
            new { s = SnakeCaseEnum.ToSnakeCase(status) });

    /// <summary>
    /// Count every completed task (tasks, bug-fixes, chores). This is the QA gate's
    /// watermark: it rises whenever any work finishes — the initial build, a bug-fix, or
    /// a change request's tasks — so QA re-verifies after all of them, uniformly.
    /// </summary>
    public int CountDone() =>
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM tasks WHERE status = 'done'");

    /// <summary>
    /// Attach a child task to its parent Feature — the harness back-fills this after the
    /// Principal decomposes a Feature, so the linkage is computed by trusted code rather
    /// than left to the model to set correctly on every create_task.
    /// </summary>
    /// <remarks>
    /// Nullable because a split re-parents the replacement tasks onto the parent OF the task
    /// being replaced, which is null when that task sat under no Feature.
    /// </remarks>
    public void SetParent(long childId, long? parentId) =>
        conn.Execute("""
            UPDATE tasks SET parent_id = @parentId, updated_at = datetime('now') WHERE id = @childId
            """, new { childId, parentId });

    /// <summary>How deep in a chain of splits this task sits. See <see cref="TaskRecord.SplitDepth"/>.</summary>
    public void SetSplitDepth(long taskId, int depth) =>
        conn.Execute("""
            UPDATE tasks SET split_depth = @depth, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId, depth });

    /// <summary>
    /// Attach a task to a milestone. Set by the harness when a child inherits its
    /// Feature's milestone; the Principal may also name one directly on create_task.
    /// </summary>
    public void SetMilestone(long taskId, long milestoneId) =>
        conn.Execute("""
            UPDATE tasks SET milestone_id = @milestoneId, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId, milestoneId });

    /// <summary>Sets a task's strike count back to zero.</summary>
    /// <remarks>
    /// Called when the client sends a task back for another attempt: without it the task
    /// returns to the Principal already at its strike ceiling and is given up on at once.
    /// </remarks>
    public void ResetOutOfBudgetCount(long taskId) =>
        conn.Execute("""
            UPDATE tasks SET out_of_budget_count = 0, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId });

    /// <summary>The lowest-id task in <paramref name="status"/>, or null.</summary>
    /// <remarks>
    /// How a task mid-pipeline is found again. Review and merge used to run inline after
    /// the engineer, so a worker that died between them left the task in a status nothing
    /// queried; making each step a queue entry is what lets it resume.
    /// </remarks>
    public TaskRecord? NextInStatus(TaskStatus status)
    {
        var id = conn.QueryFirstOrDefault<long?>(
            "SELECT id FROM tasks WHERE status = @status ORDER BY id LIMIT 1",
            new { status = SnakeCaseEnum.ToSnakeCase(status) });
        return id is { } i ? Get(i) : null;
    }

    /// <summary>Every task in <see cref="TaskStatus.NeedsHuman"/>, lowest id first.</summary>
    public IReadOnlyList<TaskRecord> AwaitingClient() =>
        conn.Query<Row>($"{SelectColumns} WHERE status = 'needs_human' ORDER BY id")
            .Select(r => r.ToRecord()).ToList();

    /// <summary>Cancelled tasks that still have a branch recorded, lowest id first.</summary>
    public IReadOnlyList<TaskRecord> CancelledWithBranch() =>
        conn.Query<Row>($"""
            {SelectColumns} WHERE status = 'cancelled' AND branch_name IS NOT NULL ORDER BY id
            """).Select(r => r.ToRecord()).ToList();

    /// <summary>Forgets the task's branch, marking its working copy as already cleaned up.</summary>
    public void ClearBranch(long taskId) =>
        conn.Execute("""
            UPDATE tasks SET branch_name = NULL, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId });

    /// <summary>
    /// The tasks that transitively depend on <paramref name="taskId"/> and are not yet
    /// terminal, lowest id first.
    /// </summary>
    /// <remarks>
    /// A dependency edge is only satisfied by a `done` task, so cancelling one strands
    /// everything downstream. Callers cancel these together rather than leave them
    /// permanently unclaimable.
    /// </remarks>
    public IReadOnlyList<TaskRecord> UnfinishedDependents(long taskId) =>
        conn.Query<Row>($"""
            WITH RECURSIVE downstream(id) AS (
              SELECT task_id FROM task_deps WHERE depends_on = @taskId
              UNION
              SELECT d.task_id FROM task_deps d JOIN downstream ON d.depends_on = downstream.id
            )
            {SelectColumns} WHERE id IN (SELECT id FROM downstream)
              AND status NOT IN ('done','rejected','cancelled')
            ORDER BY id
            """, new { taskId }).Select(r => r.ToRecord()).ToList();

    /// <summary>
    /// Features in `active` whose children are all terminal (done/rejected/cancelled) —
    /// these are complete and the harness closes them to `done`, which is what arms QA.
    /// A Feature with no children yet is excluded: it must not close before any work runs.
    /// "The last child finished" is derived from this query, never tracked as a flag.
    /// </summary>
    public IReadOnlyList<long> ActiveFeaturesReadyToClose() =>
        conn.Query<long>("""
            SELECT f.id FROM tasks f
            WHERE f.type = 'feature' AND f.status = 'active'
              AND EXISTS (SELECT 1 FROM tasks c WHERE c.parent_id = f.id)
              AND NOT EXISTS (
                SELECT 1 FROM tasks c
                WHERE c.parent_id = f.id AND c.status NOT IN ('done','rejected','cancelled'))
            ORDER BY f.id
            """).ToList();

    /// <summary>
    /// Is there anything a worker could pick up? True while any task is neither finished nor
    /// parked on the client. Deliberately coarse — it decides whether starting a worker is
    /// worth it, not which task runs, and a worker with nothing to claim simply drains.
    /// </summary>
    public bool HasWorkForAWorker() =>
        conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks
            WHERE status NOT IN ('done','rejected','cancelled','needs_human')
            """) > 0;

    /// <summary>Are all non-bug tasks terminal and no bug still active? Then the board is quiescent.</summary>
    public bool BoardQuiescent() =>
        conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks
            WHERE status NOT IN ('done','rejected','cancelled')
            """) == 0
        && conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks WHERE type != 'bug' AND status = 'done'
            """) > 0;

    public void SetProgressNote(long taskId, string note) =>
        conn.Execute("""
            UPDATE tasks SET progress_note = @note, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId, note });

    /// <summary>Count one budget/iteration exhaustion; returns the new total (the strike count).</summary>
    public int IncrementOutOfBudgetCount(long taskId)
    {
        conn.Execute("""
            UPDATE tasks SET out_of_budget_count = out_of_budget_count + 1, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId });
        return conn.ExecuteScalar<int>(
            "SELECT out_of_budget_count FROM tasks WHERE id = @taskId", new { taskId });
    }

    /// <summary>Raise (or lower) a task's token budget — the Principal's lever when triaging an out-of-budget task.</summary>
    public void SetBudget(long taskId, int tokenBudget)
    {
        if (tokenBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget, "Token budget must be positive.");
        conn.Execute("""
            UPDATE tasks SET token_budget = @tokenBudget, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId, tokenBudget });
    }

    /// <summary>
    /// An edge of the task DAG (spec §6 task_deps): taskId cannot start until
    /// dependsOn is done. INSERT OR IGNORE so authoring the same edge twice is
    /// harmless; a self-edge is a mistake the Principal shouldn't make and we refuse.
    /// A cycle is refused for the same reason, one step further out: see
    /// <see cref="DependencyChain"/>.
    /// </summary>
    public void AddDependency(long taskId, long dependsOn)
    {
        if (taskId == dependsOn)
            throw new ArgumentException($"Task {taskId} cannot depend on itself.");
        // Refusing the closing edge is what keeps the graph on disk acyclic — there is no
        // later repair, because nothing rereads the DAG once the design phase has written it.
        if (DependencyChain(from: dependsOn, to: taskId) is { Count: > 0 } chain)
            throw new DependencyCycleException(taskId, dependsOn, chain);
        conn.Execute("""
            INSERT OR IGNORE INTO task_deps (task_id, depends_on) VALUES (@taskId, @dependsOn)
            """, new { taskId, dependsOn });
    }

    /// <summary>
    /// The chain of edges by which <paramref name="from"/> already depends on
    /// <paramref name="to"/> — ids ordered from one to the other — or empty if it does not.
    /// A path rather than a bool because the caller's job is to show the Principal the cycle
    /// it is about to close, and "4 → 3 → 4" is actionable where "cycle detected" is not.
    /// </summary>
    /// <remarks>
    /// Breadth-first over one snapshot of the edges, with a visited set: a graph that
    /// ALREADY contains a cycle (HabitTracker's did) must terminate here too, or the check
    /// added to prevent a deadlock becomes one.
    /// </remarks>
    public IReadOnlyList<long> DependencyChain(long from, long to)
    {
        var edges = conn.Query<(long TaskId, long DependsOn)>(
                "SELECT task_id, depends_on FROM task_deps")
            .GroupBy(e => e.TaskId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.DependsOn).ToList());

        // Each visited node maps to the node it was reached from, so the path can be walked
        // back out once the target is found. `from` maps to itself to terminate that walk.
        var cameFrom = new Dictionary<long, long> { [from] = from };
        var queue = new Queue<long>([from]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edges.TryGetValue(current, out var next)) continue;
            foreach (var dep in next)
            {
                if (!cameFrom.TryAdd(dep, current)) continue;
                if (dep == to)
                {
                    var path = new List<long> { to };
                    for (var step = current; step != from; step = cameFrom[step]) path.Add(step);
                    path.Add(from);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(dep);
            }
        }
        return [];
    }

    public IReadOnlyList<long> DependenciesOf(long taskId) =>
        conn.Query<long>("SELECT depends_on FROM task_deps WHERE task_id = @taskId ORDER BY depends_on",
            new { taskId }).ToList();

    /// <summary>The tasks that wait on this one — the other half of <see cref="DependenciesOf"/>.</summary>
    public IReadOnlyList<long> DependentsOf(long taskId) =>
        conn.Query<long>("SELECT task_id FROM task_deps WHERE depends_on = @taskId ORDER BY task_id",
            new { taskId }).ToList();

    /// <summary>
    /// Drops every edge into or out of a task. Used when a split replaces it: its dependents
    /// and dependencies have already been re-pointed at the replacements, and leaving the old
    /// edges would make the replacements wait on a cancelled task — a wait nothing can end,
    /// since only a `done` task satisfies a dependency.
    /// </summary>
    public void RemoveDependenciesInvolving(long taskId) =>
        conn.Execute("DELETE FROM task_deps WHERE task_id = @taskId OR depends_on = @taskId",
            new { taskId });
}

/// <summary>
/// The edge was refused because it would close a cycle. The message is written to be read by
/// the Principal as a tool error — it names the existing path and says what to do about it,
/// since the agent has no tool to delete an edge and can only revise the plan it is authoring.
/// </summary>
public sealed class DependencyCycleException(long taskId, long dependsOn, IReadOnlyList<long> chain)
    : InvalidOperationException(
        $"Task {taskId} cannot depend on task {dependsOn}: {dependsOn} already depends on {taskId} "
        + $"({string.Join(" → ", chain)}). A dependency is satisfied only by a DONE task, so this "
        + "cycle would leave every task in it permanently unclaimable and stall the board. Order "
        + "the work so it flows one way — if two tasks genuinely need each other, the shared part "
        + "belongs in a third task they both depend on.")
{
    public long TaskId { get; } = taskId;
    public long DependsOn { get; } = dependsOn;

    /// <summary>The existing path, from <see cref="DependsOn"/> back to <see cref="TaskId"/>.</summary>
    public IReadOnlyList<long> Chain { get; } = chain;
}
