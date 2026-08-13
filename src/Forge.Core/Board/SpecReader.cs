using Forge.Core.Workspaces;

namespace Forge.Core.Board;

public sealed record SpecSection(string File, string Title, string Markdown);

/// <summary>What one requirement file gained and lost in a pending change.</summary>
/// <param name="File">The requirement's file name, e.g. `01-kanban-board.md`.</param>
/// <param name="Added">Lines the change adds, without their diff marker.</param>
/// <param name="Removed">Lines it takes out, without their diff marker.</param>
public sealed record SpecChange(string File, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>
/// The requirements the client is being asked to accept, read straight from trunk.
///
/// From the bare repo via `git show`, not from a working clone: the PM's workspace can
/// sit mid-edit between turns, and the client should only ever see what has actually
/// been committed. Re-read on every poll, so a change request updates the spec on the
/// page instead of freezing it at the first build.
/// </summary>
public static class SpecReader
{
    private const string RequirementsDir = "docs/requirements";

    /// <summary>
    /// Keyed by repo path, valid while trunk's HEAD is unchanged. The page polls every
    /// 3s and a spec of N files costs N+1 git subprocesses to read — but between
    /// commits the answer cannot differ, so one rev-parse per poll replaces the lot.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, (string Sha, IReadOnlyList<SpecSection> Sections)> Cache = new();

    public static IReadOnlyList<SpecSection> Read(ForgePaths paths, string project)
    {
        var repo = paths.ProjectBareRepo(project);
        if (!Directory.Exists(repo)) return [];

        var head = Git.Run(repo, "rev-parse", WorkspaceManager.TrunkBranch);
        if (head.ExitCode != 0) return [];
        var sha = head.Stdout.Trim();
        if (Cache.TryGetValue(repo, out var cached) && cached.Sha == sha) return cached.Sections;

        var listing = Git.Run(repo, "ls-tree", "--name-only",
            $"{WorkspaceManager.TrunkBranch}:{RequirementsDir}");
        if (listing.ExitCode != 0) return [];

        List<SpecSection> sections = [];
        foreach (var file in listing.Stdout
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var body = Git.Run(repo, "show", $"{WorkspaceManager.TrunkBranch}:{RequirementsDir}/{file}");
            if (body.ExitCode != 0) continue;
            sections.Add(new SpecSection(file, TitleOf(body.Stdout, file), body.Stdout));
        }
        Cache[repo] = (sha, sections);
        return sections;
    }

    /// <summary>
    /// What the requirements have gained and lost since <paramref name="baselineSha"/> — the
    /// state the client last accepted. A change request edits the living spec in place, so this
    /// is the only way to show them the change rather than the whole document again. Empty when
    /// there is no baseline (the first build) or nothing has been edited since.
    /// </summary>
    public static IReadOnlyList<SpecChange> Changes(ForgePaths paths, string project, string? baselineSha)
    {
        if (baselineSha is not { Length: > 0 }) return [];

        var repo = paths.ProjectBareRepo(project);
        if (!Directory.Exists(repo)) return [];

        // Names first, so each file's diff is its own block on the page rather than one wall.
        var names = Git.Run(repo, "diff", "--name-only",
            $"{baselineSha}..{WorkspaceManager.TrunkBranch}", "--", RequirementsDir);
        if (names.ExitCode != 0) return [];

        List<SpecChange> changes = [];
        foreach (var file in names.Stdout
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .Where(f => !f.EndsWith("INDEX.md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // No context lines: the client is reading what changed, not the surrounding prose.
            var diff = Git.Run(repo, "diff", "--unified=0", "--no-color",
                $"{baselineSha}..{WorkspaceManager.TrunkBranch}", "--", file);
            if (diff.ExitCode != 0 || diff.Stdout.Trim().Length == 0) continue;

            var (added, removed) = Lines(diff.Stdout);
            if (added.Count == 0 && removed.Count == 0) continue;
            changes.Add(new SpecChange(Path.GetFileName(file), added, removed));
        }
        return changes;
    }

    /// <summary>
    /// The added and removed lines of a unified diff, without the file headers and hunk markers
    /// and without their leading +/-.
    /// </summary>
    private static (List<string> Added, List<string> Removed) Lines(string diff)
    {
        List<string> added = [], removed = [];
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith('+') && line[1..].Trim().Length > 0) added.Add(line[1..].Trim());
            else if (line.StartsWith('-') && line[1..].Trim().Length > 0) removed.Add(line[1..].Trim());
        }
        return (added, removed);
    }

    /// <summary>The document's own H1 if it has one; the file name is a poor label for a client.</summary>
    private static string TitleOf(string markdown, string fallback)
    {
        foreach (var raw in markdown.Split('\n').Take(20))
        {
            var line = raw.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        }
        return fallback;
    }
}
