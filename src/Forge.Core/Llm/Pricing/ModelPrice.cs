namespace Forge.Core.Llm.Pricing;

/// <summary>
/// What one model costs in USD per token, split by the four buckets a provider reports
/// separately. Rates are decimal, since they are money. The cache rates are nullable: most
/// models have no prompt cache, and a missing rate is not a rate of zero — it is an error only
/// if a call used that bucket, which <see cref="CostOf"/> enforces.
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
    /// What one call's usage costs. Throws when a bucket has tokens but no rate, rather than
    /// guessing a multiplier.
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
/// Thrown when a model or a token bucket has no rate. Raised rather than returning zero, which
/// would be a spend cap that never trips.
/// </summary>
public sealed class ModelNotPricedException(string message) : InvalidOperationException(message);
