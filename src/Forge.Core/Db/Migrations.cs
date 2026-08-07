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
    public static void Apply(SqliteConnection conn)
    {
        AddNeedsHumanStatus(conn);
        AddSplitDepth(conn);
        AddColumn(conn, "contract_ops", "ALTER TABLE tasks ADD COLUMN contract_ops TEXT;");
    }

    /// <summary>
    /// Adds a nullable column to `tasks` when the database predates it. A plain ADD COLUMN
    /// rather than a rebuild: no CHECK constraint changes, and every existing row correctly
    /// reads null. A no-op once <see cref="AddNeedsHumanStatus"/> has rebuilt the table from
    /// the current <see cref="Schema.TasksDdl"/>, which already has the column.
    /// </summary>
    private static void AddColumn(SqliteConnection conn, string column, string ddl)
    {
        var present = conn.Query<string>("SELECT name FROM pragma_table_info('tasks')")
            .Any(c => string.Equals(c, column, StringComparison.Ordinal));
        if (!present) conn.Execute(ddl);
    }

    /// <summary>
    /// Adds `tasks.split_depth` to a database that predates task splitting. Default 0 is
    /// correct for every existing task — none of them came from a split.
    /// </summary>
    private static void AddSplitDepth(SqliteConnection conn) =>
        AddColumn(conn, "split_depth",
            "ALTER TABLE tasks ADD COLUMN split_depth INTEGER NOT NULL DEFAULT 0;");

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

        // Copy by name, not `SELECT *`: the new table is built from the CURRENT DDL, so it
        // has every column added since this database was written (split_depth, and whatever
        // follows). Positional copying would mismatch the moment the schema grows; naming
        // the old table's columns lets the new ones take their defaults.
        var columns = string.Join(", ", conn.Query<string>("SELECT name FROM pragma_table_info('tasks')"));

        conn.Execute("PRAGMA foreign_keys=off; PRAGMA legacy_alter_table=on;");
        using (var tx = conn.BeginTransaction())
        {
            conn.Execute(tempDdl, transaction: tx);
            conn.Execute($"INSERT INTO tasks_migrated ({columns}) SELECT {columns} FROM tasks;",
                transaction: tx);
            conn.Execute("DROP TABLE tasks;", transaction: tx);
            conn.Execute("ALTER TABLE tasks_migrated RENAME TO tasks;", transaction: tx);
            tx.Commit();
        }
        conn.Execute("PRAGMA foreign_keys=on; PRAGMA legacy_alter_table=off;");
    }
}
