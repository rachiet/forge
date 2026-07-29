using System.Text.Json;
using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;

namespace Forge.Tests;

/// <summary>
/// A feed that answers from a script and counts its calls, so the caching and TTL
/// rules can be asserted without a network round trip.
/// </summary>
public sealed class FakePriceFeed(string? payload = null) : IPriceFeed
{
    public string Source => "test://prices";
    public int Fetches { get; private set; }
    public string? LastETagSent { get; private set; }
    public string? Payload { get; set; } = payload;
    public string? ETag { get; set; } = "\"v1\"";
    public bool Fails { get; set; }
    public bool AnswerNotModified { get; set; }

    public Task<FeedResult> FetchAsync(string? etag, CancellationToken ct = default)
    {
        Fetches++;
        LastETagSent = etag;
        if (Fails) throw new HttpRequestException("network down");
        return Task.FromResult(AnswerNotModified
            ? FeedResult.Unchanged(etag)
            : FeedResult.Fresh(Payload ?? "{}", ETag));
    }

    public static string Table(params (string Model, double In, double Out, double? Read, double? Write)[] models)
    {
        var d = models.ToDictionary(m => m.Model, m =>
        {
            var e = new Dictionary<string, object> { ["input_cost_per_token"] = m.In, ["output_cost_per_token"] = m.Out };
            if (m.Read is { } r) e["cache_read_input_token_cost"] = r;
            if (m.Write is { } w) e["cache_creation_input_token_cost"] = w;
            return e;
        });
        return JsonSerializer.Serialize(d);
    }
}

public class PriceCatalogTests : IDisposable
{
    private readonly string _cache = Path.Combine(
        Path.GetTempPath(), $"forge-prices-{Guid.NewGuid():N}", "litellm.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_cache)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static readonly string SonnetTable = FakePriceFeed.Table(
        ("claude-sonnet-5", 2e-6, 1e-5, 2e-7, 2.5e-6));

    private PriceCatalog Catalog(FakePriceFeed feed, TimeSpan? ttl = null, Func<DateTimeOffset>? clock = null) =>
        new(_cache, feed, ttl, clock);

    [Fact]
    public void Rates_come_back_per_bucket_and_cost_is_their_sum()
    {
        var price = Catalog(new FakePriceFeed(SonnetTable)).For("claude-sonnet-5");

        Assert.Equal(2e-6m, price.InputPerToken);
        Assert.Equal(1e-5m, price.OutputPerToken);
        Assert.Equal(2e-7m, price.CacheReadPerToken);
        Assert.Equal(2.5e-6m, price.CacheWritePerToken);

        // 1000*2e-6 + 500*1e-5 + 10000*2e-7 + 2000*2.5e-6 = 0.002 + 0.005 + 0.002 + 0.005
        Assert.Equal(0.014m, price.CostOf(new LlmUsage(1000, 500, 10_000, 2000)));
    }

    [Fact]
    public void The_feed_is_hit_once_and_then_served_from_memory()
    {
        var feed = new FakePriceFeed(SonnetTable);
        var catalog = Catalog(feed);

        for (var i = 0; i < 5; i++) catalog.For("claude-sonnet-5");

        Assert.Equal(1, feed.Fetches);
    }

    [Fact]
    public void A_fresh_disk_snapshot_is_used_without_touching_the_network()
    {
        Catalog(new FakePriceFeed(SonnetTable)).For("claude-sonnet-5");   // populates the cache

        var second = new FakePriceFeed(SonnetTable);
        Catalog(second).For("claude-sonnet-5");                            // new process, same disk

        Assert.Equal(0, second.Fetches);
    }

    [Fact]
    public void A_snapshot_past_its_ttl_is_refreshed()
    {
        var now = DateTimeOffset.Parse("2026-07-28T10:00:00Z");
        Catalog(new FakePriceFeed(SonnetTable), clock: () => now).For("claude-sonnet-5");

        var feed = new FakePriceFeed(SonnetTable);
        var later = now.AddDays(1).AddMinutes(1);
        var catalog = Catalog(feed, ttl: TimeSpan.FromDays(1), clock: () => later);
        catalog.For("claude-sonnet-5");

        Assert.Equal(1, feed.Fetches);
        Assert.Equal("\"v1\"", feed.LastETagSent);   // conditional GET, not a blind re-download
        Assert.False(catalog.IsStale());
    }

