namespace Forge.Core.Model;

/// <summary>
/// One row of the messages table, which is both the queue and the log. Abstract, with a sealed
/// subtype per message type so routing can switch exhaustively. Senders and receivers are role
/// names, "client", or "system".
/// </summary>
public abstract record Message
{
    public long Id { get; init; }
    public required string FromAgent { get; init; }
    public required string ToAgent { get; init; }
    public long? TaskId { get; init; }
    public required string Payload { get; init; }
    public MessageStatus Status { get; init; } = MessageStatus.Pending;
    public string? CreatedAt { get; init; }

    public abstract MessageType Type { get; }

    public static Message Create(
        MessageType type, string fromAgent, string toAgent, string payload,
        long? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(fromAgent))
            throw new ArgumentException("Sender must be non-empty.", nameof(fromAgent));
        if (string.IsNullOrWhiteSpace(toAgent))
            throw new ArgumentException("Receiver must be non-empty.", nameof(toAgent));
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Payload must be non-empty.", nameof(payload));
        // Agent-to-agent messages must name a task. Client chat and the harness's own
        // project-level reports have nothing to anchor to.
        if (taskId is null && fromAgent is not ("client" or "system") && toAgent != "client")
            throw new ArgumentException(
                "Messages must be task-anchored; task_id may be null only for client chat or system notices.",
                nameof(taskId));

        return Construct(type, fromAgent, toAgent, payload) with { TaskId = taskId };
    }

    /// <summary>Rehydrate the right subtype from a stored type discriminator.</summary>
    public static Message FromRow(
        MessageType type, long id, string fromAgent, string toAgent,
        long? taskId, string payload, MessageStatus status, string? createdAt) =>
        Construct(type, fromAgent, toAgent, payload) with
        {
            Id = id,
            TaskId = taskId,
            Status = status,
            CreatedAt = createdAt,
        };

    private static Message Construct(MessageType type, string from, string to, string payload) => type switch
    {
        MessageType.Question => new QuestionMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.Answer => new AnswerMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.Review => new ReviewMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.Decision => new DecisionMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.Escalation => new EscalationMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.Status => new StatusMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.ChangeRequest => new ChangeRequestMessage { FromAgent = from, ToAgent = to, Payload = payload },
        MessageType.SystemNudge => new SystemNudgeMessage { FromAgent = from, ToAgent = to, Payload = payload },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

public sealed record QuestionMessage : Message { public override MessageType Type => MessageType.Question; }
public sealed record AnswerMessage : Message { public override MessageType Type => MessageType.Answer; }
public sealed record ReviewMessage : Message { public override MessageType Type => MessageType.Review; }
public sealed record DecisionMessage : Message { public override MessageType Type => MessageType.Decision; }
public sealed record EscalationMessage : Message { public override MessageType Type => MessageType.Escalation; }
public sealed record StatusMessage : Message { public override MessageType Type => MessageType.Status; }
public sealed record ChangeRequestMessage : Message { public override MessageType Type => MessageType.ChangeRequest; }
public sealed record SystemNudgeMessage : Message { public override MessageType Type => MessageType.SystemNudge; }
