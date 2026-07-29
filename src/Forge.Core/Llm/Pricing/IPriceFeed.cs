using System.Net;
using System.Net.Http.Headers;

namespace Forge.Core.Llm.Pricing;

/// <summary>What a fetch produced: fresh bytes, or "you already have it" (HTTP 304).</summary>
public sealed record FeedResult(bool NotModified, string? Payload, string? ETag)
{
    public static FeedResult Unchanged(string? etag) => new(true, null, etag);
    public static FeedResult Fresh(string payload, string? etag) => new(false, payload, etag);
}

/// <summary>
/// The network edge of pricing, kept behind an interface so the catalogue's cache
/// and TTL logic can be tested without a socket — the same reason ILlmClient exists.
/// </summary>
public interface IPriceFeed
{
    string Source { get; }
    Task<FeedResult> FetchAsync(string? etag, CancellationToken ct = default);
}

/// <summary>
/// LiteLLM's price table, served as a plain file from GitHub. Chosen over a live
/// pricing API because its keys are provider-native model ids — the exact strings
/// AgentRecipe already carries — so no name mapping stands between a recipe and
/// its rate, and a mapping that guesses wrong would silently mis-bill.
/// </summary>
public sealed class LiteLlmPriceFeed(HttpClient? http = null) : IPriceFeed
{
    public const string Url =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    public string Source => Url;

    public async Task<FeedResult> FetchAsync(string? etag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        // A conditional GET turns the common "TTL expired but nothing changed" case
        // into a 304 with no body, instead of re-downloading ~1.7 MB every day.
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return FeedResult.Unchanged(etag);

        response.EnsureSuccessStatusCode();
        return FeedResult.Fresh(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false),
            response.Headers.ETag?.Tag);
    }
}
