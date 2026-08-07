using Dapper;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>Connection factory + schema bootstrap for the global and per-project DBs.</summary>
/// <summary>Opens the SQLite databases, applying their schema and any migrations.</summary>
public static class Database
{
    /// <summary>Opens a database, creating its directory and enabling foreign keys and WAL.</summary>
    public static SqliteConnection Open(string dbPath)
    {
        TypeHandlerRegistry.EnsureRegistered();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true,
        }.ToString());
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL;");
        return conn;
    }

    /// <summary>Opens the global forge.db and ensures its schema exists.</summary>
    public static SqliteConnection OpenGlobal(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = Open(dbPath);
        conn.Execute(Schema.GlobalDdl);
        return conn;
    }

    /// <summary>Opens a project.db, ensures its schema exists, and applies any pending migrations.</summary>
    public static SqliteConnection OpenProject(string dbPath)
    {
        var conn = Open(dbPath);
        conn.Execute(Schema.ProjectDdl);
        Migrations.Apply(conn);
        return conn;
    }
}
