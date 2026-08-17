using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;

namespace Forge.Tests;

/// <summary>
/// A price catalogue for tests, backed by a scripted feed rather than the network.
/// Rates are the real shape (USD per single token) so cost assertions elsewhere read
/// like real money, and every model a test double can name is present — an unpriced
/// model throws by design, and that must stay a deliberate test, never an accident.
/// </summary>
internal static class TestPrices
{
    /// <summary>What the test doubles report from ModelFor.</summary>
    public const string Model = "claude-sonnet-5";

    private static readonly Lazy<PriceCatalog> Instance = new(() => new PriceCatalog(
        Path.Combine(Path.GetTempPath(), "forge-tests", $"prices-{Guid.NewGuid():N}.json"),
        new FakePriceFeed(FakePriceFeed.Table(
            ("claude-sonnet-5", 2e-6, 1e-5, 2e-7, 2.5e-6),
            ("claude-opus-4-8", 5e-6, 25e-6, 5e-7, 6.25e-6)))));

    public static PriceCatalog Catalog => Instance.Value;

    /// <summary>The tier map a test double answers with, matching the Anthropic defaults.</summary>
    public static string For(ModelTier tier) => tier switch
    {
        ModelTier.Reasoning => "claude-opus-4-8",
        _ => Model,
    };
}
