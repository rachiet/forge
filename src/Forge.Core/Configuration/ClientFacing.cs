namespace Forge.Core.Configuration;

/// <summary>
/// Names the client sees. Internally the role stays <c>pm</c> — in the schema, the
/// ledger, the logs and every prompt file path — so renaming the agent is a text
/// change here and nowhere else.
/// </summary>
public static class ClientFacing
{
    /// <summary>The agent the client talks to. Substituted into the PM prompt and the board.</summary>
    public const string AgentName = "Iris";

    /// <summary>The placeholder role prompts use to refer to their own client-facing name.</summary>
    public const string AgentNameToken = "{{agent_name}}";

    /// <summary>What the client is told the moment they approve the requirements.</summary>
    /// <remarks>
    /// Written by the harness rather than by a PM turn: approving is the client's one
    /// commitment, and the acknowledgement has to appear immediately and cannot be left
    /// to a call that might be refused or overloaded.
    /// </remarks>
    public const string ApprovalAcknowledgement =
        "Thank you — the requirements are confirmed and the team is starting work now.\n\n"
        + "You'll see the plan and its progress on this page from now on, and the "
        + "specification stays there for the whole build so you can read it back whenever "
        + "you like. I'll come to you if anything needs a decision.\n\n"
        + "If you want to change something, just message me here and I'll raise it as a "
        + "change request.";
}
