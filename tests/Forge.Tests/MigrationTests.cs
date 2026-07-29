using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// Migrations run against databases that already hold a project's history, so the
/// thing worth proving is that the data survives — a green suite over fresh
/// in-memory DBs would never touch the code path that matters.
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"forge-migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path)) File.Delete(path);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A database as it stood before cost was dropped: the current schema everywhere
    /// else (token_ledger references tasks, so the rest has to exist), with the old
    /// token_ledger put back exactly as it shipped.
    /// </summary>
    private void CreateLegacyLedger()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        conn.Execute(Schema.ProjectDdl);
        conn.Execute("""
            DROP TABLE token_ledger;

            CREATE TABLE token_ledger (
              id INTEGER PRIMARY KEY,
              agent_instance_id TEXT NOT NULL,
              role TEXT NOT NULL CHECK(role IN ('pm','principal','engineer','qa','researcher')),
              task_id INTEGER REFERENCES tasks(id),
              model TEXT NOT NULL,
              tokens_in INTEGER NOT NULL CHECK(tokens_in >= 0),
              tokens_out INTEGER NOT NULL CHECK(tokens_out >= 0),
              cost_usd REAL NOT NULL CHECK(cost_usd >= 0),
              created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            INSERT INTO token_ledger (id, agent_instance_id, role, task_id, model,
                                      tokens_in, tokens_out, cost_usd, created_at)
            VALUES (7, 'eng-20260719-100000', 'engineer', NULL, 'claude-sonnet-5',
                    1000, 500, 0.0105, '2026-07-19 10:00:00');
            """);
    }

    [Fact]
    public void Existing_ledger_loses_its_cost_column_but_keeps_every_row()
    {
        CreateLegacyLedger();

        using var conn = Database.OpenProject(_dbPath);

        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('token_ledger')").ToList();
        Assert.DoesNotContain("cost_usd", columns);

        // History is the point of a ledger: the id, the counts and the original
        // timestamp all have to come through the table rebuild unchanged.
        var entry = Assert.Single(new LedgerRepository(conn).List());
        Assert.Equal(7, entry.Id);
        Assert.Equal("eng-20260719-100000", entry.AgentInstanceId);
        Assert.Equal(AgentRole.Engineer, entry.Role);
        Assert.Equal("claude-sonnet-5", entry.Model);
        Assert.Equal(1000, entry.TokensIn);
        Assert.Equal(500, entry.TokensOut);
        Assert.Equal("2026-07-19 10:00:00", entry.CreatedAt);
    }

    [Fact]
    public void Migrated_ledger_still_accepts_new_rows()
    {
        CreateLegacyLedger();

        using var conn = Database.OpenProject(_dbPath);
        new LedgerRepository(conn).Append(new TokenLedgerEntry
        {
            AgentInstanceId = "pm-20260719-100001",
            Role = AgentRole.Pm,
            TaskId = null,
            Model = "claude-opus-4-8",
            TokensIn = 200,
            TokensOut = 100,
        });

        var totals = new LedgerRepository(conn).ProjectTotals();
        Assert.Equal(1200, totals.TokensIn);
        Assert.Equal(600, totals.TokensOut);
    }

    [Fact]
    public void Migration_is_a_no_op_on_every_open_after_the_first()
    {
        CreateLegacyLedger();

        // Three opens: the migrating one, then two that must find nothing to do —
        // a migration that ran twice would drop the rebuilt table and lose the rows.
        using (var first = Database.OpenProject(_dbPath)) { }
        using (var second = Database.OpenProject(_dbPath)) { }
        using var third = Database.OpenProject(_dbPath);

        Assert.Single(new LedgerRepository(third).List());
        Assert.Equal(
            1, third.QuerySingle<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_ledger_task'"));
    }

    [Fact]
    public void A_fresh_database_needs_no_migration_and_has_no_cost_column()
    {
        using var conn = Database.OpenProject(_dbPath);

        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('token_ledger')").ToList();
        Assert.DoesNotContain("cost_usd", columns);
        Assert.Contains("tokens_in", columns);
    }
}
