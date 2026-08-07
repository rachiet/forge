namespace Forge.Core.Model;

/// <summary>One token_ledger row: what a single LLM call used and cost.</summary>
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
    /// What this call cost, priced from all four token buckets. Stored as integer nano-dollars,
    /// and decimal here so money never round-trips through binary floating point.
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

/// <summary>One agent_instances row: a single agent run, e.g. `eng-20260718-093012`.</summary>
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

/// <summary>One secrets_registry row in the global database: a secret's name and metadata, never its value.</summary>
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
