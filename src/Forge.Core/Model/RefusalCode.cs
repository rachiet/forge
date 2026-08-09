namespace Forge.Core.Model;

/// <summary>
/// The codes stamped on the refusals an agent can provoke. Each one prefixes the message the
/// model reads, so it also lands in the refusal line the toolset logs — counting a code across
/// projects is how often that mistake is made.
/// </summary>
public static class RefusalCode
{
    /// <summary>A dependency edge from a task to itself.</summary>
    public const string SelfDependency = "DEP_SELF";

    /// <summary>A dependency edge that would close a cycle in the task DAG.</summary>
    public const string DependencyCycle = "DEP_CYCLE";

    /// <summary>A dependency edge from a task to the Feature it belongs to.</summary>
    public const string DependsOnFeature = "DEP_ON_FEATURE";

    /// <summary>A dependency edge onto a task id that does not exist.</summary>
    public const string NoSuchTask = "DEP_NO_SUCH_TASK";

    /// <summary>Renders a refusal message under its code.</summary>
    public static string Message(string code, string text) => $"[{code}] {text}";
}
