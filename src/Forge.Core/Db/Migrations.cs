using Dapper;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>
/// Schema changes that a `CREATE TABLE IF NOT EXISTS` cannot make for itself.
///
/// The DDL in <see cref="Schema"/> only ever creates missing tables, so it brings a
/// fresh database to the current shape and leaves an existing one exactly as it was.
/// Anything that alters a table already carrying data belongs here, runs on every
/// open, and must be a no-op the second time.
/// </summary>
public static class Migrations
{
    public static void ApplyProject(SqliteConnection conn) => DropLedgerCostColumn(conn);

    /// <summary>
    /// Remove token_ledger.cost_usd. Forge meters in tokens — the count the provider
    /// reports and bills on — and no longer derives a dollar figure, because that
    /// derivation needed a hand-maintained price table that is wrong the moment a
    /// provider changes a rate. A stale number is worse than no number.
    ///
    /// Rebuild rather than DROP COLUMN: SQLite refuses to drop a column named in a
    /// CHECK constraint, and this one carried CHECK(cost_usd >= 0).
    /// </summary>
    private static void DropLedgerCostColumn(SqliteConnection conn)
    {
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('token_ledger')");
        if (!columns.Contains("cost_usd")) return;

        // Foreign keys are disabled around the swap per SQLite's documented recipe for
        // rebuilding a table, and the pragma is a no-op inside a transaction — so it
        // has to be set before the transaction opens, and restored after it closes.
        conn.Execute("PRAGMA foreign_keys=off;");
        try
        {
            using var tx = conn.BeginTransaction();
            conn.Execute("""
                CREATE TABLE token_ledger_new (
                  id INTEGER PRIMARY KEY,
                  agent_instance_id TEXT NOT NULL,
                  role TEXT NOT NULL CHECK(role IN ('pm','principal','engineer','qa','researcher')),
                  task_id INTEGER REFERENCES tasks(id),
                  model TEXT NOT NULL,
                  tokens_in INTEGER NOT NULL CHECK(tokens_in >= 0),
                  tokens_out INTEGER NOT NULL CHECK(tokens_out >= 0),
                  created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT INTO token_ledger_new (id, agent_instance_id, role, task_id, model,
                                              tokens_in, tokens_out, created_at)
                SELECT id, agent_instance_id, role, task_id, model,
                       tokens_in, tokens_out, created_at
                FROM token_ledger;

                DROP TABLE token_ledger;
                ALTER TABLE token_ledger_new RENAME TO token_ledger;

                CREATE INDEX IF NOT EXISTS ix_ledger_task ON token_ledger(task_id);
                """, transaction: tx);
            tx.Commit();
        }
        finally
        {
            conn.Execute("PRAGMA foreign_keys=on;");
        }
    }
}
