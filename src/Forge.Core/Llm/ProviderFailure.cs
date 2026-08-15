using System.Text.RegularExpressions;

namespace Forge.Core.Llm;

/// <summary>
/// Turns a provider's rejection into one sentence a client can act on, and keeps the provider's
/// own words as the detail behind it. Written here rather than by an agent because the call that
/// failed is the call that would have composed the message.
///
/// The same condition carries a different code at each provider, so the status alone is not
/// enough: a rejected key is 401 at Anthropic and OpenAI but 403 at Gemini, and a spent balance
/// is 402 at Anthropic, 429 `insufficient_quota` at OpenAI, and 429 `quota_exceeded` or 400
/// `failed_precondition` at Gemini. The payload's own error string decides where they overlap.
/// </summary>
public static partial class ProviderFailure
{
    /// <summary>
    /// What to tell the client about a failed call, or null when the text carries no status this
    /// recognises — the caller then falls back to the raw detail.
    /// </summary>
    /// <param name="detail">
    /// The adapter's message, e.g. `Gemini returned 429: {"error":{"status":"RESOURCE_EXHAUSTED"…`.
    /// Its shape is fixed by the adapters, which write it in one place each.
    /// </param>
    public static string? Describe(string? detail)
    {
        if (detail is not { Length: > 0 }) return null;
        if (StatusPattern().Match(detail) is not { Success: true } match) return null;

        var provider = match.Groups["provider"].Value;
        var status = int.Parse(match.Groups["status"].Value);
        var says = detail.ToLowerInvariant();

        // Ordered by what the client must do about it, not by code: money first, then
        // credentials, then the provider's own health. Each ends by asking the client to reply,
        // because a PM turn is what puts a worker back on the board — there is no button for it.
        if (OutOfCredit(status, says))
            return $"I've had to stop: {provider} says the account has no credit left. "
                 + "Top it up and tell me here, and I'll pick up where we left off. Nothing is lost.";

        if (KeyRejected(status, says))
            return $"I've had to stop: {provider} did not accept the API key. Check the key in "
                 + "~/forge_env is correct and valid, then tell me here and I'll carry on.";

        if (status is 403)
            return $"I've had to stop: {provider} accepted the key but will not let this account "
                 + "use the model. Once the account has access, tell me here and I'll carry on.";

        if (status is 404)
            return $"I've had to stop: {provider} no longer offers the model this project runs on. "
                 + "It needs configuring before I can continue. Tell me once it is sorted.";

        if (RateLimited(status, says))
            return $"I've had to stop: {provider} is rate-limiting the account. It usually clears "
                 + "on its own, so message me in a few minutes and I'll carry on.";

        if (status is 503 or 529 or 500 or 502 or 504)
            return $"I've had to stop: {provider} is having trouble at their end. Nothing is lost. "
                 + "Message me shortly and I'll pick up where we left off.";

        if (status is 400 or 413 or 422)
            return $"I've had to stop: {provider} rejected the request. That is a fault in Forge "
                 + "rather than anything you can fix from here.";

        return $"I've had to stop: {provider} returned {status}.";
    }

    /// <summary>
    /// A spent balance. Anthropic gives it a code of its own; OpenAI and Gemini fold it into 429
    /// or 400, so the payload's wording is what separates it from a rate limit.
    /// </summary>
    private static bool OutOfCredit(int status, string says) =>
        status is 402
        || says.Contains("insufficient_quota", StringComparison.Ordinal)
        || says.Contains("quota_exceeded", StringComparison.Ordinal)
        || says.Contains("failed_precondition", StringComparison.Ordinal)
        || says.Contains("credits are depleted", StringComparison.Ordinal)
        || says.Contains("billing", StringComparison.Ordinal)
        || says.Contains("spending cap", StringComparison.Ordinal)
        || says.Contains("spend cap", StringComparison.Ordinal);

    /// <summary>A key the provider will not accept: 401 at Anthropic and OpenAI, 403 at Gemini.</summary>
    private static bool KeyRejected(int status, string says) =>
        status is 401
        || (status is 403 && (says.Contains("api key", StringComparison.Ordinal)
                           || says.Contains("api_key", StringComparison.Ordinal)));

    /// <summary>Too many requests, as opposed to no money — both arrive as 429 at two providers.</summary>
    private static bool RateLimited(int status, string says) =>
        status is 429
        && (says.Contains("rate", StringComparison.Ordinal)
         || says.Contains("resource_exhausted", StringComparison.Ordinal)
         || !says.Contains("quota", StringComparison.Ordinal));

    /// <summary>Matches the `Provider returned NNN` prefix every adapter writes.</summary>
    [GeneratedRegex(@"(?<provider>Anthropic|OpenAI|Gemini)\s+returned\s+(?<status>\d{3})",
        RegexOptions.IgnoreCase)]
    private static partial Regex StatusPattern();
}
