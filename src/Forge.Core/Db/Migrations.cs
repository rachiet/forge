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
    public static void ApplyProject(SqliteConnection conn)
    {
        DropLedgerCostColumn(conn);
        AddLedgerCacheAndCostColumns(conn);
        DropMilestoneStatusColumn(conn);
        DropMessagesThreadId(conn);
    }

    /// <summary>The global forge.db's own migrations — currently dropping the dead budget column.</summary>
    public static void ApplyGlobal(SqliteConnection conn)
    {
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('projects')");
        if (!columns.Contains("token_budget")) return;

        // token_budget on the registry was dead on both axes: no code ever read it, and
        // after the move to dollar budgets it was the wrong unit too. Settings live in
        // each project's own project_meta. Rebuild (the column carries a CHECK).
        Rebuild(conn, "projects", """
            CREATE TABLE projects_new (
              name TEXT PRIMARY KEY,
              created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO projects_new (name, created_at)
            SELECT name, created_at FROM projects;
            DROP TABLE projects;
            ALTER TABLE projects_new RENAME TO projects;
            """);
    }

    /// <summary>
    /// milestones.status is gone: the board DERIVES milestone state from the tasks
    /// attached to it, and nothing ever advanced the stored column past 'planned' —
    /// a stored status beside a derived one is two sources of truth.
    /// </summary>
    private static void DropMilestoneStatusColumn(SqliteConnection conn)
    {
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('milestones')");
        if (!columns.Contains("status")) return;

        Rebuild(conn, "milestones", """
            CREATE TABLE milestones_new (
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL,
              description TEXT,
              ordinal INTEGER NOT NULL
            );
            INSERT INTO milestones_new (id, name, description, ordinal)
            SELECT id, name, description, ordinal FROM milestones;
            DROP TABLE milestones;
            ALTER TABLE milestones_new RENAME TO milestones;
            """);
    }

    /// <summary>messages.thread_id was written and never once queried — dead weight.</summary>
    private static void DropMessagesThreadId(SqliteConnection conn)
    {
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('messages')");
        if (!columns.Contains("thread_id")) return;

        Rebuild(conn, "messages", """
            CREATE TABLE messages_new (
              id INTEGER PRIMARY KEY,
              from_agent TEXT NOT NULL,
              to_agent   TEXT NOT NULL,
              task_id INTEGER REFERENCES tasks(id),
              type TEXT NOT NULL CHECK(type IN ('question','answer','review','decision',
                                       'escalation','status','change_request','system_nudge')),
              payload TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','in_progress','done')),
              created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            INSERT INTO messages_new (id, from_agent, to_agent, task_id, type, payload, status, created_at)
            SELECT id, from_agent, to_agent, task_id, type, payload, status, created_at FROM messages;
            DROP TABLE messages;
            ALTER TABLE messages_new RENAME TO messages;
            CREATE INDEX IF NOT EXISTS ix_messages_queue ON messages(to_agent, status, created_at);
            """);
    }

    /// <summary>The SQLite table-rebuild recipe, shared: FK pragma off around a transactional swap.</summary>
    private static void Rebuild(SqliteConnection conn, string table, string script)
    {
        conn.Execute("PRAGMA foreign_keys=off;");
        try
        {
            using var tx = conn.BeginTransaction();
            conn.Execute(script, transaction: tx);
            tx.Commit();
        }
        finally
        {
            conn.Execute("PRAGMA foreign_keys=on;");
        }
    }

    /// <summary>
    /// Give an existing ledger the cache buckets and the cost column. Plain ADD
    /// COLUMN, unlike the drop above: SQLite adds columns in place, and a NOT NULL
    /// column is legal as long as it carries a default — which is right anyway,
    /// since rows written before this point genuinely had no cost recorded.
    /// </summary>
    private static void AddLedgerCacheAndCostColumns(SqliteConnection conn)
    {
        var columns = conn.Query<string>("SELECT name FROM pragma_table_info('token_ledger')").ToHashSet();

        foreach (var (name, ddl) in new[]
                 {
                     ("cache_read_tokens", "INTEGER NOT NULL DEFAULT 0"),
                     ("cache_write_tokens", "INTEGER NOT NULL DEFAULT 0"),
                     ("cost_nanos", "INTEGER NOT NULL DEFAULT 0"),
                     ("priced_with", "TEXT"),
                 })
        {
            if (!columns.Contains(name))
                conn.Execute($"ALTER TABLE token_ledger ADD COLUMN {name} {ddl};");
        }

        conn.Execute("CREATE INDEX IF NOT EXISTS ix_ledger_role ON token_ledger(role);");
    }

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
