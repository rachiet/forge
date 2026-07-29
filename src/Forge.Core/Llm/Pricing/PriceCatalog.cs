using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Core.Logging;

namespace Forge.Core.Llm.Pricing;

/// <summary>
/// The cached price table, wrapped in our own envelope rather than trusted to the
/// file's mtime — a FORGE_HOME gets copied, restored from backup and moved between
/// machines, and every one of those resets mtime while leaving the prices as stale
/// as they were. <c>Models</c> holds the upstream payload verbatim so the snapshot
/// stays diffable against the source.
/// </summary>
public sealed record PriceSnapshot
{
    public required string Source { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public string? ETag { get; init; }
    public required Dictionary<string, JsonElement> Models { get; init; }

    /// <summary>Identifies which snapshot priced a ledger row (the `priced_with` column).</summary>
    [JsonIgnore]
    public string Id => ETag is { Length: > 0 } tag ? tag.Trim('"', 'W', '/') : $"{FetchedAt:yyyy-MM-ddTHH:mm:ssZ}";
}

/// <summary>
/// Model prices, resolved once and cached: in memory for the process, on disk for
/// the machine.
///
/// Prices are a property of the world, not of a project, so the disk copy lives at
/// the data root and is shared by every project — a copy per project.db would drift
/// five ways on a machine with five projects.
///
/// The in-memory copy is deliberately loaded once and held for the life of the
/// process. A `forge run --loop` can run for hours, and letting rates change
/// underneath it would leave a single project's ledger internally inconsistent —
/// worse than being a few hours out of date.
/// </summary>
public sealed class PriceCatalog
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _cachePath;
    private readonly IPriceFeed _feed;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ForgeLogger _log;

    private PriceSnapshot? _snapshot;
    private bool _refreshedOnMiss;

    public PriceCatalog(
        string cachePath,
        IPriceFeed? feed = null,
        TimeSpan? ttl = null,
        Func<DateTimeOffset>? clock = null,
        ForgeLogger? logger = null)
    {
        _cachePath = cachePath;
        _feed = feed ?? new LiteLlmPriceFeed();
        _ttl = ttl ?? DefaultTtl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = logger ?? ForgeLogger.Null;
    }

    /// <summary>The snapshot backing the current prices; loads it if that has not happened yet.</summary>
    public PriceSnapshot Snapshot => _snapshot ??= Load();

    /// <summary>
    /// The rate card for one model id, as the provider names it.
    ///
    /// A miss forces one refresh before it is allowed to fail: the likeliest reason
    /// a cached table lacks a model is that the model is newer than the table, and
    /// that case should heal itself rather than block a run. A miss that survives a
    /// fresh fetch is a real one, and throwing beats returning zero — an unpriced
    /// model at $0 is a budget that never trips.
    /// </summary>
    public ModelPrice For(string model)
    {
        if (TryRead(Snapshot, model) is { } price) return price;

        if (!_refreshedOnMiss)
        {
            _refreshedOnMiss = true;
            _log.Message($"Model '{model}' is absent from the {Age().TotalHours:F0}h-old price " +
                         "snapshot; refreshing before giving up.");
            if (Refresh() is { } refreshed && TryRead(refreshed, model) is { } found) return found;
        }

        throw new ModelNotPricedException(
            $"No price for model '{model}' in {_feed.Source}. Forge meters spend in dollars, so it " +
            "will not run a model it cannot price. Check the model id in the agent recipe.");
    }

    public TimeSpan Age() => _clock() - Snapshot.FetchedAt;

    public bool IsStale() => Age() > _ttl;

    /// <summary>Force a fetch regardless of TTL (`forge prices update`).</summary>
    public PriceSnapshot Update() => Refresh() ?? Snapshot;

    private PriceSnapshot Load()
    {
        var cached = ReadCache();

        if (cached is not null && _clock() - cached.FetchedAt <= _ttl)
            return cached;

        _snapshot = cached; // so a refresh can send the stored ETag
        if (Refresh() is { } refreshed) return refreshed;

        // A refresh that fails over a table we already have is not worth halting a
        // build for: rates move rarely, and a day-old rate is approximately right.
        // The genuinely dangerous staleness — a model missing entirely — is caught
        // in For(), which refetches before it refuses.
        if (cached is not null)
        {
            _log.Message($"Price refresh failed; continuing on the cached snapshot " +
                         $"({(_clock() - cached.FetchedAt).TotalHours:F0}h old).");
            return cached;
        }

        throw new ModelNotPricedException(
            $"No price data: the cache at {_cachePath} is empty and {_feed.Source} could not be reached.");
    }

    private PriceSnapshot? Refresh()
    {
        try
        {
            var result = _feed.FetchAsync(_snapshot?.ETag).GetAwaiter().GetResult();

            // 304: the table is identical, so only the freshness stamp moves — that is
            // what stops an unchanged file from being re-fetched on every single run.
            if (result.NotModified && _snapshot is { } unchanged)
                return _snapshot = Persist(unchanged with { FetchedAt = _clock() });

            if (result.Payload is not { Length: > 0 } payload) return null;

            return _snapshot = Persist(new PriceSnapshot
            {
                Source = _feed.Source,
                FetchedAt = _clock(),
                ETag = result.ETag,
                Models = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload) ?? [],
            });
        }
        catch (Exception ex)
        {
            _log.Event(EventType.ErrorProvider, $"price refresh failed: {ex.Message}");
            return null;
        }
    }

    private PriceSnapshot? ReadCache()
    {
        try
        {
            return File.Exists(_cachePath)
                ? JsonSerializer.Deserialize<PriceSnapshot>(File.ReadAllText(_cachePath))
                : null;
        }
        catch (Exception ex)
        {
            // A corrupt cache is a cache miss, never a crash.
            _log.Message($"Ignoring unreadable price cache at {_cachePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Written through a temp file and renamed into place: `forge run` has no
    /// concurrency guard, so two processes can reach the refresh path together and
    /// a half-written table must never be observable.
    /// </summary>
    private PriceSnapshot Persist(PriceSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = $"{_cachePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Failing to cache is a performance problem, not a correctness one.
            _log.Message($"Could not write the price cache at {_cachePath}: {ex.Message}");
        }
        return snapshot;
    }

    /// <summary>
    /// LiteLLM states rates as USD per single token, which is why these are read as
    /// decimal from the raw text: the values are things like 3e-06, and a double
    /// round-trip would bake a representation error into every row of the ledger.
    /// </summary>
    private static ModelPrice? TryRead(PriceSnapshot snapshot, string model)
    {
        if (!snapshot.Models.TryGetValue(model, out var entry) || entry.ValueKind != JsonValueKind.Object)
            return null;

        var input = Rate(entry, "input_cost_per_token");
        var output = Rate(entry, "output_cost_per_token");
        if (input is null || output is null) return null;

        return new ModelPrice(
            model,
            input.Value,
            output.Value,
            Rate(entry, "cache_read_input_token_cost"),
            Rate(entry, "cache_creation_input_token_cost"));
    }

    private static decimal? Rate(JsonElement entry, string field) =>
        entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            && decimal.TryParse(value.GetRawText(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? rate
            : null;
}
