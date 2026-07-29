namespace Forge.Core.Llm;

/// <summary>
/// How much model a job deserves — the vocabulary the spec already used ("cheap/fast
/// coding tier", "high-reasoning tier") made into a type.
///
/// A recipe names a tier, never a model id. That split keeps orchestration policy
/// (an engineer needs the coding tier) separate from provider knowledge (what the
/// coding tier is called at Anthropic today), so adding a role stays a record plus a
/// prompt file, and adding a provider stays one class with three strings in it.
/// Neither side has to learn the other's vocabulary.
/// </summary>
public enum ModelTier
{
    /// <summary>Cheapest and quickest. Mechanical work with a fixed answer.</summary>
    Fast,

    /// <summary>The working tier: writing code, verifying behaviour against a contract.</summary>
    Coding,

    /// <summary>The strongest available. Design, review, and rescuing stuck work.</summary>
    Reasoning,
}

/// <summary>
/// Builds an adapter's tier → model-id map, letting llm.json override any entry.
/// Shared so every provider resolves configuration identically; a provider that
/// rolled its own would be the one place an override silently did nothing.
/// </summary>
internal static class TierMap
{
    public static IReadOnlyDictionary<ModelTier, string> Resolve(
        LlmConfig? config, IReadOnlyDictionary<ModelTier, string> defaults) =>
        Enum.GetValues<ModelTier>().ToDictionary(
            tier => tier,
            tier => config?.Override(tier) ?? defaults[tier]);
}
