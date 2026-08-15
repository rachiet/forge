using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

/// <summary>Rolled-up spend. Tokens for volume, USD for what it actually cost.</summary>
public sealed record LedgerTotals(
    long TokensIn, long TokensOut, long CacheReadTokens, long CacheWriteTokens, decimal CostUsd)
{
    /// <summary>Everything actually sent or received — the real traffic, not the uncached remainder.</summary>
    public long TotalTokens => TokensIn + TokensOut + CacheReadTokens + CacheWriteTokens;

    /// <summary>
    /// What a task budget is measured against: the tokens the model actually processed fresh.
    /// Cache reads are excluded because they are re-sending a prompt the provider already holds
    /// — they cost a fraction of the rate and say nothing about how much work an instance did,
    /// so counting them made the same budget mean an order of magnitude less on a provider that
    /// caches than on one that does not.
    /// </summary>
    public long BudgetedTokens => TokensIn + TokensOut;
}

/// <summary>Spend attributed to one role — "what did the PM cost me".</summary>
public sealed record RoleSpend(AgentRole Role, long Calls, long TotalTokens, decimal CostUsd);

public sealed class LedgerRepository(IDbConnection conn)
{
    /// <summary>
    /// USD is stored as integer nano-dollars. Rates run to 1e-7 per token, so a
    /// single cache-read token still lands in the hundreds of nanos — plenty of
    /// resolution — while SUM() over the column stays exact integer arithmetic.
    /// </summary>
    /// <summary>How many stored units make one dollar.</summary>
    private const decimal NanosPerUsd = 1_000_000_000m;

    /// <summary>Dollars as the integer nano-dollars the ledger stores.</summary>
    internal static long ToNanos(decimal usd) => (long)decimal.Round(usd * NanosPerUsd, 0);

    /// <summary>Stored nano-dollars back as dollars.</summary>
    internal static decimal FromNanos(long nanos) => nanos / NanosPerUsd;

    /// <summary>Records one LLM call and returns it with the id the database assigned.</summary>
    public TokenLedgerEntry Append(TokenLedgerEntry entry)
    {
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO token_ledger (agent_instance_id, role, task_id, model,
                                      tokens_in, tokens_out, cache_read_tokens,
                                      cache_write_tokens, cost_nanos, priced_with)
            VALUES (@AgentInstanceId, @Role, @TaskId, @Model, @TokensIn, @TokensOut,
                    @CacheReadTokens, @CacheWriteTokens, @CostNanos, @PricedWith)
            RETURNING id
            """,
            new
            {
                entry.AgentInstanceId,
                Role = SnakeCaseEnum.ToSnakeCase(entry.Role),
                entry.TaskId,
                entry.Model,
                entry.TokensIn,
                entry.TokensOut,
                entry.CacheReadTokens,
                entry.CacheWriteTokens,
                CostNanos = ToNanos(entry.CostUsd),
                entry.PricedWith,
            });
        return entry with { Id = id };
    }

    /// <summary>Everything the project has spent, in tokens and dollars.</summary>
    public LedgerTotals ProjectTotals() => Totals("");

    /// <summary>What one task has spent across every instance that worked it.</summary>
    public LedgerTotals TaskTotals(long taskId) => Totals("WHERE task_id = @taskId", new { taskId });

    /// <summary>What one agent instance has processed so far.</summary>
    /// <remarks>
    /// The token budget bounds a single runaway agent, so it is measured per instance
    /// rather than per task — a task legitimately passes through an engineer, a reviewer
    /// and a revision, and charging them all to one allowance starved whichever came last.
    /// </remarks>
    /// <summary>What one agent instance has spent. Task budgets are enforced against this.</summary>
    public LedgerTotals InstanceTotals(string agentInstanceId) =>
        Totals("WHERE agent_instance_id = @agentInstanceId", new { agentInstanceId });

    /// <summary>Sums the four buckets and the cost over the rows a filter selects.</summary>
    private LedgerTotals Totals(string where, object? param = null)
    {
        var row = conn.QuerySingle<(long In, long Out, long Read, long Write, long Nanos)>($"""
            SELECT COALESCE(SUM(tokens_in), 0), COALESCE(SUM(tokens_out), 0),
                   COALESCE(SUM(cache_read_tokens), 0), COALESCE(SUM(cache_write_tokens), 0),
                   COALESCE(SUM(cost_nanos), 0)
            FROM token_ledger {where}
            """, param);
        return new LedgerTotals(row.In, row.Out, row.Read, row.Write, FromNanos(row.Nanos));
    }

    /// <summary>
    /// Spend per role, most expensive first. The ledger has carried role and
    /// agent_instance_id on every row since M0, so attribution needed nothing new —
    /// only a cost column to sum.
    /// </summary>
    /// <summary>What each role has cost, most expensive first.</summary>
    public IReadOnlyList<RoleSpend> SpendByRole()
    {
        var rows = conn.Query<(string Role, long Calls, long Total, long Nanos)>("""
            SELECT role, COUNT(*),
                   COALESCE(SUM(tokens_in + tokens_out + cache_read_tokens + cache_write_tokens), 0),
                   COALESCE(SUM(cost_nanos), 0)
            FROM token_ledger
            GROUP BY role
            ORDER BY SUM(cost_nanos) DESC
            """);
        return rows
            .Select(r => new RoleSpend(
                SnakeCaseEnum.Parse<AgentRole>(r.Role), r.Calls, r.Total, FromNanos(r.Nanos)))
            .ToList();
    }

    /// <summary>Every ledger row, or every row for one task, oldest first.</summary>
    public IReadOnlyList<TokenLedgerEntry> List(long? taskId = null)
    {
        var rows = conn.Query<(long Id, string AgentInstanceId, string Role, long? TaskId,
                string Model, int TokensIn, int TokensOut, int CacheReadTokens, int CacheWriteTokens,
                long CostNanos, string? PricedWith, string CreatedAt)>(
            taskId is null
                ? """
                  SELECT id, agent_instance_id, role, task_id, model, tokens_in, tokens_out,
                         cache_read_tokens, cache_write_tokens, cost_nanos, priced_with, created_at
                  FROM token_ledger ORDER BY id
                  """
                : """
                  SELECT id, agent_instance_id, role, task_id, model, tokens_in, tokens_out,
                         cache_read_tokens, cache_write_tokens, cost_nanos, priced_with, created_at
                  FROM token_ledger WHERE task_id = @taskId ORDER BY id
                  """,
            new { taskId });
        return rows.Select(r => new TokenLedgerEntry
        {
            Id = r.Id,
            AgentInstanceId = r.AgentInstanceId,
            Role = SnakeCaseEnum.Parse<AgentRole>(r.Role),
            TaskId = r.TaskId,
            Model = r.Model,
            TokensIn = r.TokensIn,
            TokensOut = r.TokensOut,
            CacheReadTokens = r.CacheReadTokens,
            CacheWriteTokens = r.CacheWriteTokens,
            CostUsd = FromNanos(r.CostNanos),
            PricedWith = r.PricedWith,
            CreatedAt = r.CreatedAt,
        }).ToList();
    }
}
