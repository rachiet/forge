using System.Data;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;

namespace Forge.Core.Review;

/// <summary>A reviewer's decision on one task.</summary>
/// <param name="Approved">Whether the diff may merge.</param>
/// <param name="Feedback">The reason for changes, or the approval note.</param>
/// <param name="Convention">A rule to append to CONVENTIONS.md, if the reviewer gave one.</param>
/// <param name="End">Why the reviewer's instance stopped.</param>
/// <param name="RejectedBugReason">Set when the review rejected the underlying bug instead.</param>
public sealed record ReviewVerdict(
    bool Approved, string Feedback, string? Convention, EndReason End, string? RejectedBugReason = null);

/// <summary>
/// Runs a fresh Principal over a task's diff, which has already passed CI, to approve it or
/// send it back. A new instance every time, so the reviewer never wrote the code.
///
/// It runs in the task's existing workspace, so read_file and grep see the branch, and is
/// seeded with the diff. A convention the reviewer asked for is appended to CONVENTIONS.md
/// here, whether or not the task itself goes on to merge.
/// </summary>
public sealed class ReviewPhase(
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null)
{
    private readonly AgentRecipe _recipe = AgentRecipe.PrincipalReview;
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    /// <summary>The play attached once a review round is not judging the work but re-scoping it.</summary>
    private const string CloseItOutPlay = "close-it-out";

    /// <summary>How many verdicts a task may collect before every later review carries the play.</summary>
    private const int CloseItOutAfterReviews = 3;

    /// <summary>Reviews a task's branch against trunk and returns the verdict.</summary>
    public async Task<ReviewVerdict> RunAsync(
        TaskRecord task, string branch, WorkspaceManager workspaces, CancellationToken ct = default)
    {
        var log = _log.For(task.Id);
        var executor = new ToolExecutor(
            workspaces.Path(task.Id), _recipe.ToolAllowlist, vault, agentHome: workspaces.AgentHome);
        // Bind logging to the task, so a review's events sit alongside the rest of its story.
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, log);

        var diff = workspaces.DiffAgainstTrunk(task.Id, branch);
        var discussions = new DiscussionRepository(conn);

        // Verdicts already recorded, so the reviewer is told which round this is rather than
        // inferring it from a history that only carries the last few entries. Past the
        // threshold every round carries the play: the condition is durable, unlike the
        // one-shot triage play, and the round after next is the one that needs it most.
        var priorVerdicts = discussions.ReviewCount(task.Id);
        var play = priorVerdicts >= CloseItOutAfterReviews
            ? "\n\n" + prompts.Play(CloseItOutPlay).TrimEnd()
            : "";
        if (play.Length > 0)
            log.Message($"Task {task.Id}: {priorVerdicts} reviews so far; attaching the {CloseItOutPlay} play.");

        var result = await loop
            .RunReviewAsync(
                [new LlmMessage("user", Brief(task, diff, discussions.History(task.Id),
                    Ui.UiGate.CustomStyleReport(workspaces.Path(task.Id)), priorVerdicts) + play)],
                task, executor, ct)
            .ConfigureAwait(false);

        // The reviewer rejected the underlying bug, which the runner closes rather than revises.
        if (result.RejectedBugReason is { } rejectReason)
        {
            log.Event(EventType.ReviewChangesRequested, $"bug rejected in review: {rejectReason}");
            return new ReviewVerdict(false, rejectReason, null, result.End, rejectReason);
        }

        // No verdict tool was called: inconclusive, which the runner reviews again.
        if (result.ReviewApproved is not { } approved)
        {
            log.Event(EventType.ReviewChangesRequested,
                $"review ended without a verdict ({SnakeCaseEnum.ToSnakeCase(result.End)})");
            return new ReviewVerdict(false,
                result.ReviewFeedback ?? "Review did not reach a verdict.", null, result.End);
        }

        var feedback = result.ReviewFeedback ?? (approved ? "Approved." : "Changes requested.");
        discussions.Open(task.Id, "principal", feedback);

        if (approved)
            log.Event(EventType.ReviewApproved, feedback);
        else
            log.Event(EventType.ReviewChangesRequested, feedback);

        return new ReviewVerdict(approved, feedback, result.ReviewConvention, result.End);
    }

    /// <summary>
    /// The reviewer's opening turn: the task, its acceptance criteria, the diff, and what has
    /// already been said about it — without which a fresh reviewer re-argues its own verdicts.
    /// <paramref name="priorVerdicts"/> is how many times the work has already been judged.
    /// </summary>
    private static string Brief(
        TaskRecord task, string diff, string history, string? bespokeStyling, int priorVerdicts) => $"""
        # Review

        Task {task.Id}: {task.Title}

        {RoundLine(priorVerdicts)}

        ## Objective
        {task.Objective}

        ## Acceptance criteria
        {task.AcceptanceCriteria ?? "(none stated)"}

        This diff has already passed CI — it builds and its tests are green. Review
        it for correctness, generality (not overfitting to the examples), convention
        conformance, and design conformance. Read the touched files with read_file
        and grep for patterns you're worried about. End with approve or
        request_changes.
        {(history is { Length: > 0 } ? $"""

        ## What has already been said about this task

        Earlier reviews and the engineer's account of each attempt, oldest first. A
        reviewer before you may have asked for the very thing you are about to object
        to — read this first. If an objection here has been met, do not raise it again;
        if you find that two earlier reviews asked for opposite things, settle it now,
        say which reading the acceptance criteria support, and pass the rule as the
        `convention` argument so it is not re-argued next round.

        {history}
        """ : "")}
        {(task.Type == TaskType.Bug ? """

        This is a BUG fix. Before judging the diff, judge the bug itself: reproduce
        the reported failure (read the repro in the objective; check the behaviour it
        claims). If the reported defect is NOT real — the code already behaves per the
        contract, or the repro cannot fail — do NOT loop request_changes demanding a
        fix for a non-bug. Call `reject_bug(reason)` to close it. Reserve
        request_changes for a real defect whose fix is inadequate.
        """ : "")}

        {(bespokeStyling is { Length: > 0 } ? $"""

        ## Styling written by hand

        {bespokeStyling}

        """ : "")}
        ## Diff (task branch against trunk)

        {diff}
        """;

    /// <summary>
    /// States which review round this is, so the cost of another rejection is a fact in the
    /// packet rather than something the reviewer must infer from a truncated history.
    /// </summary>
    private static string RoundLine(int priorVerdicts) => priorVerdicts == 0
        ? "This is the first review of this task."
        : $"This is review {priorVerdicts + 1} of this task: the work has already been sent "
          + $"back {priorVerdicts} time{(priorVerdicts == 1 ? "" : "s")}, and each rejection "
          + "costs another full engineer round.";
}
