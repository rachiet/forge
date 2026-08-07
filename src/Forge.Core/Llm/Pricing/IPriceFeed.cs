using System.Net;
using System.Net.Http.Headers;

namespace Forge.Core.Llm.Pricing;

/// <summary>What a fetch produced: fresh content, or nothing when it is unchanged.</summary>
public sealed record FeedResult(bool NotModified, string? Payload, string? ETag)
{
    public static FeedResult Unchanged(string? etag) => new(true, null, etag);
    public static FeedResult Fresh(string payload, string? etag) => new(false, payload, etag);
}

/// <summary>Fetches the price table. An interface, so the catalogue can be tested without a socket.</summary>
public interface IPriceFeed
{
    string Source { get; }
    Task<FeedResult> FetchAsync(string? etag, CancellationToken ct = default);
}

/// <summary>
/// Fetches LiteLLM's price table from GitHub. Its keys are provider-native model ids — the
/// same strings the recipes resolve to — so no name mapping stands between a model and its rate.
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
        // A conditional GET, so an unchanged table costs a 304 rather than ~1.7 MB.
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
