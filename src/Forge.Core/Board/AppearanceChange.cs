using System.Data;
using Forge.Core.Agents;
using Forge.Core.Ui;
using Forge.Core.Workspaces;

namespace Forge.Core.Board;

/// <summary>
/// The client choosing how their interface looks. Deterministic from end to end: the choice is
/// a set of ids from closed sets, the kit renders it into theme.css, and both the installed
/// stylesheet and the record of the choice land on trunk in one commit — no agent, no task and
/// no tokens.
/// </summary>
public static class AppearanceChange
{
    /// <summary>Where the theme's own trunk clone lives; reused across changes.</summary>
    private const string CloneRole = "theme";

    /// <summary>
    /// Installs the choice on trunk and records it under docs/requirements/changes/, in one
    /// commit. False when the repo has no runnable project to install into, in which case
    /// nothing is written at all.
    /// </summary>
    public static bool ApplyAndRecord(
        ForgePaths paths, string project, ThemeChoice choice, PromptLibrary prompts, IDbConnection conn)
    {
        var workspaces = new WorkspaceManager(paths, project);
        var clone = workspaces.PrepareTrunkClone(paths.RoleWorkspace(project, CloneRole));

        if (!UiKit.Ensure(clone, choice, prompts)) return false;

        Record(clone, choice, conn);
        return workspaces.CommitAndPushTrunk(clone, $"style: {choice.Describe()}");
    }

    /// <summary>
    /// Writes the choice into the change log: onto the entry of a change the client is still
    /// deciding, when there is one, and otherwise as its own accepted entry. An appearance
    /// change is accepted the moment it is made — the client made it themselves.
    /// </summary>
    private static void Record(string clone, ThemeChoice choice, IDbConnection conn)
    {
        var line = $"- Theme: {choice.Describe()}";

        if (RequirementsProposal.Load(conn)?.ChangeEntry is { Length: > 0 } pending)
        {
            var path = Path.Combine(clone, pending);
            if (File.Exists(path))
            {
                var body = File.ReadAllText(path).TrimEnd();
                // Replace the line rather than stack one per click: the client tries themes on.
                var kept = body.Split('\n').Where(l => !l.StartsWith("- Theme:", StringComparison.Ordinal));
                File.WriteAllText(path, $"{string.Join('\n', kept).TrimEnd()}\n\n## Appearance\n\n{line}\n");
                return;
            }
        }

        var number = ChangeLog.NextNumber(clone);
        var title = $"Appearance: {choice.Theme} {choice.Mode}";
        var entry = ChangeLog.Render(
            number, title,
            "Chose the look from the theme picker.",
            $"The interface uses {choice.Describe()}. No requirement text changed — appearance is "
            + "a selection from the kit, not generated work.",
            null, null);

        var file = Path.Combine(clone, ChangeLog.PathFor(number, title));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ChangeLog.Approve(entry) ?? entry);
    }
}
