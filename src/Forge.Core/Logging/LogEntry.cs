namespace Forge.Core.Logging;

/// <summary>
/// One log line: timestamp, project, task, event type and message. Every line names its
/// project; `task` is null for project-level events, so filtering by project includes every
/// task's lines and filtering by task narrows within them.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Project,
    long? Task,
    EventType Type,
    string Message)
{
    // Unit Separator: invisible and never present in normal text, so a message
    // containing '|' or ':' can't corrupt the columns on read-back.
    private const char FieldSep = '\u001f';

    public static LogEntry Project_(string project, EventType type, string message) =>
        new(DateTimeOffset.UtcNow, project, null, type, message);

    public static LogEntry Task_(string project, long task, EventType type, string message) =>
        new(DateTimeOffset.UtcNow, project, task, type, message);

    /// <summary>
    /// The stored form — one entry per line, six columns:
    ///   timestamp | project | task | domain | action | message
    /// domain and action are rendered from the single EventType, so they cannot
    /// disagree; read-back reassembles the enum with EventTypes.FromColumns.
    /// </summary>
    public string Serialize() => string.Join(FieldSep,
        Timestamp.ToString("o"),
        Project,
        Task?.ToString() ?? "",
        Type.Domain(),
        Type.Action(),
        OneLine(Message));

    public static LogEntry Deserialize(string line)
    {
        var parts = line.Split(FieldSep);
        if (parts.Length != 6)
            throw new FormatException($"Log line has {parts.Length} fields, expected 6.");
        return new LogEntry(
            DateTimeOffset.Parse(parts[0]),
            parts[1],
            parts[2].Length == 0 ? null : long.Parse(parts[2]),
            EventTypes.FromColumns(parts[3], parts[4]),
            parts[5]);
    }

    /// <summary>Human-readable rendering for the console — the columns as a person reads them.</summary>
    public string Display() =>
        $"{Timestamp:HH:mm:ss}  {Project,-10}  {Task?.ToString() ?? "-",4}  " +
        $"{Type.Domain(),-10}  {Type.Action(),-16}  {Message}";

    // A log entry is one line; fold any newlines in tool output into spaces.
    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
