using Dapper;
using Microsoft.Data.Sqlite;

namespace Forge.Core.Db;

/// <summary>Connection factory + schema bootstrap for the global and per-project DBs.</summary>
public static class Database
{
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

    public static SqliteConnection OpenGlobal(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var conn = Open(dbPath);
        conn.Execute(Schema.GlobalDdl);
        return conn;
    }

    public static SqliteConnection OpenProject(string dbPath)
    {
        var conn = Open(dbPath);
        conn.Execute(Schema.ProjectDdl);
        // The DDL only creates missing tables; reshaping one that already holds a
        // project's history is Migrations' job. Idempotent, so it runs on every open.
        Migrations.ApplyProject(conn);
        return conn;
    }
}
