namespace Forge.Core.Model;

// These enums mirror the CHECK constraints in Db/Schema.cs — keep both layers in sync.

// Feature is the PM's unit of scope handed to the Principal — the whole initial
// build, or one change request — and is never assigned to an engineer. The
// Principal decomposes a Feature into Task/Bug/Chore units that engineers execute.
/// <summary>What kind of work a task row holds.</summary>
public enum TaskType { Feature, Task, Bug, Chore }

/// <summary>Where a task sits on the board. Which role owns each is in TaskTransitions.</summary>
public enum TaskStatus
{
    Created, Ready, Claimed, InProgress, InReview, Merging,
    Qa, Done,
    // The Principal's queue. A task lands here whether the harness stopped it (budget or
    // turn cap) or the agent gave up (`escalate`) — both mean the same thing to whoever
    // picks it up next, and stall_count decides which rung of the ladder it gets.
    Stalled,
    // Bug lifecycle: a QA-filed bug lands in Triage (the Principal decides), then
    // becomes Ready (accepted → an engineer fixes it) or Rejected (kept, with the
    // reason, as a durable "not a bug" verdict QA must not re-file).
    Triage, Rejected,
    // Feature lifecycle: a PM-opened Feature is born Triage (the Principal decomposes
    // it), then Active once its child tasks exist — Active means "decomposed, children
    // building; no queue re-claims it" — and Done when the harness sees every child
    // reach a terminal state, which is what arms QA.
    Active,
    // Parked on the client: the Principal exhausted its options, so the PM asks the
    // client what to do and resolves it back to Triage (guidance) or Cancelled (drop).
    NeedsHuman,
    Cancelled
}

/// <summary>The roles an agent instance can run as.</summary>
public enum AgentRole { Pm, Principal, Engineer, Qa, Researcher }

/// <summary>What a message is for; each has a sealed subtype of Message.</summary>
public enum MessageType
{
    Question, Answer, Review, Decision, Escalation, Status, ChangeRequest, SystemNudge
}

/// <summary>Whether a message has been delivered to its recipient yet.</summary>
public enum MessageStatus { Pending, Received }

/// <summary>Why an agent instance stopped.</summary>
public enum EndReason { Done, Budget, Iterations, Crash, Escalated }

/// <summary>Converts an enum member to and from the snake_case TEXT the schema stores.</summary>
public static class SnakeCaseEnum
{
    /// <summary>The snake_case text the schema stores for an enum member.</summary>
    public static string ToSnakeCase<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>The enum member for a stored snake_case value; throws on an unknown one.</summary>
    public static T Parse<T>(string text) where T : struct, Enum
    {
        var candidate = text.Replace("_", "");
        if (Enum.TryParse<T>(candidate, ignoreCase: true, out var value) &&
            ToSnakeCase(value) == text)
        {
            return value;
        }
        throw new FormatException($"'{text}' is not a valid {typeof(T).Name}.");
    }
}
