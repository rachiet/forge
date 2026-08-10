using System.Data;
using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;

namespace Forge.Core.Board;

/// <summary>What the client sees for one feature.</summary>
public sealed record BoardItem(
    long Id, string Name, string State, decimal CostUsd, int Done, int Total);

/// <summary>
/// Progress on one requirement, from the tasks naming it in `requirements_ref`. The file is
/// the group key and the page joins it to the spec for a title; an empty file is the work
/// that serves no requirement — scaffolding, chores and bug fixes.
/// </summary>
public sealed record RequirementProgress(
    string File, string State, decimal CostUsd, int Done, int Total);

/// <summary>The task a worker has in hand right now, so the client can see what is being built.</summary>
public sealed record CurrentTask(long TaskId, string Title, string Status, string? Requirement);

public sealed record AgentSpend(string Role, long Calls, decimal CostUsd);

public sealed record ChatLine(long Id, string From, string Text, string? At);

/// <summary>A task the build has stopped on until the client answers.</summary>
public sealed record StuckTask(long Id, string Title, string? Note);

/// <summary>Everything the progress page renders, assembled fresh on every poll.</summary>
public sealed record BoardSnapshot(
    string Project,
    string State,
    decimal TotalCostUsd,
    decimal? BudgetUsd,
    string? Provider,
    bool Planned,
    bool SpecReady,
    IReadOnlyList<RequirementProgress> Requirements,
    IReadOnlyList<BoardItem> Features,
    CurrentTask? CurrentTask,
    decimal ProjectLevelCostUsd,
    decimal UnparentedTaskCostUsd,
    IReadOnlyList<AgentSpend> Agents,
    IReadOnlyList<ChatLine> Chat,
    RequirementsProposal? Proposal = null,
    IReadOnlyList<StuckTask>? AwaitingClient = null,
    Delivery? Delivery = null)
{
    /// <summary>Whether the client has to answer something before the build can go on.</summary>
    public bool NeedsClient => AwaitingClient is { Count: > 0 };

    /// <summary>Spend against the cap, for the client's "how much is left" question.</summary>
    public decimal? BudgetRemainingUsd => BudgetUsd is { } cap ? Math.Max(0, cap - TotalCostUsd) : null;

    public bool BudgetExhausted => BudgetUsd is { } cap && TotalCostUsd >= cap;
}

/// <summary>
/// Builds the board's snapshot. Every figure is derived at query time from tasks,
/// token_ledger and messages; nothing is stored or cached, so the page cannot disagree with
/// the ledger.
/// </summary>
public sealed class BoardQuery(IDbConnection conn, string project)
{
    /// <summary>
    /// What a set of tasks cost, including their children's rows. A Feature's own row is
    /// near zero; its real cost is its subtree's.
    /// </summary>
    private const string SubtreeCost = """
        COALESCE((
          SELECT SUM(l.cost_nanos) FROM token_ledger l
          JOIN tasks t2 ON t2.id = l.task_id
          WHERE t2.id = t.id OR t2.parent_id = t.id
        ), 0)
        """;

    /// <summary>
    /// The few fields the project dropdown shows. Kept separate from the full snapshot, which
    /// is far more work and is queried for every project on every poll.
    /// </summary>
    public (string State, decimal TotalCostUsd, decimal? BudgetUsd, string? Provider) Summary()
    {
        var settings = new ProjectSettings(conn);
        return (
            ProjectState(Features()),
            LedgerRepository.FromNanos(
                conn.ExecuteScalar<long>("SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger")),
            settings.BudgetUsd,
            settings.Provider);
    }

