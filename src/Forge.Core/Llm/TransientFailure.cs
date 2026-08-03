using System.Net;

namespace Forge.Core.Llm;

/// <summary>Which HTTP failures are worth trying again.</summary>
/// <remarks>
/// Shared by the adapters that speak HTTP directly. Auth and request errors are
/// excluded deliberately: retrying a 401 only delays discovering a bad key, and
/// retrying a 400 or 404 repeats a request the provider will never accept.
/// 429 is included because it is usually a rate limit, though a provider may also
/// use it for a spent account — the attempt ceiling is what bounds that case.
/// </remarks>
public static class TransientFailure
{
    public static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.RequestTimeout or            // 408
        HttpStatusCode.TooManyRequests or           // 429
        HttpStatusCode.InternalServerError or       // 500
        HttpStatusCode.BadGateway or                // 502
        HttpStatusCode.ServiceUnavailable or        // 503
        HttpStatusCode.GatewayTimeout;              // 504

    /// <summary>The wait the provider asked for in Retry-After, if it sent one.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
}
