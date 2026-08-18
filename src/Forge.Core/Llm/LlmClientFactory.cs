namespace Forge.Core.Llm;

/// <summary>
/// Turns a provider name into a client. This is the one place that knows which adapters
/// exist; everywhere else depends on ILlmClient alone.
///
/// The provider is the client's choice, stored per project, and it is the whole of the
/// configuration: each adapter answers for its own tiers, so choosing a provider chooses
/// its coding and reasoning models too. There is no machine-wide file and no override —
/// a model id that did not come from the adapter that will call it is how a project ends
/// up asking one provider for another's model.
///
/// Adding a provider is: one adapter class implementing ILlmClient (including its tier
/// map), and one case here.
/// </summary>
public static class LlmClientFactory
{
    public static ILlmClient Create(string provider) => provider.ToLowerInvariant() switch
    {
        AnthropicLlmClient.ProviderName or "claude" => new AnthropicLlmClient(),
        OpenAiLlmClient.ProviderName or "gpt" => new OpenAiLlmClient(),
        GeminiLlmClient.ProviderName or "google" => new GeminiLlmClient(),
        var unknown => throw new NotSupportedException(
            $"Unknown LLM provider '{unknown}'. Supported: {string.Join(", ", Supported)}."),
    };

    /// <summary>Provider names Create accepts — also what the board offers and `forge prices` reports.</summary>
    public static IReadOnlyList<string> Supported =>
        [AnthropicLlmClient.ProviderName, OpenAiLlmClient.ProviderName, GeminiLlmClient.ProviderName];

    /// <summary>Whether a name is one of <see cref="Supported"/>, ignoring case. Aliases are not offered.</summary>
    public static bool IsSupported(string? provider) =>
        provider is { Length: > 0 } name && Supported.Contains(name, StringComparer.OrdinalIgnoreCase);
}
