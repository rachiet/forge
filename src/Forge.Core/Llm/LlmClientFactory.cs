namespace Forge.Core.Llm;

/// <summary>
/// Turns the configured provider name into a client. This is the one place that
/// knows which adapters exist; everywhere else depends on ILlmClient alone, so
/// switching Forge from Claude to another provider is an edit to llm.json rather
/// than a code change.
///
/// Adding a provider is: one adapter class implementing ILlmClient (including its
/// three-entry tier map), and one case here.
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient Create(LlmConfig config) => config.Provider.ToLowerInvariant() switch
    {
        AnthropicLlmClient.ProviderName or "claude" => new AnthropicLlmClient(config: config),
        OpenAiLlmClient.ProviderName or "gpt" => new OpenAiLlmClient(config: config),
        GeminiLlmClient.ProviderName or "google" => new GeminiLlmClient(config: config),
        var unknown => throw new NotSupportedException(
            $"Unknown LLM provider '{unknown}'. Supported: {string.Join(", ", Supported)}."),
    };

    /// <summary>Provider names Create accepts — also what `forge prices show` reports.</summary>
    public static IReadOnlyList<string> Supported =>
        [AnthropicLlmClient.ProviderName, OpenAiLlmClient.ProviderName, GeminiLlmClient.ProviderName];
}
