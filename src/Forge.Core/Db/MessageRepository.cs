using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

public sealed class MessageRepository(IDbConnection conn)
{
    private sealed record Row
    {
        public long Id { get; init; }
        public string FromAgent { get; init; } = "";
        public string ToAgent { get; init; } = "";
        public long? TaskId { get; init; }
        public string Type { get; init; } = "";
        public string Payload { get; init; } = "";
        public string Status { get; init; } = "";
        public string? CreatedAt { get; init; }

        public Message ToMessage() => Message.FromRow(
            SnakeCaseEnum.Parse<MessageType>(Type), Id, FromAgent, ToAgent,
            TaskId, Payload, SnakeCaseEnum.Parse<MessageStatus>(Status), CreatedAt);
    }

    private const string SelectColumns = """
        SELECT id AS Id, from_agent AS FromAgent, to_agent AS ToAgent,
               task_id AS TaskId, type AS Type, payload AS Payload, status AS Status,
               created_at AS CreatedAt
        FROM messages
        """;

    public Message Insert(Message message)
    {
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO messages (from_agent, to_agent, task_id, type, payload, status)
            VALUES (@FromAgent, @ToAgent, @TaskId, @Type, @Payload, @Status)
            RETURNING id
            """,
            new
            {
                message.FromAgent,
                message.ToAgent,
                message.TaskId,
                Type = SnakeCaseEnum.ToSnakeCase(message.Type),
                message.Payload,
                Status = SnakeCaseEnum.ToSnakeCase(message.Status),
            });
        return message with { Id = id };
    }

    /// <summary>Queue read: pending messages for one receiver, oldest first (spec §6 semantics).</summary>
    public IReadOnlyList<Message> Pending(string toAgent) =>
        conn.Query<Row>(
                $"{SelectColumns} WHERE to_agent = @toAgent AND status = 'pending' ORDER BY created_at, id",
                new { toAgent })
            .Select(r => r.ToMessage()).ToList();

    public void SetStatus(long id, MessageStatus status) =>
        conn.Execute("UPDATE messages SET status = @status WHERE id = @id",
            new { id, status = SnakeCaseEnum.ToSnakeCase(status) });

    /// <summary>Log read: full trail, optionally filtered to one task, oldest first.</summary>
    public IReadOnlyList<Message> Log(long? taskId = null) =>
        conn.Query<Row>(
                taskId is null
                    ? $"{SelectColumns} ORDER BY created_at, id"
                    : $"{SelectColumns} WHERE task_id = @taskId ORDER BY created_at, id",
                new { taskId })
            .Select(r => r.ToMessage()).ToList();
}
