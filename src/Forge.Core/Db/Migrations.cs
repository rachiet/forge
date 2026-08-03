using Dapper;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>Brings an existing project.db up to the current <see cref="Schema"/>.</summary>
/// <remarks>
/// <c>CREATE TABLE IF NOT EXISTS</c> leaves an already-created table alone, and SQLite
/// cannot ALTER a CHECK constraint, so a new status value needs the table rebuilt.
/// </remarks>
public static class Migrations
{
    public static void Apply(SqliteConnection conn) => AddNeedsHumanStatus(conn);

    /// <summary>Rebuilds the tasks table when its status CHECK predates 'needs_human'.</summary>
    private static void AddNeedsHumanStatus(SqliteConnection conn)
    {
        var ddl = conn.QueryFirstOrDefault<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'tasks'");
        if (ddl is null || ddl.Contains("needs_human", StringComparison.Ordinal)) return;

        // legacy_alter_table keeps the RENAME from rewriting the REFERENCES tasks(id)
        // clauses in other tables to point at the temporary name.
        // Only the table being created is renamed; the REFERENCES clauses inside must
        // keep pointing at 'tasks', which is what the new table is called after the swap.
        var tempDdl = Schema.TasksDdl.Replace(
            "CREATE TABLE IF NOT EXISTS tasks (", "CREATE TABLE tasks_migrated (", StringComparison.Ordinal);

        conn.Execute("PRAGMA foreign_keys=off; PRAGMA legacy_alter_table=on;");
        using (var tx = conn.BeginTransaction())
        {
            conn.Execute(tempDdl, transaction: tx);
            conn.Execute("INSERT INTO tasks_migrated SELECT * FROM tasks;", transaction: tx);
            conn.Execute("DROP TABLE tasks;", transaction: tx);
            conn.Execute("ALTER TABLE tasks_migrated RENAME TO tasks;", transaction: tx);
            tx.Commit();
        }
        conn.Execute("PRAGMA foreign_keys=on; PRAGMA legacy_alter_table=off;");
    }
}
