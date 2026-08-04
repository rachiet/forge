using System.Net;
using Forge.Core.Llm;
using Forge.Core.Model;

namespace Forge.Tests;

/// <summary>
/// Retrying a provider that could not serve the call, without retrying one that
/// refused it.
/// </summary>
public class RetryingLlmClientTests
{
    /// <summary>Fails the first <c>failures</c> calls with <c>error</c>, then succeeds.</summary>
    private sealed class FlakyClient(int failures, Func<Exception> error) : ILlmClient
    {
        public int Calls { get; private set; }

        public string ModelFor(ModelTier tier) => "test-model";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Calls <= failures) throw error();
            return Task.FromResult(new LlmResponse { Content = "ok", Usage = new LlmUsage(1, 1) });
        }
    }

    private static LlmRequest Request() => new()
    {
        Model = "test-model",
        Messages = [new LlmMessage("user", "hi")],
        Attribution = new LlmAttribution("test-1", AgentRole.Pm, null),
    };

    [Fact]
    public async Task An_overloaded_provider_is_retried_until_it_answers()
    {
        // The failure this exists for: a 503 on one turn ended a whole chat turn and
        // put a raw provider error in front of the client.
        var inner = new FlakyClient(2, () => new TransientLlmException("503 overloaded"));

        var result = await new RetryingLlmClient(inner, attempts: 3).CompleteAsync(Request());

        Assert.Equal("ok", result.Content);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Retries_stop_at_the_ceiling_and_the_failure_surfaces()
    {
        // A provider can report a permanent condition with a transient-looking status,
        // so the cap is what stops it spinning rather than perfect classification.
        var inner = new FlakyClient(int.MaxValue, () => new TransientLlmException("still down"));

        await Assert.ThrowsAsync<TransientLlmException>(
            () => new RetryingLlmClient(inner, attempts: 3).CompleteAsync(Request()));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task A_rejected_request_is_not_retried()
    {
        // Retrying a bad key only delays finding it — which is exactly how a shadowed
        // GEMINI_API_KEY stayed hidden behind a confusing provider error.
        var inner = new FlakyClient(int.MaxValue,
            () => new HttpRequestException("401 unauthorized", null, HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new RetryingLlmClient(inner, attempts: 3).CompleteAsync(Request()));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_cancelled_call_stops_rather_than_waiting_out_the_backoff()
    {
        var inner = new FlakyClient(int.MaxValue, () => new TransientLlmException("down"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<TransientLlmException>(
            () => new RetryingLlmClient(inner, attempts: 3).CompleteAsync(Request(), cancelled.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_call_that_never_returns_is_abandoned_and_retried()
    {
        // A hang is worse than a failure: the worker lease keeps beating on its own timer,
        // so the board reads "building" while nothing moves. Observed live — a review call
        // sat blocked for two hours despite HttpClient's own timeout.
        var inner = new HangingClient(hangs: 1);

        var result = await new RetryingLlmClient(
                inner, attempts: 3, attemptTimeout: TimeSpan.FromMilliseconds(150))
            .CompleteAsync(Request());

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_caller_that_cancels_is_not_mistaken_for_a_timeout()
    {
        var inner = new HangingClient(hangs: int.MaxValue);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RetryingLlmClient(inner, attempts: 3, attemptTimeout: TimeSpan.FromMinutes(5))
                .CompleteAsync(Request(), cancelled.Token));
    }

    /// <summary>Blocks until cancelled for the first <c>hangs</c> calls, then answers.</summary>
    private sealed class HangingClient(int hangs) : ILlmClient
    {
        public int Calls { get; private set; }

        public string ModelFor(ModelTier tier) => "test-model";

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;
            if (Calls <= hangs) await Task.Delay(Timeout.Infinite, ct);
            return new LlmResponse { Content = "ok", Usage = new LlmUsage(1, 1) };
        }
    }

    [Fact]
    public void The_tier_map_passes_straight_through()
    {
        var inner = new FlakyClient(0, () => new TransientLlmException("unused"));

        Assert.Equal("test-model", new RetryingLlmClient(inner).ModelFor(ModelTier.Reasoning));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public void Only_server_side_failures_count_as_worth_retrying(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, TransientFailure.IsTransient(status));
}
