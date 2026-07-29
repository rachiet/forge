using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Llm;

/// <summary>
/// Which provider Forge talks to, and optionally which model it uses for each tier.
///
/// Lives at <c>&lt;data root&gt;/llm.json</c> and is entirely optional: with no file
/// at all, the default provider's built-in tier map applies and Forge runs. Overrides
/// are per tier, so naming one model does not oblige you to name the other two.
///
/// <code>
/// { "provider": "anthropic",
///   "models": { "reasoning": "claude-opus-4-8", "coding": "claude-sonnet-5" } }
/// </code>
/// </summary>
public sealed record LlmConfig
{
    public const string ProviderEnvVar = "FORGE_LLM_PROVIDER";

    public string Provider { get; init; } = "anthropic";

    /// <summary>Tier name (lower-case) → model id. Absent tiers fall back to the provider default.</summary>
    public Dictionary<string, string> Models { get; init; } = [];

    [JsonIgnore]
    public string FileName => "llm.json";

    /// <summary>
    /// Load from the data root, letting the environment win — same precedence rule as
    /// EnvFile, so a one-off `FORGE_LLM_PROVIDER=openai forge run` behaves the way
    /// anyone would expect without editing a file.
    /// </summary>
    public static LlmConfig Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "llm.json");
        var config = new LlmConfig();

        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<LlmConfig>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? config;
            }
            catch (JsonException ex)
            {
                // A malformed config is a real mistake and silently defaulting to
                // Anthropic would hide it — you would only notice from the bill.
                throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
            }
        }

        return Environment.GetEnvironmentVariable(ProviderEnvVar) is { Length: > 0 } provider
            ? config with { Provider = provider }
            : config;
    }

    /// <summary>The configured override for a tier, if the operator named one.</summary>
    public string? Override(ModelTier tier) =>
        Models.TryGetValue(tier.ToString().ToLowerInvariant(), out var model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : null;
}
