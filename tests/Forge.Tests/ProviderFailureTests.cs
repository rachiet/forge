using Forge.Core.Llm;

namespace Forge.Tests;

/// <summary>
/// What a client is told when a provider refuses the call. The same condition carries a
/// different code at each provider, so these are the pairings the published error tables give.
/// </summary>
public class ProviderFailureTests
{
    [Theory]
    // A rejected key: 401 at Anthropic and OpenAI, 403 at Gemini.
    [InlineData("Anthropic returned 401: {\"type\":\"authentication_error\"}", "did not accept the API key")]
    [InlineData("OpenAI returned 401: {\"error\":{\"code\":\"invalid_api_key\"}}", "did not accept the API key")]
    [InlineData("Gemini returned 403: {\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"API key not valid\"}}",
        "did not accept the API key")]
    // No money: its own code at Anthropic, folded into 429 or 400 elsewhere.
    [InlineData("Anthropic returned 402: {\"type\":\"billing_error\"}", "no credit left")]
    [InlineData("OpenAI returned 429: {\"error\":{\"code\":\"insufficient_quota\"}}", "no credit left")]
    [InlineData("Gemini returned 429: {\"error\":{\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"prepayment credits are depleted\"}}",
        "no credit left")]
    // Busy rather than broke.
    [InlineData("Gemini returned 429: {\"error\":{\"message\":\"rate limit\"}}", "rate-limiting the account")]
    // Down: 529 is Anthropic's own overloaded code.
    [InlineData("Anthropic returned 529: {\"type\":\"overloaded_error\"}", "having trouble at their end")]
    [InlineData("Gemini returned 503: {\"error\":{\"status\":\"UNAVAILABLE\"}}", "having trouble at their end")]
    // A model the account cannot reach, and one that does not exist.
    [InlineData("Gemini returned 404: {\"error\":{\"message\":\"model not found\"}}", "no longer offers the model")]
    public void The_client_is_told_what_to_do_about_it(string detail, string expected) =>
        Assert.Contains(expected, ProviderFailure.Describe(detail)!, StringComparison.Ordinal);

    [Fact]
    public void Text_carrying_no_provider_status_is_left_to_the_caller() =>
        Assert.Null(ProviderFailure.Describe("the workspace was deleted"));
}