    [Fact]
    public void A_304_keeps_the_table_and_only_moves_the_freshness_stamp()
    {
        var now = DateTimeOffset.Parse("2026-07-28T10:00:00Z");
        Catalog(new FakePriceFeed(SonnetTable), clock: () => now).For("claude-sonnet-5");

        var feed = new FakePriceFeed(SonnetTable) { AnswerNotModified = true };
        var later = now.AddDays(2);
        var catalog = Catalog(feed, ttl: TimeSpan.FromDays(1), clock: () => later);

        Assert.Equal(2e-6m, catalog.For("claude-sonnet-5").InputPerToken);
        Assert.Equal(later, catalog.Snapshot.FetchedAt);
        Assert.False(catalog.IsStale());
    }

    [Fact]
    public void A_failed_refresh_falls_back_to_the_stale_snapshot_rather_than_halting()
    {
        var now = DateTimeOffset.Parse("2026-07-28T10:00:00Z");
        Catalog(new FakePriceFeed(SonnetTable), clock: () => now).For("claude-sonnet-5");

        var feed = new FakePriceFeed { Fails = true };
        var catalog = Catalog(feed, ttl: TimeSpan.FromDays(1), clock: () => now.AddDays(30));

        Assert.Equal(2e-6m, catalog.For("claude-sonnet-5").InputPerToken);
        Assert.True(catalog.IsStale());
    }

    [Fact]
    public void An_unknown_model_forces_one_refresh_before_it_is_allowed_to_fail()
    {
        var now = DateTimeOffset.Parse("2026-07-28T10:00:00Z");
        Catalog(new FakePriceFeed(SonnetTable), clock: () => now).For("claude-sonnet-5");

        // The model exists upstream but not in the cached table — the everyday
        // consequence of a TTL, and it should heal itself.
        var feed = new FakePriceFeed(FakePriceFeed.Table(
            ("claude-sonnet-5", 2e-6, 1e-5, 2e-7, 2.5e-6),
            ("claude-opus-9", 5e-6, 25e-6, 5e-7, 6.25e-6)));
        var catalog = Catalog(feed, clock: () => now);

        Assert.Equal(5e-6m, catalog.For("claude-opus-9").InputPerToken);
        Assert.Equal(1, feed.Fetches);
    }

    [Fact]
    public void A_model_missing_even_after_a_refresh_throws_rather_than_costing_nothing()
    {
        var catalog = Catalog(new FakePriceFeed(SonnetTable));

        var ex = Assert.Throws<ModelNotPricedException>(() => catalog.For("gpt-not-real"));
        Assert.Contains("gpt-not-real", ex.Message);
    }

    [Fact]
    public void The_miss_refresh_happens_once_and_is_not_retried_per_lookup()
    {
        var feed = new FakePriceFeed(SonnetTable);
        var catalog = Catalog(feed);

        for (var i = 0; i < 4; i++)
            Assert.Throws<ModelNotPricedException>(() => catalog.For("gpt-not-real"));

        Assert.Equal(2, feed.Fetches);   // the initial load, plus exactly one miss-refresh
    }

    [Fact]
    public void Cache_tokens_with_no_rate_throw_instead_of_being_counted_free()
    {
        // A model priced for input/output but with no cache rates at all.
        var catalog = Catalog(new FakePriceFeed(FakePriceFeed.Table(("bare-model", 1e-6, 2e-6, null, null))));
        var price = catalog.For("bare-model");

        Assert.Equal(0.003m, price.CostOf(new LlmUsage(1000, 1000)));           // no cache tokens: fine
        Assert.Throws<ModelNotPricedException>(() => price.CostOf(new LlmUsage(1000, 1000, 5000)));
    }

    [Fact]
    public void A_corrupt_cache_is_treated_as_a_miss_not_a_crash()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
        File.WriteAllText(_cache, "{ this is not json");

        var feed = new FakePriceFeed(SonnetTable);
        Assert.Equal(2e-6m, Catalog(feed).For("claude-sonnet-5").InputPerToken);
        Assert.Equal(1, feed.Fetches);
    }

    [Fact]
    public void With_no_cache_and_no_network_it_refuses_rather_than_pricing_at_zero()
    {
        var catalog = Catalog(new FakePriceFeed { Fails = true });
        Assert.Throws<ModelNotPricedException>(() => catalog.For("claude-sonnet-5"));
    }
}
