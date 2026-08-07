using Forge.Core.Model;

namespace Forge.Core.Llm;

public sealed record LlmMessage(string Role, string Content);

/// <summary>Who a call belongs to: the instance, its role, and the task it works.</summary>
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
/// sent = TokensIn + CacheReadTokens + CacheWriteTokens. CacheReadTokens sitting at zero
/// across a run's turns means something is invalidating the prompt prefix.
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
///
/// The client is also the single authority on model ids: nothing else in Forge may
/// name a model. A recipe asks for a tier and the configured client answers with
/// whatever it calls that tier, which is what lets the provider be swapped in
/// configuration rather than in code.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>The concrete model id this provider uses for the given tier.</summary>
    string ModelFor(ModelTier tier);
}

/// <summary>
/// Raised instead of making the call — budgets are enforced by refusal, never by
/// asking the model. The two budgets are denominated differently on purpose: a
/// project cap is money, a task cap is an approximate guard on one runaway agent,
/// so the amounts are carried as pre-formatted text rather than forced into a
/// single unit that would fit neither.
/// </summary>
public sealed class BudgetExhaustedException(string scope, string spent, string budget, bool projectCap)
    : InvalidOperationException($"{scope} budget exhausted: {spent} spent of {budget}. LLM call refused.")
{
    public string Scope { get; } = scope;
    public string Spent { get; } = spent;
    public string Budget { get; } = budget;

    /// <summary>
    /// True when the PROJECT's dollar cap is spent, false when one task's token budget
    /// is. The distinction decides everything downstream: a task over its own budget is
    /// that task's failure (strike it, hand it to the Principal), but a project over its
    /// cap is a client money decision — no task did anything wrong, so nothing may be
    /// struck; the whole build pauses until the client raises the cap.
    /// </summary>
    public bool ProjectCap { get; } = projectCap;

    public static BudgetExhaustedException Tokens(string scope, long spent, long budget) =>
        new(scope, $"{spent:N0} tokens", $"{budget:N0}", projectCap: false);

    public static BudgetExhaustedException Usd(string scope, decimal spent, decimal budget) =>
        new(scope, $"${spent:F4}", $"${budget:F2}", projectCap: true);
}
