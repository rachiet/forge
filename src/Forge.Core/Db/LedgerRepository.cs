using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

public sealed class LedgerRepository(IDbConnection conn)
{
    public TokenLedgerEntry Append(TokenLedgerEntry entry)
    {
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO token_ledger (agent_instance_id, role, task_id, model,
                                      tokens_in, tokens_out)
            VALUES (@AgentInstanceId, @Role, @TaskId, @Model, @TokensIn, @TokensOut)
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
            });
        return entry with { Id = id };
    }

    public (long TokensIn, long TokensOut) ProjectTotals() =>
        conn.QuerySingle<(long, long)>("""
            SELECT COALESCE(SUM(tokens_in), 0), COALESCE(SUM(tokens_out), 0)
            FROM token_ledger
            """);

    public (long TokensIn, long TokensOut) TaskTotals(long taskId) =>
        conn.QuerySingle<(long, long)>("""
            SELECT COALESCE(SUM(tokens_in), 0), COALESCE(SUM(tokens_out), 0)
            FROM token_ledger WHERE task_id = @taskId
            """, new { taskId });

    public IReadOnlyList<TokenLedgerEntry> List(long? taskId = null)
    {
        var rows = conn.Query<(long Id, string AgentInstanceId, string Role, long? TaskId,
                string Model, int TokensIn, int TokensOut, string CreatedAt)>(
            taskId is null
                ? """
                  SELECT id, agent_instance_id, role, task_id, model, tokens_in, tokens_out,
                         created_at
                  FROM token_ledger ORDER BY id
                  """
                : """
                  SELECT id, agent_instance_id, role, task_id, model, tokens_in, tokens_out,
                         created_at
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
            CreatedAt = r.CreatedAt,
        }).ToList();
    }
}
