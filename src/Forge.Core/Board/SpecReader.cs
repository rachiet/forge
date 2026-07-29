using Forge.Core.Workspaces;

namespace Forge.Core.Board;

public sealed record SpecSection(string File, string Title, string Markdown);

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
