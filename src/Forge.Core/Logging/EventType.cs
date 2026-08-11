namespace Forge.Core.Logging;

/// <summary>
/// Every event that can be logged, written as <c>domain.action</c> — <c>tool.write_file</c>,
/// <c>git.merge</c>. An enum rather than free strings, so no call site can invent a spelling
/// and fragment the log.
/// </summary>
public enum EventType
{
    // lifecycle — the skeleton of a task's life
    TaskCreated,
    TaskTransition,
    InstanceStart,
    InstanceEnd,

    // llm — every model interaction the supervisor mediates
    LlmCall,
    LlmNudge,
    LlmRefused,
    LlmNoToolCall,
    LlmError,

    // tool — what an agent actually did, one per tool
    ToolListDir,
    ToolReadFile,
    ToolGrep,
    ToolWriteFile,
    ToolRun,

    /// <summary>An agent parsed the repo's served .js, .json and .html files.</summary>
    ToolCheckStatic,
    ToolServe,
    ToolStopServer,
    ToolHttp,
    ToolCreateTask,
    ToolAddDependency,
    ToolChooseTheme,
    ToolApprove,
    ToolRequestChanges,
    ToolReply,
    ToolProgressNote,
    ToolDone,
    ToolEscalate,
    ToolRefused,

    // ci — harness-run build/test, zero tokens
    CiRun,
    CiPassed,
    CiFailed,

    // review — the Principal's verdict on a diff
    ReviewApproved,
    ReviewChangesRequested,

    // git — harness-side repository truth
    GitBranch,
    GitCommit,
    GitMerge,
    GitPush,

    // message — the free-form, human-readable channel: agent↔client communication
    // AND ordinary service/debug lines the code emits ("creating util file X").
    // The one eventType you read rather than skip; single-token, not domain.action,
    // because it is a general log line, not a typed mechanical event.
    Message,

    // error — failures worth a line of their own
    ErrorProvider,
    ErrorInternal,
}

/// <summary>Wire rendering for <see cref="EventType"/>, and back again.</summary>
public static class EventTypes
{
    /// <summary>Each event's `domain.action` text.</summary>
    private static readonly IReadOnlyDictionary<EventType, string> ToWire =
        new Dictionary<EventType, string>
        {
            [EventType.TaskCreated] = "lifecycle.task_created",
            [EventType.TaskTransition] = "lifecycle.task_transition",
            [EventType.InstanceStart] = "lifecycle.instance_start",
            [EventType.InstanceEnd] = "lifecycle.instance_end",
            [EventType.LlmCall] = "llm.call",
            [EventType.LlmNudge] = "llm.nudge",
            [EventType.LlmRefused] = "llm.refused",
            [EventType.LlmNoToolCall] = "llm.no_tool_call",
            [EventType.LlmError] = "llm.error",
            [EventType.ToolListDir] = "tool.list_dir",
            [EventType.ToolReadFile] = "tool.read_file",
            [EventType.ToolGrep] = "tool.grep",
            [EventType.ToolWriteFile] = "tool.write_file",
            [EventType.ToolRun] = "tool.run",
            [EventType.ToolCheckStatic] = "tool.check_static",
            [EventType.ToolServe] = "tool.serve",
            [EventType.ToolStopServer] = "tool.stop_server",
            [EventType.ToolHttp] = "tool.http",
            [EventType.ToolCreateTask] = "tool.create_task",
            [EventType.ToolAddDependency] = "tool.add_dependency",
            [EventType.ToolChooseTheme] = "tool.choose_theme",
            [EventType.ToolApprove] = "tool.approve",
            [EventType.ToolRequestChanges] = "tool.request_changes",
            [EventType.ToolReply] = "tool.reply",
            [EventType.ToolProgressNote] = "tool.progress_note",
            [EventType.ToolDone] = "tool.done",
            [EventType.ToolEscalate] = "tool.escalate",
            [EventType.ToolRefused] = "tool.refused",
            [EventType.CiRun] = "ci.run",
            [EventType.CiPassed] = "ci.passed",
            [EventType.CiFailed] = "ci.failed",
            [EventType.ReviewApproved] = "review.approved",
            [EventType.ReviewChangesRequested] = "review.changes_requested",
            [EventType.GitBranch] = "git.branch",
            [EventType.GitCommit] = "git.commit",
            [EventType.GitMerge] = "git.merge",
            [EventType.GitPush] = "git.push",
            [EventType.Message] = "message",
            [EventType.ErrorProvider] = "error.provider",
            [EventType.ErrorInternal] = "error.internal",
        };

    /// <summary>The reverse lookup, built from <see cref="ToWire"/>.</summary>
    private static readonly IReadOnlyDictionary<string, EventType> FromWire =
        ToWire.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    /// <summary>The event's `domain.action` text.</summary>
    public static string Wire(this EventType type) =>
        ToWire.TryGetValue(type, out var wire)
            ? wire
            : throw new ArgumentOutOfRangeException(nameof(type), type, "No wire form — add it to EventTypes.");

    /// <summary>The event for a `domain.action` string; throws on an unknown one.</summary>
    public static EventType Parse(string wire) =>
        FromWire.TryGetValue(wire, out var type)
            ? type
            : throw new FormatException($"'{wire}' is not a known eventType.");

    /// <summary>The part before the dot, or the whole token for the single-level `message`.</summary>
    public static string Domain(this EventType type)
    {
        var wire = type.Wire();
        var dot = wire.IndexOf('.');
        return dot < 0 ? wire : wire[..dot];
    }

    /// <summary>The part after the dot, or empty for `message`, which has no action.</summary>
    public static string Action(this EventType type)
    {
        var wire = type.Wire();
        var dot = wire.IndexOf('.');
        return dot < 0 ? "" : wire[(dot + 1)..];
    }

    /// <summary>Rebuilds the enum from the two stored columns.</summary>
    public static EventType FromColumns(string domain, string action) =>
        Parse(action.Length == 0 ? domain : $"{domain}.{action}");

    /// <summary>The event type for a tool name, so tool calls are never tagged by hand.</summary>
    public static EventType? ForTool(string toolName) => toolName switch
    {
        "list_dir" => EventType.ToolListDir,
        "read_file" => EventType.ToolReadFile,
        "grep" => EventType.ToolGrep,
        "write_file" => EventType.ToolWriteFile,
        "run" => EventType.ToolRun,
        "check_static" => EventType.ToolCheckStatic,
        "serve" => EventType.ToolServe,
        "stop_server" => EventType.ToolStopServer,
        "http" => EventType.ToolHttp,
        "create_task" => EventType.ToolCreateTask,
        "add_dependency" => EventType.ToolAddDependency,
        "choose_theme" => EventType.ToolChooseTheme,
        "approve" => EventType.ToolApprove,
        "request_changes" => EventType.ToolRequestChanges,
        "reply" => EventType.ToolReply,
        "progress_note" => EventType.ToolProgressNote,
        "done" => EventType.ToolDone,
        "escalate" => EventType.ToolEscalate,
        _ => null,
    };
}
