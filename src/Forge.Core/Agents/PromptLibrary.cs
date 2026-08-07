using Forge.Core.Configuration;
using Forge.Core.Model;

namespace Forge.Core.Agents;

/// <summary>
/// Reads prompt layers A and B off disk. Prompts are files in the Forge source
/// repo, never rows in the tasks table: fixing a template improves every future
/// task instead of only the next one.
/// </summary>
public sealed class PromptLibrary(string root)
{
    public const string EnvVar = "FORGE_PROMPTS";

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>FORGE_PROMPTS if set, else the prompts/ copied next to the binary.</summary>
    public static PromptLibrary Resolve() =>
        new(Environment.GetEnvironmentVariable(EnvVar)
            ?? Path.Combine(AppContext.BaseDirectory, "prompts"));

    /// <summary>Layer A — role identity.</summary>
    public string Role(string name) =>
        Read(Path.Combine(Root, "roles", $"{name}.md"))
            .Replace(ClientFacing.AgentNameToken, ClientFacing.AgentName, StringComparison.Ordinal);

    /// <summary>Layer B — task-type instructions.</summary>
    public string TaskType(TaskType type) =>
        Read(Path.Combine(Root, "tasks", $"{SnakeCaseEnum.ToSnakeCase(type)}.md"));

    /// <summary>
    /// A file Forge writes into the client's repo rather than into a prompt — currently
    /// the base CONVENTIONS.md every project starts from. It lives here for the same
    /// reason the prompts do: editing one versioned file improves every future project,
    /// where a copy authored per project drifts and re-learns what the last one paid for.
    /// </summary>
    public string Template(string name) => Read(Path.Combine(Root, "templates", name));

    private static string Read(string path) =>
        File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException(
                $"Prompt template '{path}' not found. Set {EnvVar} to the prompts/ directory.", path);
}
