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

public sealed record ReviewVerdict(
    bool Approved, string Feedback, string? Convention, EndReason End, string? RejectedBugReason = null);

/// <summary>
/// The Principal review (spec §7, M4): a diff that already passed CI is read by
/// the Principal, who approves it or sends it back. Reviewer ≠ author — this runs
/// a fresh Principal instance that did not write the code.
///
/// Runs the same agent loop, in the task's existing workspace (so read_file and
/// grep see the branch), seeded with the diff. The verdict comes back on the
/// AgentRunResult; a requested convention is appended to CONVENTIONS.md here, so
/// the rule survives whether or not this particular task ends up merging.
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

    public async Task<ReviewVerdict> RunAsync(
        TaskRecord task, string branch, WorkspaceManager workspaces, CancellationToken ct = default)
    {
        var log = _log.For(task.Id);
        var executor = new ToolExecutor(workspaces.Path(task.Id), _recipe.ToolAllowlist, vault);
        // The review is about one task; bind its loop's logging to that task so its
        // events (instance start, llm.call) are task-scoped like the rest of the task.
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, log);

        var diff = workspaces.DiffAgainstTrunk(task.Id, branch);
        var result = await loop
            .RunReviewAsync([new LlmMessage("user", Brief(task, diff))], task, executor, ct)
            .ConfigureAwait(false);

        // The reviewer judged the underlying bug not a real defect and rejected it
        // (already transitioned to Rejected by the tool). Surface that so the runner
        // closes it instead of looping request_changes on a fix for a non-bug.
        if (result.RejectedBugReason is { } rejectReason)
        {
            log.Event(EventType.ReviewChangesRequested, $"bug rejected in review: {rejectReason}");
            return new ReviewVerdict(false, rejectReason, null, result.End, rejectReason);
        }

        // No verdict tool was called (budget, cap, crash) — treat as inconclusive,
        // which the runner handles as "not approved" without pretending it was rejected.
        if (result.ReviewApproved is not { } approved)
        {
            log.Event(EventType.ReviewChangesRequested,
                $"review ended without a verdict ({SnakeCaseEnum.ToSnakeCase(result.End)})");
            return new ReviewVerdict(false,
                result.ReviewFeedback ?? "Review did not reach a verdict.", null, result.End);
        }

        var feedback = result.ReviewFeedback ?? (approved ? "Approved." : "Changes requested.");
        new DiscussionRepository(conn).Open(task.Id, "principal", feedback);

        if (approved)
            log.Event(EventType.ReviewApproved, feedback);
        else
            log.Event(EventType.ReviewChangesRequested, feedback);

        return new ReviewVerdict(approved, feedback, result.ReviewConvention, result.End);
    }

    private static string Brief(TaskRecord task, string diff) => $"""
        # Review

        Task {task.Id}: {task.Title}

        ## Objective
        {task.Objective}

        ## Acceptance criteria
        {task.AcceptanceCriteria ?? "(none stated)"}

        This diff has already passed CI — it builds and its tests are green. Review
        it for correctness, generality (not overfitting to the examples), convention
        conformance, and design conformance. Read the touched files with read_file
        and grep for patterns you're worried about. End with approve or
        request_changes.
        {(task.Type == TaskType.Bug ? """

        This is a BUG fix. Before judging the diff, judge the bug itself: reproduce
        the reported failure (read the repro in the objective; check the behaviour it
        claims). If the reported defect is NOT real — the code already behaves per the
        contract, or the repro cannot fail — do NOT loop request_changes demanding a
        fix for a non-bug. Call `reject_bug(reason)` to close it. Reserve
        request_changes for a real defect whose fix is inadequate.
        """ : "")}

        ## Diff (task branch against trunk)

        {diff}
        """;
}
