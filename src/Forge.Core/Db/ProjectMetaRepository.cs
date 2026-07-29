using System.Data;
using Dapper;

namespace Forge.Core.Db;

/// <summary>
/// The project_meta key/value store: project-level orchestration state (QA
/// watermarks) and the client's settings (budget, provider). Its own repository
/// because none of this is about tasks — it lived on TaskRepository only by
/// historical accident, and ProjectSettings reaching through a *task* repository
/// for project configuration read like a wrong turn.
/// </summary>
public sealed class ProjectMetaRepository(IDbConnection conn)
{
    public string? Get(string key) =>
        conn.QueryFirstOrDefault<string>("SELECT value FROM project_meta WHERE key = @key", new { key });

    public void Set(string key, string value) =>
        conn.Execute("""
            INSERT INTO project_meta (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, new { key, value });
}
