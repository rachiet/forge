using System.Data;
using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;

namespace Forge.Core.Board;

/// <summary>What the client sees for one milestone or feature.</summary>
public sealed record BoardItem(
    long Id, string Name, string State, decimal CostUsd, int Done, int Total);

public sealed record AgentSpend(string Role, long Calls, decimal CostUsd);

public sealed record ChatLine(long Id, string From, string Text, string? At);

/// <summary>
/// Everything the progress page renders, in one snapshot. Assembled per poll — the
/// board is a read model over the same tables the orchestrator writes, never a
/// second copy of the truth, so it cannot drift from what actually happened.
/// </summary>
public sealed record BoardSnapshot(
    string Project,
    string State,
    decimal TotalCostUsd,
    decimal? BudgetUsd,
    string? Provider,
    bool Planned,
    bool SpecReady,
    IReadOnlyList<BoardItem> Milestones,
    IReadOnlyList<BoardItem> Features,
    decimal ProjectLevelCostUsd,
    decimal UnparentedTaskCostUsd,
    IReadOnlyList<AgentSpend> Agents,
    IReadOnlyList<ChatLine> Chat)
{
    /// <summary>Spend against the cap, for the client's "how much is left" question.</summary>
    public decimal? BudgetRemainingUsd => BudgetUsd is { } cap ? Math.Max(0, cap - TotalCostUsd) : null;

    public bool BudgetExhausted => BudgetUsd is { } cap && TotalCostUsd >= cap;
}

/// <summary>
/// The board's read side. Every figure is derived at query time from tasks,
/// milestones, token_ledger and messages; nothing here is stored or cached, so the
/// page cannot show a number the ledger disagrees with.
/// </summary>
public sealed class BoardQuery(IDbConnection conn, string project)
{
    /// <summary>
    /// Cost attributed to a set of tasks: their own ledger rows plus those of any
    /// children. A Feature is a parent the Principal decomposes, so its real cost is
    /// the subtree's, not the parent row's — which is usually near zero.
    /// </summary>
    private const string SubtreeCost = """
        COALESCE((
          SELECT SUM(l.cost_nanos) FROM token_ledger l
          JOIN tasks t2 ON t2.id = l.task_id
          WHERE t2.id = t.id OR t2.parent_id = t.id
        ), 0)
        """;

