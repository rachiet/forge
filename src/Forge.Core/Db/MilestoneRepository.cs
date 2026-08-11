using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

/// <summary>
/// Reads and writes the milestones table: the phases the plan is grouped into, in the order
/// they were first named. A milestone has no status column — its state is derived from the
/// tasks pointing at it.
/// </summary>
public sealed class MilestoneRepository(IDbConnection conn)
{
    /// <summary>The phase holding work that was never a task: intake, planning, the handover note.</summary>
    public const string GettingStarted = "Getting started";

    /// <summary>The phase holding QA's rounds and every bug they produce.</summary>
    public const string Testing = "Testing & fixes";

    /// <summary>
    /// The milestone with this name, creating it at the end of the plan if it is new. Naming an
    /// existing phase reuses its row, so the Principal groups tasks by repeating the name and
    /// never has to carry an id.
    /// </summary>
    public Milestone EnsureByName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("A milestone name must be non-empty.", nameof(name));

        if (Find(trimmed) is { } existing) return existing;

        var position = conn.ExecuteScalar<long>("SELECT COALESCE(MAX(position), -1) + 1 FROM milestones");
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO milestones (name, position) VALUES (@trimmed, @position) RETURNING id
            """, new { trimmed, position });
        return new Milestone(id, trimmed, position);
    }

    /// <summary>
    /// The same as <see cref="EnsureByName"/>, but placed first in the plan. Used for the phase
    /// that holds pre-task work, which happens before anything the Principal names.
    /// </summary>
    public Milestone EnsureFirst(string name)
    {
        if (Find(name.Trim()) is { } existing) return existing;

        conn.Execute("UPDATE milestones SET position = position + 1");
        var id = conn.ExecuteScalar<long>("""
            INSERT INTO milestones (name, position) VALUES (@name, 0) RETURNING id
            """, new { name = name.Trim() });
        return new Milestone(id, name.Trim(), 0);
    }

    /// <summary>Every milestone in plan order.</summary>
    public IReadOnlyList<Milestone> List() =>
        [.. conn.Query<Milestone>(
            "SELECT id AS Id, name AS Name, position AS Position FROM milestones ORDER BY position, id")];

    /// <summary>The names already in the plan, for a refusal that tells an agent what to reuse.</summary>
    public IReadOnlyList<string> Names() => [.. List().Select(m => m.Name)];

    private Milestone? Find(string name) => conn.QueryFirstOrDefault<Milestone>(
        "SELECT id AS Id, name AS Name, position AS Position FROM milestones WHERE name = @name COLLATE NOCASE",
        new { name });
}
