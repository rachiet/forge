using Forge.Core.Model;

namespace Forge.Core.Workspaces;

/// <summary>
/// Owns the per-task working clone: the directory that is also the tool
/// executor's jail. Created on claim, reused on resume, deleted after merge
/// (CLAUDE.md, directory layout [DECIDED]).
/// </summary>
public sealed class WorkspaceManager(ForgePaths paths, string project)
{
    public const string TrunkBranch = "master";

    public string BareRepo => paths.ProjectBareRepo(project);

    public string Path(long taskId) => paths.TaskWorkspace(project, taskId);

    public bool Exists(long taskId) => Directory.Exists(Path(taskId));

    /// <summary>Branch per task (spec §8): task/&lt;id&gt;-&lt;slug&gt;.</summary>
    public static string BranchName(TaskRecord task) => $"task/{task.Id}-{Slug(task.Title)}";

    /// <summary>
    /// Bring the workspace to a state the agent can work in, whether this is a
    /// first claim or a resume after a kill. Resume is not a special case here:
    /// an existing clone is simply reused, which is what makes crash recovery and
    /// context-bloat recovery the same mechanism.
    /// </summary>
    public string Prepare(TaskRecord task, string branch)
    {
        var dir = Path(task.Id);
        if (Directory.Exists(System.IO.Path.Combine(dir, ".git")))
        {
            Git.Require(dir, "checkout", branch);
            return dir;
        }

        Directory.CreateDirectory(paths.WorkspacesDir(project));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);

        Git.Require(paths.ProjectDir(project), "clone", BareRepo, dir);

