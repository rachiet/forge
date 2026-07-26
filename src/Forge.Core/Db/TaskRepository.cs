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
        public string Type { get; init; } = "";
        public string Title { get; init; } = "";
        public string Objective { get; init; } = "";
        public string? AcceptanceCriteria { get; init; }
        public IReadOnlyList<string> ContextPaths { get; init; } = [];
        public RequirementsRef? RequirementsRef { get; init; }
        public string? AssignedRole { get; init; }
        public string Status { get; init; } = "";
        public int TokenBudget { get; init; }
        public int TokensSpent { get; init; }
        public int OutOfBudgetCount { get; init; }
        public string? ProgressNote { get; init; }
        public string? BranchName { get; init; }
        public string? CreatedBy { get; init; }
        public string? CreatedAt { get; init; }
        public string? UpdatedAt { get; init; }

        public TaskRecord ToRecord() => new()
        {
            Id = Id,
            MilestoneId = MilestoneId,
            Type = SnakeCaseEnum.Parse<TaskType>(Type),
            Title = Title,
            Objective = Objective,
            AcceptanceCriteria = AcceptanceCriteria,
            ContextPaths = ContextPaths,
            RequirementsRef = RequirementsRef,
            AssignedRole = AssignedRole is null ? null : SnakeCaseEnum.Parse<AgentRole>(AssignedRole),
            Status = SnakeCaseEnum.Parse<TaskStatus>(Status),
            TokenBudget = TokenBudget,
            TokensSpent = TokensSpent,
            OutOfBudgetCount = OutOfBudgetCount,
            ProgressNote = ProgressNote,
            BranchName = BranchName,
            CreatedBy = CreatedBy,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private const string SelectColumns = """
        SELECT id AS Id, milestone_id AS MilestoneId, type AS Type, title AS Title,
               objective AS Objective, acceptance_criteria AS AcceptanceCriteria,
               context_paths AS ContextPaths, requirements_ref AS RequirementsRef,
               assigned_role AS AssignedRole, status AS Status,
               token_budget AS TokenBudget, tokens_spent AS TokensSpent,
               out_of_budget_count AS OutOfBudgetCount,
               progress_note AS ProgressNote, branch_name AS BranchName,
               created_by AS CreatedBy, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM tasks
        """;

    public TaskRecord Insert(TaskRecord task)
    {
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO tasks (milestone_id, type, title, objective, acceptance_criteria,
                               context_paths, requirements_ref, assigned_role, status,
                               token_budget, tokens_spent, progress_note, branch_name, created_by)
            VALUES (@MilestoneId, @Type, @Title, @Objective, @AcceptanceCriteria,
                    @ContextPaths, @RequirementsRef, @AssignedRole, @Status,
                    @TokenBudget, @TokensSpent, @ProgressNote, @BranchName, @CreatedBy)
            RETURNING id
            """,
            new
            {
                task.MilestoneId,
                Type = SnakeCaseEnum.ToSnakeCase(task.Type),
                task.Title,
                task.Objective,
                task.AcceptanceCriteria,
                task.ContextPaths,
                task.RequirementsRef,
                AssignedRole = task.AssignedRole is { } r ? SnakeCaseEnum.ToSnakeCase(r) : null,
                Status = SnakeCaseEnum.ToSnakeCase(task.Status),
                task.TokenBudget,
                task.TokensSpent,
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

    /// <summary>Zero the spend for a fresh, Principal-directed attempt (redirect / takeover).</summary>
    public void ResetTokensSpent(long taskId) =>
        conn.Execute("""
            UPDATE tasks SET tokens_spent = 0, updated_at = datetime('now') WHERE id = @taskId
            """, new { taskId });

    /// <summary>
    /// The Principal's queue: the lowest-id blocked or out-of-budget task, highest
    /// priority because a stuck task usually gates the DAG. Tasks already escalated
    /// to a human (a pending message to pm/client) are skipped — they are waiting on
    /// a decision, so the autonomous loop must not keep re-triaging them.
    /// </summary>
    public TaskRecord? NextPrincipalOwned()
    {
        var id = conn.QueryFirstOrDefault<long?>("""
            SELECT t.id FROM tasks t
            WHERE t.status IN ('out_of_budget','blocked')
              AND NOT EXISTS (
                SELECT 1 FROM messages m
                WHERE m.task_id = t.id AND m.to_agent IN ('pm','client') AND m.status = 'pending')
            ORDER BY t.id LIMIT 1
            """);
        return id is { } i ? Get(i) : null;
    }

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
    /// </summary>
    public void AddDependency(long taskId, long dependsOn)
    {
        if (taskId == dependsOn)
            throw new ArgumentException($"Task {taskId} cannot depend on itself.");
        conn.Execute("""
            INSERT OR IGNORE INTO task_deps (task_id, depends_on) VALUES (@taskId, @dependsOn)
            """, new { taskId, dependsOn });
    }

    public IReadOnlyList<long> DependenciesOf(long taskId) =>
        conn.Query<long>("SELECT depends_on FROM task_deps WHERE task_id = @taskId ORDER BY depends_on",
            new { taskId }).ToList();
}
