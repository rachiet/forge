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

    public static IReadOnlyList<SpecSection> Read(ForgePaths paths, string project)
    {
        var repo = paths.ProjectBareRepo(project);
        if (!Directory.Exists(repo)) return [];

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