    public BoardSnapshot Snapshot(int chatLimit = 200)
    {
        var features = Features();
        var total = LedgerRepository.FromNanos(
            conn.ExecuteScalar<long>("SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger"));

        // Calls with no task at all: the PM conversation, design, and QA rounds.
        var projectLevel = LedgerRepository.FromNanos(conn.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger WHERE task_id IS NULL"));

        // Task work under no feature the page shows, including the subtrees of cancelled and
        // rejected features. Every dollar in the total appears in one section or another.
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
        var proposal = RequirementsProposal.Load(conn);

        return new BoardSnapshot(
            project,
            ProjectState(features),
            total,
            settings.BudgetUsd,
            settings.Provider,
            Planned: features.Count > 0,
            // Shown only once the client has approved: a Feature exists. A pending proposal is
            // still a draft and is read in the review dialog instead, so declining leaves the
            // page as it was.
            SpecReady: conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM tasks WHERE type = 'feature'") > 0,
            Requirements(),
            features,
            InHand(),
            projectLevel,
            unparented,
            Agents(),
            Chat(chatLimit),
            proposal,
            AwaitingClient(),
            HandedOver());
    }

    /// <summary>How to run the finished project, once it has been handed over.</summary>
    private Delivery? HandedOver()
    {
        var meta = new ProjectMetaRepository(conn);
        return (meta.Get("run_dir"), meta.Get("run_command")) is
            ({ Length: > 0 } dir, { Length: > 0 } command)
            ? new Delivery(dir, command, meta.Get("run_url"))
            : null;
    }

    /// <summary>The tasks parked on the client, lowest id first.</summary>
    private IReadOnlyList<StuckTask> AwaitingClient() =>
        conn.Query<StuckTask>("""
            SELECT id AS Id, title AS Title, progress_note AS Note FROM tasks
            WHERE status = 'needs_human' ORDER BY id
            """).ToList();

    /// <summary>
    /// The `requirements_ref` file, with its version stripped: the group a task's progress and
    /// cost are reported under. Empty for a task that names no requirement.
    /// </summary>
    private const string RequirementFile = """
        CASE
          WHEN t.requirements_ref IS NULL OR t.requirements_ref = '' THEN ''
          WHEN instr(t.requirements_ref, '@') > 0
            THEN substr(t.requirements_ref, 1, instr(t.requirements_ref, '@') - 1)
          ELSE t.requirements_ref
        END
        """;

    /// <summary>
    /// Progress per requirement, derived from the tasks naming it: done when all of them are,
    /// active when any is in flight or finished, pending otherwise. A Feature contributes its
    /// cost but is not counted as work — it is the container its children are counted in.
    /// Every task falls in exactly one group, so these costs and the project-level ones
    /// account for the whole ledger. The unnamed group sorts last, since it is the leftover
    /// rather than part of the plan.
    /// </summary>
    private IReadOnlyList<RequirementProgress> Requirements() =>
        conn.Query<(string File, long Cost, int Done, int Total, int Active)>($"""
            SELECT {RequirementFile} AS File,
                   COALESCE((SELECT SUM(l.cost_nanos) FROM token_ledger l WHERE l.task_id = t.id), 0),
                   CASE WHEN t.type <> 'feature' AND t.status = 'done' THEN 1 ELSE 0 END,
                   CASE WHEN t.type <> 'feature'
                         AND t.status NOT IN ('rejected','cancelled') THEN 1 ELSE 0 END,
                   CASE WHEN t.type <> 'feature'
                         AND t.status IN ('claimed','in_progress','in_review','merging','triage')
                        THEN 1 ELSE 0 END
            FROM tasks t
            """)
            .GroupBy(r => r.File, StringComparer.Ordinal)
            .OrderBy(g => g.Key.Length == 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RequirementProgress(
                g.Key,
                State(g.Sum(r => r.Done), g.Sum(r => r.Total), g.Sum(r => r.Active)),
                LedgerRepository.FromNanos(g.Sum(r => r.Cost)),
                g.Sum(r => r.Done),
                g.Sum(r => r.Total)))
            .Where(r => r.Total > 0 || r.CostUsd > 0)
            .ToList();

    /// <summary>
    /// The task a worker has in hand, lowest id first. Null when nothing is in flight, which is
    /// every moment between tasks as well as a project nobody is building.
    /// </summary>
    private CurrentTask? InHand() =>
        conn.Query<(long Id, string Title, string Status, string? Requirement)>($"""
            SELECT t.id, t.title, t.status, {RequirementFile} AS Requirement
            FROM tasks t
            WHERE t.type <> 'feature'
              AND t.status IN ('claimed','in_progress','in_review','merging','triage')
            ORDER BY t.id LIMIT 1
            """)
            .Select(r => new CurrentTask(
                r.Id, r.Title, r.Status,
                r.Requirement is { Length: > 0 } file ? file : null))
            .FirstOrDefault();

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

    /// <summary>
    /// How far the plan has got: "planning" until the client approves something to build,
    /// "complete" once every Feature is done, "building" in between.
    /// </summary>
    private static string ProjectState(IReadOnlyList<BoardItem> features) =>
        features.Count == 0 ? "planning"
        : features.All(f => f.State == "done") ? "complete"
        : "building";

    private IReadOnlyList<AgentSpend> Agents() =>
        new LedgerRepository(conn).SpendByRole()
            .Select(r => new AgentSpend(SnakeCaseEnum.ToSnakeCase(r.Role), r.Calls, r.CostUsd))
            .ToList();

    /// <summary>The client's conversation with the PM. Agent-to-agent traffic is left out.</summary>
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
