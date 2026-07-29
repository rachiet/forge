namespace Forge.Core.Model;

/// <summary>token_ledger row: one entry per LLM call, written by MeteredLlmClient.</summary>
public sealed record TokenLedgerEntry
{
    public long Id { get; init; }
    public required string AgentInstanceId { get; init; }
    public required AgentRole Role { get; init; }
    public long? TaskId { get; init; }
    public required string Model { get; init; }
    public required int TokensIn { get; init; }
    public required int TokensOut { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheWriteTokens { get; init; }

    /// <summary>
    /// What this one call cost, priced from all four buckets. Stored as integer
    /// nano-dollars; decimal here because money should not round-trip through binary
    /// floating point.
    /// </summary>
    public decimal CostUsd { get; init; }

    /// <summary>Identifies the price snapshot that produced <see cref="CostUsd"/>.</summary>
    public string? PricedWith { get; init; }

    public string? CreatedAt { get; init; }
}

public sealed record MilestoneRecord
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required int Ordinal { get; init; }
}

/// <summary>agent_instances row. Id convention: 'eng-20260718-093012'.</summary>
public sealed record AgentInstanceRecord
{
    public required string Id { get; init; }
    public required AgentRole Role { get; init; }
    public required string Model { get; init; }
    public long? TaskId { get; init; }
    public string? StartedAt { get; init; }
    public string? EndedAt { get; init; }
    public EndReason? EndReason { get; init; }
}

public sealed record DiscussionRecord
{
    public long Id { get; init; }
    public required long TaskId { get; init; }
    public long? ParentId { get; init; }
    public required string Author { get; init; }
    public required string Body { get; init; }
    public string? FilePath { get; init; }
    public int? LineNumber { get; init; }
    public bool Resolved { get; init; }
    public string? CreatedAt { get; init; }
}

/// <summary>secrets_registry row (global DB): names and metadata ONLY — values live in the vault.</summary>
public sealed record SecretRegistryEntry
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? ProvidedAt { get; init; }
}

/// <summary>projects row (global DB): registry of per-project data directories.</summary>
public sealed record ProjectRecord
{
    public required string Name { get; init; }
    public int? TokenBudget { get; init; }
    public string? CreatedAt { get; init; }
}
