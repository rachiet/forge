using System.Data;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

public sealed class MilestoneRepository(IDbConnection conn)
{
    private sealed record Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public int Ordinal { get; init; }

        public MilestoneRecord ToRecord() => new()
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Ordinal = Ordinal,
        };
    }

    private const string SelectColumns = """
        SELECT id AS Id, name AS Name, description AS Description, ordinal AS Ordinal
        FROM milestones
        """;

    public MilestoneRecord Insert(MilestoneRecord milestone)
    {
        if (string.IsNullOrWhiteSpace(milestone.Name))
            throw new ArgumentException("Milestone name must be non-empty.", nameof(milestone));

        var id = conn.ExecuteScalar<long>("""
            INSERT INTO milestones (name, description, ordinal)
            VALUES (@Name, @Description, @Ordinal)
            RETURNING id
            """,
            new { milestone.Name, milestone.Description, milestone.Ordinal });
        return milestone with { Id = id };
    }

    public IReadOnlyList<MilestoneRecord> List() =>
        conn.Query<Row>($"{SelectColumns} ORDER BY ordinal, id").Select(r => r.ToRecord()).ToList();

    public MilestoneRecord Get(long id) =>
        conn.QuerySingle<Row>($"{SelectColumns} WHERE id = @id", new { id }).ToRecord();

    /// <summary>Append position, so an agent that omits an ordinal still gets a sane plan order.</summary>
    public int NextOrdinal() =>
        conn.ExecuteScalar<int>("SELECT COALESCE(MAX(ordinal), 0) + 1 FROM milestones");

}
