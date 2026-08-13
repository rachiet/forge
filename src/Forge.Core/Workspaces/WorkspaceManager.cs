using Forge.Core.Model;

namespace Forge.Core.Workspaces;

/// <summary>
/// Owns a project's working clones: the per-task directory that is also the tool executor's
/// jail, created on claim and deleted after merge, and the long-lived clones roles like the PM
/// and QA work in. Also performs the commits, pushes and merges, so what lands is the
/// harness's record of the workspace rather than the agent's account of it.
/// </summary>
public sealed class WorkspaceManager(ForgePaths paths, string project)
{
    public const string TrunkBranch = "master";

    public string BareRepo => paths.ProjectBareRepo(project);

    public string Path(long taskId) => paths.TaskWorkspace(project, taskId);

    public bool Exists(long taskId) => Directory.Exists(Path(taskId));

    /// <summary>A task's branch name: `task/&lt;id&gt;-&lt;slug&gt;`.</summary>
    public static string BranchName(TaskRecord task) => $"task/{task.Id}-{Slug(task.Title)}";

    /// <summary>
    /// Brings a task's workspace to a state the agent can work in, cloning it on a first claim
    /// and reusing the existing clone on a resume.
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

        // A branch already on the remote is an earlier instance's; track it, don't fork.
        if (Git.Run(dir, "rev-parse", "--verify", $"origin/{branch}").Ok)
            Git.Require(dir, "checkout", branch);
        else
            Git.Require(dir, "checkout", "-b", branch);
        return dir;
    }

    /// <summary>
    /// A long-lived clone sitting on trunk, for a role that edits documents rather than working
    /// tasks. Reused across turns and pulled up to date each time, and cleaned of untracked
    /// files so the role sees trunk and nothing left over from an earlier run. Ignored files
    /// such as bin/ and obj/ are kept, so a role does not pay a full rebuild every turn.
    /// </summary>
    public string PrepareTrunkClone(string dir)
    {
        if (Directory.Exists(System.IO.Path.Combine(dir, ".git")))
        {
            Git.Require(dir, "checkout", TrunkBranch);
            Git.Require(dir, "pull", "--ff-only", "origin", TrunkBranch);
            Git.Require(dir, "clean", "-fd");
            return dir;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dir)!);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Git.Require(paths.ProjectDir(project), "clone", BareRepo, dir);
        Git.Require(dir, "checkout", TrunkBranch);
        return dir;
    }

    /// <summary>
    /// Appends a line to a file on trunk and pushes it, without going through a task branch.
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

    /// <summary>Commits and pushes whatever a role changed in a trunk clone. False if nothing changed.</summary>
    public bool CommitAndPushTrunk(string dir, string message)
    {
        if (Git.Require(dir, "status", "--porcelain").Stdout.Trim().Length == 0) return false;
        Git.Require(dir, "add", "-A");
        Git.Require(dir, "commit", "-m", message);
        Git.Require(dir, "push", "origin", TrunkBranch);
        return true;
    }

    /// <summary>Whether the working tree has any change to commit, read from git.</summary>
    public bool HasUncommittedChanges(long taskId) =>
        Git.Require(Path(taskId), "status", "--porcelain").Stdout.Trim().Length > 0;

    public bool HasCommitsAhead(long taskId, string branch)
    {
        var result = Git.Run(Path(taskId), "rev-list", "--count", $"origin/{TrunkBranch}..{branch}");
        return result.Ok && long.TryParse(result.Output, out var count) && count > 0;
    }

    /// <summary>Stages everything in a task's workspace and commits it. False if nothing changed.</summary>
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
    /// Merges a task's branch into trunk and pushes it, returning the trunk commit sha. Done in
    /// the working clone, since the source of truth is a bare repo with no worktree.
    /// </summary>
    public string MergeToTrunk(long taskId, string branch, string message)
    {
        var dir = Path(taskId);
        Git.Require(dir, "fetch", "origin");
        Git.Require(dir, "checkout", TrunkBranch);
        // Trunk can move while a task runs, so reset this disposable clone to origin/trunk
        // before merging onto it. A real content overlap surfaces here as a merge conflict.
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
    /// The task branch's diff against trunk — name-status plus the patch — capped in size. A
    /// reviewer that needs more reads the files directly.
    /// </summary>
    public string DiffAgainstTrunk(long taskId, string branch, int maxChars = 20_000) =>
        Diff(Path(taskId), $"origin/{TrunkBranch}...{branch}", maxChars);

    /// <summary>
    /// What a workspace has changed so far — committed and uncommitted alike — measured from
    /// where its branch left trunk, so trunk's own later commits are not reported as its work.
    /// Empty when nothing has been changed, which is the case on a first attempt.
    /// </summary>
    public static string WorkSoFar(string dir, int maxChars = 12_000)
    {
        var mergeBase = Git.Run(dir, "merge-base", $"origin/{TrunkBranch}", "HEAD");
        if (!mergeBase.Ok) return "";
        var diff = Diff(dir, mergeBase.Output, maxChars);
        return diff.Contains("diff --git", StringComparison.Ordinal) ? diff : "";
    }

    /// <summary>Name-status plus the patch for one diff argument, capped in size.</summary>
    private static string Diff(string dir, string range, int maxChars)
    {
        var names = Git.Run(dir, "diff", "--name-status", range).Output;
        var patch = Git.Run(dir, "diff", range).Stdout;
        var body = $"Files changed:\n{names}\n\n{patch}";
        return body.Length <= maxChars
            ? body
            : body[..maxChars] + $"\n... [diff truncated at {maxChars} chars — read specific files for the rest]";
    }

    /// <summary>Deletes a task's workspace, once its work is merged into the bare repo.</summary>
    public void Discard(long taskId)
    {
        var dir = Path(taskId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// Deletes a task's workspace and its branch in the bare repo, discarding its commits. Used
    /// when the work is abandoned.
    /// </summary>
    public void DiscardWithBranch(long taskId, string? branch)
    {
        Discard(taskId);
        if (branch is { Length: > 0 }) Git.Run(BareRepo, "branch", "-D", branch);
    }

    /// <summary>A task title reduced to a branch-safe slug.</summary>
    private static string Slug(string title)
    {
        var slug = new string(title.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length <= 40 ? slug : slug[..40].TrimEnd('-');
    }
}
