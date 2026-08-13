using System.Data;
using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;

namespace Forge.Core.Board;

/// <summary>What the client sees for one feature. Used to decide the project's own state.</summary>
public sealed record BoardItem(
    long Id, string Name, string State, decimal CostUsd, int Done, int Total);

/// <summary>
/// One task as the client reads it: its display name, whether it is done, in flight or not
/// started, and what it cost. `active` is the one a worker has in hand, which the page blinks.
/// </summary>
public sealed record PlanTask(long Id, string Name, string State, decimal CostUsd);

/// <summary>
/// One phase of the plan with the work under it, in plan order. State is derived from the
/// tasks: done when all of them are, active while any is in flight, pending otherwise.
/// </summary>
public sealed record PlanMilestone(
    long Id, string Name, string State, decimal CostUsd, int Done, int Total,
    IReadOnlyList<PlanTask> Tasks);

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
    IReadOnlyList<PlanMilestone> Plan,
    decimal ProjectLevelCostUsd,
    IReadOnlyList<AgentSpend> Agents,
    IReadOnlyList<ChatLine> Chat,
    RequirementsProposal? Proposal = null,
    IReadOnlyList<StuckTask>? AwaitingClient = null,
    Delivery? Delivery = null,
    bool ThemeOffered = false)
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
            Plan(),
            projectLevel,
            Agents(),
            Chat(chatLimit),
            proposal,
            AwaitingClient(),
            HandedOver(),
            // The PM has asked them to pick a theme; the page opens the picker until they do.
            Board.ThemeOffer.Pending(new ProjectMetaRepository(conn)));
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
    /// The statuses that mean a worker has this task in hand. The page blinks whatever is in
    /// one of them, so it is the same list everywhere it is asked.
    /// </summary>
    private const string InFlight = "('claimed','in_progress','in_review','merging','triage')";

    /// <summary>
    /// The plan: every phase in order, with its tasks under it. Two phases are the harness's
    /// own — `Getting started` also absorbs the work that was never a task (intake, planning,
    /// the handover note) and any task created before the plan had phases, and `Testing &amp;
    /// fixes` absorbs QA's rounds, which have no task to attach to. Every dollar in the ledger
    /// therefore lands in exactly one phase.
    /// </summary>
    private IReadOnlyList<PlanMilestone> Plan()
    {
        var milestones = new MilestoneRepository(conn).List();
        if (milestones.Count == 0) return [];

        // Every task, live or not. What is SHOWN is filtered below; what is CHARGED is not,
        // because a cancelled task's spend still has to land somewhere the client can see.
        var tasks = conn.Query<(long? MilestoneId, long Id, string? Name, string Title, string Type, string Status, long Cost)>($"""
            SELECT t.milestone_id, t.id, t.display_name, t.title, t.type, t.status,
                   COALESCE((SELECT SUM(l.cost_nanos) FROM token_ledger l WHERE l.task_id = t.id), 0)
            FROM tasks t
            ORDER BY t.id
            """).ToList();

        // The planning run is finished once it has produced work of its own to show.
        var planned = tasks.Any(t => t.Type != "feature");

        // QA runs carry no task, so their spend is attributed by role instead.
        var qaCost = conn.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger WHERE task_id IS NULL AND role = 'qa'");
        var preTaskCost = conn.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(cost_nanos),0) FROM token_ledger WHERE task_id IS NULL AND role <> 'qa'");

        return [.. milestones.Select(m =>
        {
            var isFirst = string.Equals(m.Name, MilestoneRepository.GettingStarted, StringComparison.Ordinal);
            // A Feature and anything with no phase of its own belong to the first one: a
            // Feature's cost is its decomposition, which is planning.
            var charged = tasks
                .Where(t => t.MilestoneId == m.Id || (isFirst && t.MilestoneId is null))
                .ToList();

            var shown = charged
                .Where(t => t.Type != "feature" && t.Status is not ("rejected" or "cancelled"))
                .Select(t => new PlanTask(
                    t.Id,
                    t.Name is { Length: > 0 } name ? name : t.Title,
                    StateOf(t.Status),
                    LedgerRepository.FromNanos(t.Cost)))
                .ToList();

            var extra = isFirst ? preTaskCost
                : string.Equals(m.Name, MilestoneRepository.Testing, StringComparison.Ordinal) ? qaCost
                : 0;

            // The first phase's work — the intake conversation and the planning run — never
            // became a task, so it is named here rather than leaving the phase reading empty.
            if (isFirst && shown.Count == 0)
                shown.Add(new PlanTask(
                    0, "Set up the project and planned the work",
                    planned ? "done" : "pending", LedgerRepository.FromNanos(preTaskCost)));

            var done = shown.Count(t => t.State == "done");
            var active = shown.Count(t => t.State == "active");

            return new PlanMilestone(
                m.Id, m.Name,
                // The first phase holds work that never became a task, so it has nothing to be
                // in flight and must not blink after the project is delivered: it is finished
                // once the plan it produced exists.
                isFirst ? (planned ? "done" : "pending") : State(done, shown.Count, active),
                LedgerRepository.FromNanos(charged.Sum(t => t.Cost) + extra),
                done, shown.Count, shown);
        })];
    }

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
    /// "complete" once every Feature is done AND the project has been handed over, "building"
    /// in between. Delivery is the last step, so a board whose tasks are all done but which
    /// has not passed QA is still building — otherwise the page offers no way to resume it.
    /// </summary>
    private string ProjectState(IReadOnlyList<BoardItem> features) =>
        features.Count == 0 ? "planning"
        : features.All(f => f.State == "done") && new ProjectMetaRepository(conn).Get("project_delivered") == "1"
            ? "complete"
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
