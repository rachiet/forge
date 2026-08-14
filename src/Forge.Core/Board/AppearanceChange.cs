using Forge.Core.Agents;
using Forge.Core.Ui;
using Forge.Core.Workspaces;

namespace Forge.Core.Board;

/// <summary>
/// The client choosing how their interface looks. Deterministic from end to end: the choice is
/// a set of ids from closed sets, the kit renders it into theme.css, and the stylesheet lands on
/// trunk — no agent, no task, no tokens.
///
/// Not recorded anywhere else on purpose. A theme is not a requirement and not a change request:
/// it is a setting the client flips as often as they like, and a log entry per flip would be
/// noise in the record of what was actually asked for and built.
/// </summary>
public static class AppearanceChange
{
    /// <summary>Where the theme's own trunk clone lives; reused across changes.</summary>
    private const string CloneRole = "theme";

    /// <summary>
    /// Installs the choice on trunk and into the copy the client runs. False when the repo has
    /// no runnable project to install into, in which case nothing is written at all.
    /// </summary>
    public static bool Apply(
        ForgePaths paths, string project, ThemeChoice choice, PromptLibrary prompts)
    {
        var workspaces = new WorkspaceManager(paths, project);
        var clone = workspaces.PrepareTrunkClone(paths.RoleWorkspace(project, CloneRole));

        if (!UiKit.Ensure(clone, choice, prompts)) return false;

        var committed = workspaces.CommitAndPushTrunk(clone, $"style: {choice.Describe()}");

        // The handover checkout is a copy taken at delivery, not a live clone, so it would go on
        // serving the old stylesheet until the next handover. One file — theme.css — is
        // rewritten in place, and a reload of the running app shows the new theme.
        if (Directory.Exists(paths.ProjectBuild(project)))
            UiKit.Ensure(paths.ProjectBuild(project), choice, prompts);

        return committed;
    }
}
