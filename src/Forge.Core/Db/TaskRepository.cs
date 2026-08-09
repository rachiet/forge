using System.Data;
using Dapper;
using Forge.Core.Model;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Db;

/// <summary>Reads and writes the tasks table, and is the only path for a status change.</summary>
public sealed class TaskRepository(IDbConnection conn)
{
    /// <summary>One tasks row as the database returns it, converted to a TaskRecord.</summary>
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

    /// <summary>The column list every read shares, aliased to <see cref="Row"/>'s properties.</summary>
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

    /// <summary>Inserts a task and returns it with the id the database assigned.</summary>
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

    /// <summary>The task with this id; throws if there is none.</summary>
    public TaskRecord Get(long id) =>
        conn.QuerySingle<Row>($"{SelectColumns} WHERE id = @id", new { id }).ToRecord();

    /// <summary>The task with this id, or null.</summary>
    public TaskRecord? Find(long id) =>
        conn.QuerySingleOrDefault<Row>($"{SelectColumns} WHERE id = @id", new { id })?.ToRecord();

    /// <summary>Every task, lowest id first.</summary>
    public IReadOnlyList<TaskRecord> List() =>
        conn.Query<Row>($"{SelectColumns} ORDER BY id").Select(r => r.ToRecord()).ToList();

    /// <summary>
    /// The only path for status changes. Refuses a transition the legal-transition map does
    /// not allow, and re-checks the expected current status in the WHERE clause so a
    /// concurrent update cannot be lost.
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

    /// <summary>Adds to a task's running token total, which is for reporting only.</summary>
    public void AddTokensSpent(long taskId, int tokens)
    {
        if (tokens < 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        conn.Execute("""
            UPDATE tasks SET tokens_spent = tokens_spent + @tokens, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId, tokens });
    }

    /// <summary>
    /// The lowest-id task in a status the Principal owns — triage, blocked or out_of_budget —
    /// or null. needs_human is excluded: only the client can clear it.
    /// </summary>
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
    /// The bugs QA is seeded with so it does not re-file them: every rejected bug and every
    /// one still in flight. Fixed bugs are excluded, so a recurrence can be filed as a regression.
    /// </summary>
    public IReadOnlyList<TaskRecord> BugLedger() =>
        conn.Query<Row>($"""
            {SelectColumns} WHERE type = 'bug'
              AND status NOT IN ('done','cancelled')
            ORDER BY id
            """).Select(r => r.ToRecord()).ToList();

    /// <summary>How many bugs are in a given status.</summary>
    public int CountBugs(TaskStatus status) =>
        conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM tasks WHERE type = 'bug' AND status = @s",
            new { s = SnakeCaseEnum.ToSnakeCase(status) });

    /// <summary>
    /// How many tasks of any kind have finished. The QA gate compares this against its
    /// watermark to decide whether there is new work to verify.
    /// </summary>
    public int CountDone() =>
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM tasks WHERE status = 'done'");

    /// <summary>
    /// Attaches a task to its parent Feature. Nullable, because a split re-parents the
    /// replacements onto the parent of the task they replace, which may itself have none.
    /// </summary>
    public void SetParent(long childId, long? parentId) =>
        conn.Execute("""
            UPDATE tasks SET parent_id = @parentId, updated_at = datetime('now') WHERE id = @childId
            """, new { childId, parentId });

    /// <summary>How deep in a chain of splits this task sits. See <see cref="TaskRecord.SplitDepth"/>.</summary>
    public void SetSplitDepth(long taskId, int depth) =>
        conn.Execute("""
            UPDATE tasks SET split_depth = @depth, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId, depth });

    /// <summary>Attaches a task to a milestone.</summary>
    public void SetMilestone(long taskId, long milestoneId) =>
        conn.Execute("""
            UPDATE tasks SET milestone_id = @milestoneId, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId, milestoneId });

    /// <summary>
    /// Sets a task's strike count back to zero, so a task the client sent back does not
    /// return to the Principal already at its ceiling.
    /// </summary>
    public void ResetOutOfBudgetCount(long taskId) =>
        conn.Execute("""
            UPDATE tasks SET out_of_budget_count = 0, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId });

    /// <summary>
    /// The lowest-id task in <paramref name="status"/>, or null. How the runner finds work
    /// part-way through the pipeline again.
    /// </summary>
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
    /// terminal, lowest id first. A dependency is satisfied only by a `done` task, so
    /// cancelling one strands all of these unless they are cancelled with it.
    /// </summary>
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
    /// Active Features whose children have all reached a terminal state, and which the runner
    /// therefore closes. A Feature with no children yet is excluded.
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
    /// Whether any task is neither finished nor parked on the client. Coarse on purpose: it
    /// decides whether starting a worker is worthwhile, not which task that worker runs.
    /// </summary>
    public bool HasWorkForAWorker() =>
        conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks
            WHERE status NOT IN ('done','rejected','cancelled','needs_human')
            """) > 0;

    /// <summary>Whether every task is terminal and at least one non-bug task is done.</summary>
    public bool BoardQuiescent() =>
        conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks
            WHERE status NOT IN ('done','rejected','cancelled')
            """) == 0
        && conn.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM tasks WHERE type != 'bug' AND status = 'done'
            """) > 0;

    /// <summary>Replaces a task's progress note, which is what its next instance resumes from.</summary>
    public void SetProgressNote(long taskId, string note) =>
        conn.Execute("""
            UPDATE tasks SET progress_note = @note, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId, note });

    /// <summary>Records one budget or iteration exhaustion and returns the task's new strike count.</summary>
    public int IncrementOutOfBudgetCount(long taskId)
    {
        conn.Execute("""
            UPDATE tasks SET out_of_budget_count = out_of_budget_count + 1, updated_at = datetime('now')
            WHERE id = @taskId
            """, new { taskId });
        return conn.ExecuteScalar<int>(
            "SELECT out_of_budget_count FROM tasks WHERE id = @taskId", new { taskId });
    }

    /// <summary>Sets a task's token budget to an absolute value.</summary>
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
    /// Adds a DAG edge: <paramref name="taskId"/> cannot start until <paramref name="dependsOn"/>
    /// is done. Adding the same edge twice is harmless; a self-edge, an edge onto a Feature, or
    /// one that would close a cycle, is refused.
    /// </summary>
    public void AddDependency(long taskId, long dependsOn)
    {
        if (taskId == dependsOn)
            throw new ArgumentException(RefusalCode.Message(
                RefusalCode.SelfDependency, $"Task {taskId} cannot depend on itself."));
        // An edge onto an id that does not exist is never satisfied, since only a done task
        // satisfies one and no task will ever carry that id.
        if (Find(dependsOn) is not { } dependency)
            throw new ArgumentException(RefusalCode.Message(RefusalCode.NoSuchTask,
                $"Task {taskId} cannot depend on task {dependsOn}: there is no task {dependsOn}. "
                + "Depend on one of the tasks you have created, by the id create_task returned."));
        // A Feature is the parent of the tasks, not a node among them: it is marked done only
        // once they are, so a task waiting on one waits forever.
        if (dependency.Type == TaskType.Feature)
            throw new FeatureDependencyException(taskId, dependsOn);
        // Refusing the closing edge is the only thing keeping the stored graph acyclic.
        if (DependencyChain(from: dependsOn, to: taskId) is { Count: > 0 } chain)
            throw new DependencyCycleException(taskId, dependsOn, chain);
        conn.Execute("""
            INSERT OR IGNORE INTO task_deps (task_id, depends_on) VALUES (@taskId, @dependsOn)
            """, new { taskId, dependsOn });
    }

    /// <summary>
    /// The chain of ids by which <paramref name="from"/> already depends on
    /// <paramref name="to"/>, or empty if it does not. A path rather than a bool, so a refusal
    /// can name the cycle it would close. Breadth-first over a visited set, so a graph that
    /// already contains a cycle still terminates.
    /// </summary>
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
    : InvalidOperationException(RefusalCode.Message(RefusalCode.DependencyCycle,
        $"Task {taskId} cannot depend on task {dependsOn}: {dependsOn} already depends on {taskId} "
        + $"({string.Join(" → ", chain)}). A dependency is satisfied only by a DONE task, so this "
        + "cycle would leave every task in it permanently unclaimable and stall the board. Order "
        + "the work so it flows one way — if two tasks genuinely need each other, the shared part "
        + "belongs in a third task they both depend on."))
{
    public long TaskId { get; } = taskId;
    public long DependsOn { get; } = dependsOn;

    /// <summary>The existing path, from <see cref="DependsOn"/> back to <see cref="TaskId"/>.</summary>
    public IReadOnlyList<long> Chain { get; } = chain;
}

/// <summary>
/// The edge was refused because it points at a Feature. Written to be read by the Principal as
/// a tool error: it names the mechanism that makes the edge a deadlock and the edge to draw
/// instead, since the agent can only revise the plan it is authoring.
/// </summary>
public sealed class FeatureDependencyException(long taskId, long dependsOn)
    : InvalidOperationException(RefusalCode.Message(RefusalCode.DependsOnFeature,
        $"Task {taskId} cannot depend on task {dependsOn}: task {dependsOn} is the Feature these "
        + "tasks belong to. Every task you create is already part of it, and the harness marks the "
        + "Feature done once all of its tasks are done — so a task waiting on the Feature can never "
        + "be claimed and the board stalls. Depend on the sibling task that produces what this one "
        + "needs, or add no dependency at all if it can start immediately."))
{
    public long TaskId { get; } = taskId;
    public long DependsOn { get; } = dependsOn;
}
