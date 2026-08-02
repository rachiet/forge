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
}
