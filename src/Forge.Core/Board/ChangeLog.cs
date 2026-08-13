using System.Text;
using Forge.Core.Workspaces;

namespace Forge.Core.Board;

/// <summary>One recorded change request: the client's ask and what it did to the requirements.</summary>
/// <param name="Number">Its position in the chain, 1 for the first change after the build.</param>
/// <param name="File">Repo-relative path of the entry, e.g. docs/requirements/changes/001-drag.md.</param>
/// <param name="Title">The change in a line, taken from the proposal's title.</param>
/// <param name="Approved">Whether the client accepted it; a declined draft is rewritten, not kept.</param>
/// <param name="Markdown">The entry as committed, rendered on the page.</param>
public sealed record ChangeEntry(int Number, string File, string Title, bool Approved, string Markdown);

/// <summary>
/// The append-only record of what the client asked for, one file per change request under
/// docs/requirements/changes/. The requirement files themselves stay a living spec — always
/// describing the product as it is now — so this is the only place the history lives.
/// Entries are written at proposal time carrying `Status: proposed`, and stamped approved when
/// the client accepts.
/// </summary>
public static class ChangeLog
{
    /// <summary>Where the entries live, relative to the repo root.</summary>
    public const string Dir = "docs/requirements/changes";

    /// <summary>The status line of an entry the client has not yet accepted.</summary>
    public const string Proposed = "Status: proposed";

    /// <summary>Renders the next entry for a proposal. The number is the count already on disk plus one.</summary>
    /// <param name="number">Its position in the chain.</param>
    /// <param name="title">The proposal's title, used as the entry's heading.</param>
    /// <param name="asked">What the client asked for, in their own words.</param>
    /// <param name="changed">What the change does to the requirement files.</param>
    /// <param name="removed">What it takes out of them, or null when it removes nothing.</param>
    /// <param name="requirements">The requirement files it touches.</param>
    public static string Render(
        int number, string title, string asked, string changed, string? removed, string? requirements)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# CR-{number:000} — {title}").AppendLine();
        sb.AppendLine($"- Requested: {DateTime.UtcNow:yyyy-MM-dd}");
        if (requirements is { Length: > 0 }) sb.AppendLine($"- Requirements: {requirements}");
        sb.AppendLine($"- {Proposed}").AppendLine();
        sb.AppendLine("## What the client asked for").AppendLine();
        sb.AppendLine(asked.Trim()).AppendLine();
        sb.AppendLine("## What changed in the requirements").AppendLine();
        sb.AppendLine(changed.Trim()).AppendLine();
        sb.AppendLine("## What was removed").AppendLine();
        sb.AppendLine(removed is { Length: > 0 } ? removed.Trim() : "Nothing.");
        return sb.ToString();
    }

    /// <summary>The entry's path in the repo: NNN-<slug-of-title>.md, so the chain reads in order.</summary>
    public static string PathFor(int number, string title) => $"{Dir}/{number:000}-{Slug(title)}.md";

    /// <summary>
    /// The number the next entry takes, read from the entries already in a workspace: one more
    /// than the highest on disk, so a redrafted proposal overwrites its own entry rather than
    /// stacking a second one.
    /// </summary>
    public static int NextNumber(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, Dir);
        if (!Directory.Exists(dir)) return 1;

        var highest = Directory.EnumerateFiles(dir, "*.md")
            .Select(path => NumberOf(Path.GetFileName(path)))
            .DefaultIfEmpty(0)
            .Max();
        return highest + 1;
    }

    /// <summary>
    /// The entry a still-unapproved proposal already wrote, so redrafting replaces it. Null when
    /// every entry on disk has been accepted.
    /// </summary>
    public static string? PendingIn(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, Dir);
        if (!Directory.Exists(dir)) return null;

        foreach (var path in Directory.EnumerateFiles(dir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            if (File.ReadAllText(path).Contains(Proposed, StringComparison.Ordinal))
                return $"{Dir}/{Path.GetFileName(path)}";
        return null;
    }

    /// <summary>Every entry on trunk, oldest first — the chain from the first build to now.</summary>
    public static IReadOnlyList<ChangeEntry> Read(ForgePaths paths, string project)
    {
        var repo = paths.ProjectBareRepo(project);
        if (!Directory.Exists(repo)) return [];

        var listing = Git.Run(repo, "ls-tree", "--name-only", $"{WorkspaceManager.TrunkBranch}:{Dir}");
        if (listing.ExitCode != 0) return [];

        List<ChangeEntry> entries = [];
        foreach (var name in listing.Stdout
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var body = Git.Run(repo, "show", $"{WorkspaceManager.TrunkBranch}:{Dir}/{name}");
            if (body.ExitCode != 0) continue;
            entries.Add(new ChangeEntry(
                NumberOf(name),
                $"{Dir}/{name}",
                TitleOf(body.Stdout),
                !body.Stdout.Contains(Proposed, StringComparison.Ordinal),
                body.Stdout));
        }
        return entries;
    }

    /// <summary>
    /// Stamps an entry approved, in the file's text: `Status: proposed` becomes
    /// `Status: approved <date>`. Returns the new contents, or null when it is already approved.
    /// </summary>
    public static string? Approve(string markdown) =>
        markdown.Contains(Proposed, StringComparison.Ordinal)
            ? markdown.Replace(Proposed, $"Status: approved {DateTime.UtcNow:yyyy-MM-dd}", StringComparison.Ordinal)
            : null;

    /// <summary>A file-name slug: lowercase words joined by dashes, capped so paths stay readable.</summary>
    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length > 0 ? slug : "change";
    }

    /// <summary>The leading number of an entry's file name, 0 when it carries none.</summary>
    private static int NumberOf(string name) =>
        int.TryParse(name.Split('-')[0], out var n) ? n : 0;

    /// <summary>The entry's H1 without its `CR-001 — ` prefix, for a one-line label on the page.</summary>
    private static string TitleOf(string markdown)
    {
        foreach (var raw in markdown.Split('\n').Take(10))
        {
            var line = raw.Trim();
            if (!line.StartsWith("# ", StringComparison.Ordinal)) continue;
            var heading = line[2..].Trim();
            var dash = heading.IndexOf('—');
            return dash > 0 ? heading[(dash + 1)..].Trim() : heading;
        }
        return "Change request";
    }
}
