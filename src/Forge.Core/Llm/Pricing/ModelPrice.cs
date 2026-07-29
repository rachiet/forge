namespace Forge.Core.Llm.Pricing;

/// <summary>
/// What one model costs, in USD per token, split by the four buckets a provider
/// reports separately. Rates are <c>decimal</c> throughout: these are money, and
/// binary floating point has no business accumulating a bill across thousands of
/// calls.
///
/// The cache rates are nullable on purpose. Most models have no prompt cache at
/// all, and a missing rate is not the same as a rate of zero — charging zero for
/// tokens we did send is the silent-undercount failure this whole change exists to
/// remove. A null rate is therefore only an error if the call actually used that
/// bucket, which <see cref="CostOf"/> enforces.
/// </summary>
public sealed record ModelPrice(
    string Model,
    decimal InputPerToken,
    decimal OutputPerToken,
    decimal? CacheReadPerToken = null,
    decimal? CacheWritePerToken = null)
{
    public decimal CostOf(LlmUsage usage) =>
        usage.TokensIn * InputPerToken
        + usage.TokensOut * OutputPerToken
        + usage.CacheReadTokens * Rate(CacheReadPerToken, usage.CacheReadTokens, "cache read")
        + usage.CacheWriteTokens * Rate(CacheWritePerToken, usage.CacheWriteTokens, "cache write");

    /// <summary>
    /// A bucket we have no rate for is fine while it stays empty, and fatal the
    /// moment it doesn't — guessing a multiplier here is exactly how a price table
    /// goes quietly wrong.
    /// </summary>
    private decimal Rate(decimal? rate, int tokens, string bucket)
    {
        if (tokens == 0) return 0m;
        return rate ?? throw new ModelNotPricedException(
            $"{Model} used {tokens} {bucket} tokens but the price table lists no " +
            $"{bucket} rate for it. Refusing to price the call rather than undercount it.");
    }
}

/// <summary>
/// Raised instead of returning a zero cost. With a dollar budget, an unpriced model
/// that costs $0 is a cap that never trips — the one failure mode a spend limit
/// must not have.
/// </summary>
public sealed class ModelNotPricedException(string message) : InvalidOperationException(message);
