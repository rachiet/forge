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
    /// <summary>One discussions row as the database returns it.</summary>
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

    /// <summary>The column list every read shares.</summary>
    private const string SelectColumns = """
        SELECT id AS Id, task_id AS TaskId, parent_id AS ParentId, author AS Author,
               body AS Body, file_path AS FilePath, line_number AS LineNumber,
               status AS Status, created_at AS CreatedAt
        FROM discussions
        """;

    /// <summary>Opens a discussion on a task, optionally anchored to a file and line.</summary>
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

    /// <summary>The discussion with this id; throws if there is none.</summary>
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

    /// <summary>
    /// The task's exchange as a prompt renders it: the last <paramref name="limit"/> entries,
    /// oldest first, each cut to <paramref name="maxChars"/>. Bounded on both axes because a
    /// task that has been round the loop a dozen times would otherwise cost more than the work.
    /// Returns an empty string when the task has no history, so callers can append it blindly.
    /// </summary>
    public string History(long taskId, int limit = 6, int maxChars = 700)
    {
        var entries = conn.Query<(string Author, string Body)>("""
            SELECT author AS Author, body AS Body FROM discussions
            WHERE task_id = @taskId
            ORDER BY id DESC LIMIT @limit
            """, new { taskId, limit })
            .Reverse()
            .Select(e => $"**{e.Author}:** {Cut(e.Body, maxChars)}")
            .ToList();

        return entries.Count == 0 ? "" : string.Join("\n\n", entries);
    }

    /// <summary>Truncates a body to its opening, which is where a verdict states its point.</summary>
    private static string Cut(string body, int max) =>
        body.Length <= max ? body.Trim() : body[..max].Trim() + "…";

    /// <summary>The marker recording that a play has been attached to a task.</summary>
    private static string PlayMarker(string play) => $"[play] {play}";

    /// <summary>Whether this play has already been given to an instance working this task.</summary>
    public bool PlayUsed(long taskId, string play) =>
        conn.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM discussions WHERE task_id = @taskId AND body = @marker
            """, new { taskId, marker = PlayMarker(play) }) > 0;

    /// <summary>Records that a play was attached, so it is offered once and not again.</summary>
    public void RecordPlay(long taskId, string play) =>
        Open(taskId, "system", PlayMarker(play));

    /// <summary>Every discussion on a task, oldest first.</summary>
    public IReadOnlyList<DiscussionRecord> ForTask(long taskId) =>
        conn.Query<Row>($"{SelectColumns} WHERE task_id = @taskId ORDER BY created_at, id", new { taskId })
            .Select(r => r.ToRecord()).ToList();

    /// <summary>Marks a discussion resolved.</summary>
    public void Resolve(long id) =>
        conn.Execute("UPDATE discussions SET status = 'resolved' WHERE id = @id", new { id });

    /// <summary>How many discussions on a task are still open.</summary>
    public int OpenCount(long taskId) =>
        conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM discussions WHERE task_id = @taskId AND status = 'open'", new { taskId });
}