        // A branch already on the remote means an earlier instance got as far as
        // pushing before it died; track it rather than forking a second one.
        if (Git.Run(dir, "rev-parse", "--verify", $"origin/{branch}").Ok)
            Git.Require(dir, "checkout", branch);
        else
            Git.Require(dir, "checkout", "-b", branch);
        return dir;
    }

    /// <summary>
    /// A long-lived clone sitting on trunk, for a role that edits documents rather
    /// than working tasks. Reused across turns and pulled up to date each time, so
    /// a conversation spanning days doesn't drift from what the team has merged.
    /// </summary>
    public string PrepareTrunkClone(string dir)
    {
        if (Directory.Exists(System.IO.Path.Combine(dir, ".git")))
        {
            Git.Require(dir, "checkout", TrunkBranch);
            Git.Require(dir, "pull", "--ff-only", "origin", TrunkBranch);
            return dir;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dir)!);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Git.Require(paths.ProjectDir(project), "clone", BareRepo, dir);
        Git.Require(dir, "checkout", TrunkBranch);
        return dir;
    }

    /// <summary>
    /// Append a line to a file on trunk and publish it — used by the review
    /// write-back to add a convention. It goes to trunk directly, not through a
    /// task branch, so the rule persists whether or not the rejected task ever merges.
    /// </summary>
    public bool AppendToTrunkFile(string cloneDir, string relativePath, string line, string message)
    {
        var dir = PrepareTrunkClone(cloneDir);
        var path = System.IO.Path.Combine(dir, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var text = line.EndsWith('\n') ? line : line + "\n";
        File.AppendAllText(path, text);
        return CommitAndPushTrunk(dir, message);
    }

    /// <summary>Commit whatever a doc-writing role changed and publish it. False when nothing changed.</summary>
    public bool CommitAndPushTrunk(string dir, string message)
    {
        if (Git.Require(dir, "status", "--porcelain").Stdout.Trim().Length == 0) return false;
        Git.Require(dir, "add", "-A");
        Git.Require(dir, "commit", "-m", message);
        Git.Require(dir, "push", "origin", TrunkBranch);
        return true;
    }

    /// <summary>Real state from git, never from the agent: did anything actually change?</summary>
    public bool HasUncommittedChanges(long taskId) =>
        Git.Require(Path(taskId), "status", "--porcelain").Stdout.Trim().Length > 0;

    public bool HasCommitsAhead(long taskId, string branch)
    {
        var result = Git.Run(Path(taskId), "rev-list", "--count", $"origin/{TrunkBranch}..{branch}");
        return result.Ok && long.TryParse(result.Output, out var count) && count > 0;
    }

    /// <summary>
    /// The harness commits, not the agent: the commit is the harness's record of
    /// what the workspace contains, so it cannot be shaped by a model that would
    /// rather its diff looked different.
    /// </summary>
    public bool CommitAll(long taskId, string message)
    {
        var dir = Path(taskId);
        if (!HasUncommittedChanges(taskId)) return false;
        Git.Require(dir, "add", "-A");
        Git.Require(dir, "commit", "-m", message);
        return true;
    }

    public void PushBranch(long taskId, string branch) =>
        Git.Require(Path(taskId), "push", "-u", "origin", branch);

    /// <summary>
    /// Merge the task branch into trunk and publish it. Performed in the working
    /// clone and pushed, because the source of truth is a bare repo with no
    /// worktree to merge in. Returns the trunk commit sha.
    /// </summary>
    public string MergeToTrunk(long taskId, string branch, string message)
    {
        var dir = Path(taskId);
        Git.Require(dir, "fetch", "origin");
        Git.Require(dir, "checkout", TrunkBranch);
        // Sync local trunk to the real trunk before merging. Trunk can move under a
        // task while it runs — a review write-back appends a convention and pushes it
        // straight to trunk (AppendToTrunkFile), so by merge time origin/trunk is
        // ahead of this clone's stale local trunk. Merging onto the stale copy then
        // pushes non-fast-forward and is rejected. The working clone is disposable and
        // the bare repo is the source of truth, so hard-reset to origin/trunk and
        // merge onto current trunk. (A genuine content overlap surfaces as a merge
        // conflict here, which is the correct place to see it.)
        Git.Require(dir, "reset", "--hard", $"origin/{TrunkBranch}");
        try
        {
            Git.Require(dir, "merge", "--no-ff", "-m", message, branch);
        }
        catch
        {
            // A conflicted merge leaves MERGE_HEAD and a dirty index behind, which
            // would wedge the workspace for the resume (`checkout branch` refuses).
            // Abort it so the caller's park-as-blocked leaves a clean clone.
            Git.Run(dir, "merge", "--abort");
            throw;
        }
        Git.Require(dir, "push", "origin", TrunkBranch);
        return Git.Require(dir, "rev-parse", "HEAD").Output;
    }

    /// <summary>
    /// The diff the reviewer reads: the task branch against trunk, name-status plus
    /// the patch. Capped so a huge diff can't blow the context window — the reviewer
    /// reads specific files with read_file when it needs more.
    /// </summary>
    public string DiffAgainstTrunk(long taskId, string branch, int maxChars = 20_000)
    {
        var dir = Path(taskId);
        var range = $"origin/{TrunkBranch}...{branch}";
        var names = Git.Run(dir, "diff", "--name-status", range).Output;
        var patch = Git.Run(dir, "diff", range).Stdout;
        var body = $"Files changed:\n{names}\n\n{patch}";
        return body.Length <= maxChars
            ? body
            : body[..maxChars] + $"\n... [diff truncated at {maxChars} chars — read specific files for the rest]";
    }

    /// <summary>Deleted only after the work is safely in the bare repo.</summary>
    public void Discard(long taskId)
    {
        var dir = Path(taskId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Deletes the task's workspace and its branch in the bare repo.</summary>
    /// <remarks>Used when work is abandoned, so the commits are deliberately not kept.</remarks>
    public void DiscardWithBranch(long taskId, string? branch)
    {
        Discard(taskId);
        if (branch is { Length: > 0 }) Git.Run(BareRepo, "branch", "-D", branch);
    }

    private static string Slug(string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length <= 40 ? slug : slug[..40].TrimEnd('-');
    }
}
