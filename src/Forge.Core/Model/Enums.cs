namespace Forge.Core.Model;

// These enums mirror the CHECK constraints in Db/Schema.cs — keep both layers in sync.

// Feature is the PM's unit of scope handed to the Principal — the whole initial
// build, or one change request — and is never assigned to an engineer. The
// Principal decomposes a Feature into Task/Bug/Chore units that engineers execute.
public enum TaskType { Feature, Task, Bug, Chore }

public enum TaskStatus
{
    Created, Ready, Claimed, InProgress, InReview, Merging,
    Qa, Done, Blocked, OutOfBudget,
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

public enum AgentRole { Pm, Principal, Engineer, Qa, Researcher }

public enum MessageType
{
    Question, Answer, Review, Decision, Escalation, Status, ChangeRequest, SystemNudge
}

public enum MessageStatus { Pending, Received }

public enum EndReason { Done, Budget, Iterations, Crash, Escalated }

/// <summary>PascalCase enum member ⇄ snake_case TEXT, as stored under the CHECK constraints.</summary>
public static class SnakeCaseEnum
{
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
