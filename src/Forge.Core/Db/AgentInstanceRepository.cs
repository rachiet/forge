using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

/// <summary>Reads and writes the agent_instances table: one row per agent run.</summary>
public sealed class AgentInstanceRepository(IDbConnection conn)
{
    /// <summary>One agent_instances row as the database returns it.</summary>
    private sealed record Row
    {
        public string Id { get; init; } = "";
        public string Role { get; init; } = "";
        public string Model { get; init; } = "";
        public long? TaskId { get; init; }
        public string? StartedAt { get; init; }
        public string? EndedAt { get; init; }
        public string? EndReason { get; init; }

        public AgentInstanceRecord ToRecord() => new()
        {
            Id = Id,
            Role = SnakeCaseEnum.Parse<AgentRole>(Role),
            Model = Model,
            TaskId = TaskId,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            EndReason = EndReason is null ? null : SnakeCaseEnum.Parse<EndReason>(EndReason),
        };
    }

    /// <summary>The column list every read shares.</summary>
    private const string SelectColumns = """
        SELECT id AS Id, role AS Role, model AS Model, task_id AS TaskId,
               started_at AS StartedAt, ended_at AS EndedAt, end_reason AS EndReason
        FROM agent_instances
        """;

    /// <summary>
    /// Spec convention: 'eng-20260718-093012'. Seconds are not unique enough under
    /// test or a fast resume loop, so collisions get a suffix rather than throwing.
    /// </summary>
    /// <summary>A fresh instance id of the form `prefix-yyyyMMdd-HHmmss`, unique in this database.</summary>
    public string NewId(string prefix)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var candidate = $"{prefix}-{stamp}";
        for (var n = 2; Exists(candidate); n++) candidate = $"{prefix}-{stamp}-{n}";
        return candidate;
    }

    /// <summary>Whether an instance with this id has already been recorded.</summary>
    private bool Exists(string id) =>
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM agent_instances WHERE id = @id", new { id }) > 0;

    /// <summary>Records the start of an instance: its role, its model, and the task it works.</summary>
    public AgentInstanceRecord Start(string id, AgentRole role, string model, long? taskId)
    {
        conn.Execute("""
            INSERT INTO agent_instances (id, role, model, task_id)
            VALUES (@id, @role, @model, @taskId)
            """,
            new { id, role = SnakeCaseEnum.ToSnakeCase(role), model, taskId });
        return Get(id);
    }

    /// <summary>Records why an instance stopped, and when.</summary>
    public void End(string id, EndReason reason) =>
        conn.Execute("""
            UPDATE agent_instances
            SET ended_at = datetime('now'), end_reason = @reason
            WHERE id = @id
            """,
            new { id, reason = SnakeCaseEnum.ToSnakeCase(reason) });

    /// <summary>The instance with this id; throws if there is none.</summary>
    public AgentInstanceRecord Get(string id) =>
        conn.QuerySingle<Row>($"{SelectColumns} WHERE id = @id", new { id }).ToRecord();

    /// <summary>Every instance that has worked a task, oldest first.</summary>
    public IReadOnlyList<AgentInstanceRecord> ForTask(long taskId) =>
        conn.Query<Row>($"{SelectColumns} WHERE task_id = @taskId ORDER BY started_at, id", new { taskId })
            .Select(r => r.ToRecord()).ToList();
}