    public BoardSnapshot Snapshot(int chatLimit = 200)
    {
        var milestones = Milestones();
        var features = Features();
        var total = LedgerRepository.FromNanos(
            conn.ExecuteScalar<long>("SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger"));

        // Calls with no task at all: the PM conversation, the design phase, QA rounds.
        // They belong to the project rather than to any feature, and omitting them
        // would leave the client with a total nobody can account for.
        var projectLevel = LedgerRepository.FromNanos(conn.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger WHERE task_id IS NULL"));

        // Task work that sits under no feature THE PAGE SHOWS. The features section
        // excludes cancelled/rejected features, so their subtrees — and their own
        // ledger rows — must land here instead: money that appears in the total but in
        // no section is money the client cannot account for, which is the one property
        // this page must never lose. (Weatherboard really has a cancelled feature.)
        var unparented = LedgerRepository.FromNanos(conn.ExecuteScalar<long>("""
            SELECT COALESCE(SUM(l.cost_nanos),0)
            FROM token_ledger l
            JOIN tasks t ON t.id = l.task_id
            WHERE (t.type <> 'feature'
                   AND (t.parent_id IS NULL
                        OR NOT EXISTS (SELECT 1 FROM tasks p
                                       WHERE p.id = t.parent_id AND p.type = 'feature'
                                         AND p.status NOT IN ('cancelled','rejected'))))
               OR (t.type = 'feature' AND t.status IN ('cancelled','rejected'))
            """));

        var settings = new ProjectSettings(conn);

        return new BoardSnapshot(
            project,
            ProjectState(milestones, features),
            total,
            settings.BudgetUsd,
            settings.Provider,
            Planned: milestones.Count > 0 || features.Count > 0,
            // The spec is shown the moment the PM hands work to the Principal — that
            // handoff is `create_feature`, so the first Feature existing is the trigger.
            // Before then the requirements are still being drafted and revised in
            // conversation, and showing a half-written spec would invite the client to
            // approve something the PM has not committed to.
            SpecReady: conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM tasks WHERE type = 'feature'") > 0,
            milestones,
            features,
            projectLevel,
            unparented,
            Agents(),
            Chat(chatLimit));
    }

    /// <summary>
    /// Milestone state is DERIVED from the tasks attached to it, never read from
    /// milestones.status — the same rule routing follows (CLAUDE.md): two sources of
    /// truth drift, and a status column nothing advances is worse than no column.
    /// A milestone with no tasks yet is pending, not complete.
    /// </summary>
    private IReadOnlyList<BoardItem> Milestones() =>
        conn.Query<(long Id, string Name, long Cost, int Done, int Total, int Active)>($"""
            SELECT m.id, m.name,
                   COALESCE((SELECT SUM(l.cost_nanos) FROM token_ledger l
                             JOIN tasks t ON t.id = l.task_id WHERE t.milestone_id = m.id), 0),
                   (SELECT COUNT(*) FROM tasks t WHERE t.milestone_id = m.id AND t.status = 'done'),
                   (SELECT COUNT(*) FROM tasks t WHERE t.milestone_id = m.id
                      AND t.status NOT IN ('rejected','cancelled')),
                   (SELECT COUNT(*) FROM tasks t WHERE t.milestone_id = m.id
                      AND t.status IN ('claimed','in_progress','in_review','merging','triage'))
            FROM milestones m ORDER BY m.ordinal, m.id
            """)
            .Select(r => new BoardItem(
                r.Id, r.Name, State(r.Done, r.Total, r.Active),
                LedgerRepository.FromNanos(r.Cost), r.Done, r.Total))
            .ToList();

    private IReadOnlyList<BoardItem> Features() =>
        conn.Query<(long Id, string Title, string Status, long Cost, int Done, int Total, int Active)>($"""
            SELECT t.id, t.title, t.status,
                   {SubtreeCost},
                   (SELECT COUNT(*) FROM tasks c WHERE c.parent_id = t.id AND c.status = 'done'),
                   (SELECT COUNT(*) FROM tasks c WHERE c.parent_id = t.id
                      AND c.status NOT IN ('rejected','cancelled')),
                   (SELECT COUNT(*) FROM tasks c WHERE c.parent_id = t.id
                      AND c.status IN ('claimed','in_progress','in_review','merging','triage'))
            FROM tasks t
            WHERE t.type = 'feature' AND t.status NOT IN ('cancelled','rejected')
            ORDER BY t.id
            """)
            .Select(r => new BoardItem(
                r.Id, r.Title,
                // A Feature the Principal never decomposed has no children, so its own
                // status is the only signal available.
                r.Total == 0 ? StateOf(r.Status) : State(r.Done, r.Total, r.Active),
                LedgerRepository.FromNanos(r.Cost), r.Done, r.Total))
            .ToList();

    private static string State(int done, int total, int active) =>
        total > 0 && done == total ? "done"
        : active > 0 || done > 0 ? "active"
        : "pending";

    private static string StateOf(string taskStatus) => taskStatus switch
    {
        "done" => "done",
        "claimed" or "in_progress" or "in_review" or "merging" or "triage" => "active",
        _ => "pending",
    };

    private static string ProjectState(IReadOnlyList<BoardItem> milestones, IReadOnlyList<BoardItem> features)
    {
        if (milestones.Count == 0 && features.Count == 0) return "planning";
        var items = milestones.Count > 0 ? milestones : features;
        return items.All(i => i.State == "done") ? "complete" : "building";
    }

    private IReadOnlyList<AgentSpend> Agents() =>
        new LedgerRepository(conn).SpendByRole()
            .Select(r => new AgentSpend(SnakeCaseEnum.ToSnakeCase(r.Role), r.Calls, r.CostUsd))
            .ToList();

    /// <summary>
    /// The client's conversation with the PM. Only the two directions the client is
    /// party to — agent-to-agent traffic is not their business and would read as noise.
    /// </summary>
    private IReadOnlyList<ChatLine> Chat(int limit) =>
        conn.Query<(long Id, string From, string Payload, string CreatedAt)>("""
            SELECT id, from_agent, payload, created_at FROM messages
            WHERE (from_agent = 'client' AND to_agent = 'pm')
               OR (from_agent = 'pm' AND to_agent = 'client')
            ORDER BY id DESC LIMIT @limit
            """, new { limit })
            .Reverse()
            .Select(r => new ChatLine(r.Id, r.From, r.Payload, r.CreatedAt))
            .ToList();
}
