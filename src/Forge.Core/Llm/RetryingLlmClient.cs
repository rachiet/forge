using Forge.Core.Logging;
using Forge.Core.Model;

namespace Forge.Core.Llm;

/// <summary>
/// A provider failure that is worth trying again: an overloaded or unreachable
/// endpoint, rather than a request the provider will always reject.
/// </summary>
/// <remarks>
/// Thrown by the adapters, because only they know their own provider's failure
/// vocabulary. <see cref="RetryingLlmClient"/> owns the policy — how many attempts
/// and how long to wait — so it is written once for every provider.
/// </remarks>
public sealed class TransientLlmException(string message, Exception? inner = null, TimeSpan? retryAfter = null)
    : Exception(message, inner)
{
    /// <summary>How long the provider asked us to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Retries a call the provider failed to serve, with exponential backoff.
/// </summary>
/// <remarks>
/// Sits inside <see cref="MeteredLlmClient"/>: the budget is checked once per
/// logical call, and only the attempt that returns produces a ledger row. Attempts
/// are capped rather than classified perfectly — a provider can report a permanent
/// condition with a transient-looking status, so the ceiling is what guarantees a
/// misread costs seconds instead of spinning.
/// </remarks>
public sealed class RetryingLlmClient(ILlmClient inner, ForgeLogger? logger = null, int attempts = 3) : ILlmClient
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>Never wait longer than this, however long the provider asks for.</summary>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    public string ModelFor(ModelTier tier) => inner.ModelFor(tier);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var delay = FirstDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await inner.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            catch (TransientLlmException e) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                var wait = e.RetryAfter is { } asked && asked < MaxDelay ? asked : delay;
                _log.Event(EventType.ErrorProvider,
                    $"{request.Model} unavailable (attempt {attempt} of {attempts}); retrying in {wait.TotalSeconds:0.#}s: {e.Message}");
                await Task.Delay(wait, ct).ConfigureAwait(false);
                delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
            }
        }
    }
}
