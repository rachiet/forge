using Forge.Core.Model;

namespace Forge.Core.Llm;

public sealed record LlmMessage(string Role, string Content);

/// <summary>Attribution is per task (finds tarpits), not just per agent (spec §4.3).</summary>
public sealed record LlmAttribution(string AgentInstanceId, AgentRole Role, long? TaskId);

public sealed record LlmRequest
{
    public required string Model { get; init; }
    public string? System { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public int MaxTokens { get; init; } = 4096;
    public required LlmAttribution Attribution { get; init; }
}

/// <summary>
/// Cache-aware usage, as the provider reported it — these are the counts it bills
/// on, not an estimate. TokensIn is the *uncached* input remainder; CacheReadTokens
/// were served from cache and CacheWriteTokens written to it, so the whole prompt
/// sent = TokensIn + CacheReadTokens + CacheWriteTokens. A run with CacheReadTokens
/// sitting at zero across turns means a prefix invalidator is at work
/// (spec §prompt-caching).
/// </summary>
public sealed record LlmUsage(int TokensIn, int TokensOut, int CacheReadTokens = 0, int CacheWriteTokens = 0);

public sealed record LlmResponse
{
    public required string Content { get; init; }
    public string? StopReason { get; init; }
    public required LlmUsage Usage { get; init; }
}

/// <summary>
/// The one LLM gateway. ALL calls flow through the MeteredLlmClient decorator —
/// never hand an undecorated provider adapter to an agent loop.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

/// <summary>Raised instead of making the call — budgets are enforced by refusal, never by asking the model.</summary>
public sealed class BudgetExhaustedException(string scope, long spent, long budget)
    : InvalidOperationException($"{scope} token budget exhausted: {spent} spent of {budget}. LLM call refused.")
{
    public string Scope { get; } = scope;
    public long Spent { get; } = spent;
    public long Budget { get; } = budget;
}
