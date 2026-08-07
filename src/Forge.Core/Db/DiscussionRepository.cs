using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

/// <summary>
/// Reads and writes the discussions table: review comments anchored to a file and line, and
/// unanchored notes on a task. Review rejections and client guidance are recorded here, so a
/// task carries the history of why it was sent back and not only its current state.
/// </summary>
public sealed class DiscussionRepository(IDbConnection conn)
{
    private sealed record Row
    {
        public long Id { get; init; }
        public long TaskId { get; init; }
        public long? ParentId { get; init; }
        public string Author { get; init; } = "";
        public string Body { get; init; } = "";
        public string? FilePath { get; init; }
        public int? LineNumber { get; init; }
        public string Status { get; init; } = "";
        public string? CreatedAt { get; init; }

        public DiscussionRecord ToRecord() => new()
        {
            Id = Id,
            TaskId = TaskId,
            ParentId = ParentId,
            Author = Author,
            Body = Body,
            FilePath = FilePath,
            LineNumber = LineNumber,
            Resolved = Status == "resolved",
            CreatedAt = CreatedAt,
        };
    }

    private const string SelectColumns = """
        SELECT id AS Id, task_id AS TaskId, parent_id AS ParentId, author AS Author,
               body AS Body, file_path AS FilePath, line_number AS LineNumber,
               status AS Status, created_at AS CreatedAt
        FROM discussions
        """;

    public DiscussionRecord Open(long taskId, string author, string body,
        string? filePath = null, int? lineNumber = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Discussion body must be non-empty.", nameof(body));

        var id = conn.ExecuteScalar<long>("""
            INSERT INTO discussions (task_id, author, body, file_path, line_number)
            VALUES (@taskId, @author, @body, @filePath, @lineNumber)
            RETURNING id
            """,
            new { taskId, author, body, filePath, lineNumber });
        return Get(id);
    }

    public DiscussionRecord Get(long id) =>
        conn.QuerySingle<Row>($"{SelectColumns} WHERE id = @id", new { id }).ToRecord();

    /// <summary>What the client said about this task, oldest first.</summary>
    /// <remarks>
    /// Rendered into every task packet. Unlike the progress note — overwritten by each
    /// redirect and review — this survives, so an instruction given once still reaches
    /// the engineer three attempts later.
    /// </remarks>
    public IReadOnlyList<string> ClientGuidance(long taskId) =>
        conn.Query<string>("""
            SELECT body FROM discussions
            WHERE task_id = @taskId AND author = 'pm' AND body LIKE '[client guidance]%'
            ORDER BY id
            """, new { taskId })
            .Select(b => b["[client guidance]".Length..].Trim())
            .ToList();

    public IReadOnlyList<DiscussionRecord> ForTask(long taskId) =>
        conn.Query<Row>($"{SelectColumns} WHERE task_id = @taskId ORDER BY created_at, id", new { taskId })
            .Select(r => r.ToRecord()).ToList();

    public void Resolve(long id) =>
        conn.Execute("UPDATE discussions SET status = 'resolved' WHERE id = @id", new { id });

    public int OpenCount(long taskId) =>
        conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM discussions WHERE task_id = @taskId AND status = 'open'", new { taskId });
}
