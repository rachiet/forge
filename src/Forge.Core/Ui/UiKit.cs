using Forge.Core.Agents;
using Forge.Core.Workspaces;

namespace Forge.Core.Ui;

/// <summary>
/// Installs the UI kit into a client repo: the stylesheet, the behaviour script and the class
/// catalogue are copied from Forge's templates, and theme.css is assembled from the project's
/// <see cref="ThemeChoice"/>. Every write overwrites what is there, and it runs on every task
/// workspace, so a kit file an agent changed is back to Forge's copy before the next turn.
/// </summary>
public static class UiKit
{
    /// <summary>The class catalogue, written at the repo root.</summary>
    public const string CatalogueFile = "UI-KIT.md";

    /// <summary>Where the kit is served from, under the runnable project's static root.</summary>
    public const string KitDirectory = "wwwroot/forge-ui";

    /// <summary>The one stylesheet an application may add of its own.</summary>
    public const string AppStylesheet = "app.css";

    /// <summary>The theme ids the kit ships, read from the templates on disk.</summary>
    public static IReadOnlyList<string> Themes(PromptLibrary prompts) => prompts.UiThemes();

    /// <summary>
    /// Each theme id followed by the description in its own file's opening comment, as one line
    /// per theme.
    /// </summary>
    public static IReadOnlyList<string> ThemeCatalogue(PromptLibrary prompts) =>
    [
        .. Themes(prompts).Select(id =>
        {
            var css = prompts.UiAsset($"themes/{id}.css");
            var end = css.IndexOf("*/", StringComparison.Ordinal);
            var comment = end < 0 ? "" : css[..end];
            var text = comment.Replace("/*", " ", StringComparison.Ordinal)
                              .Replace("Theme:", " ", StringComparison.Ordinal);
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // The id and its dash open the file's comment too; drop them and print them once.
            var body = words.SkipWhile(word => word == id || word is "—" or "-");
            return $"{id} — {string.Join(' ', body)}".Trim();
        }),
    ];

    /// <summary>
    /// One theme as the client's picker draws it: the id, the sentence from its own file, and
    /// the file's declarations. The page paints each tile with the theme's real tokens, so a
    /// tile cannot show something the project would not get, and adding a theme file adds a tile.
    /// </summary>
    /// <param name="Id">The theme id, e.g. `slate`.</param>
    /// <param name="Summary">What the theme is for, from its opening comment.</param>
    /// <param name="Css">The theme file, whose `:root` block the page rescopes to the tile.</param>
    public sealed record ThemeTile(string Id, string Summary, string Css);

    /// <summary>Every theme as a tile, in the order the kit lists them.</summary>
    public static IReadOnlyList<ThemeTile> ThemeTiles(PromptLibrary prompts) =>
    [
        .. ThemeCatalogue(prompts).Select(line =>
        {
            var id = line.Split(' ')[0];
            var dash = line.IndexOf('—');
            return new ThemeTile(id, dash > 0 ? line[(dash + 1)..].Trim() : "",
                prompts.UiAsset($"themes/{id}.css"));
        }),
    ];

    /// <summary>
    /// The mode files the picker needs to paint a tile in light and in dark. `auto` is not among
    /// them: a tile shows one scheme at a time, and the toggle above the tiles decides which.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ModeStylesheets(PromptLibrary prompts) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Maps each theme's light-* palette into the tokens the kit reads.
            ["light"] = prompts.UiAsset("modes/light.css"),
            // The same for the dark-* palette.
            ["dark"] = prompts.UiAsset("modes/dark.css"),
        };

    /// <summary>
    /// Writes the kit into a checkout and returns whether it is installed there. A repo with no
    /// single runnable project has nowhere to serve static files from and is left untouched.
    /// </summary>
    public static bool Ensure(string workspaceDir, ThemeChoice theme, PromptLibrary prompts)
    {
        if (KitRoot(workspaceDir) is not { } kitRoot) return false;

        Directory.CreateDirectory(kitRoot);
        WriteIfChanged(Path.Combine(kitRoot, "forge-ui.css"), prompts.UiAsset("forge-ui.css"));
        WriteIfChanged(Path.Combine(kitRoot, "forge-ui.js"), prompts.UiAsset("forge-ui.js"));
        WriteIfChanged(Path.Combine(kitRoot, "theme.css"), ThemeStylesheet(theme, prompts));
        WriteIfChanged(Path.Combine(workspaceDir, CatalogueFile), prompts.UiAsset(CatalogueFile));
        return true;
    }

    /// <summary>
    /// The kit's directory inside a checkout — wwwroot/forge-ui under the one runnable project —
    /// or null when the repo holds no runnable project or more than one.
    /// </summary>
    public static string? KitRoot(string workspaceDir)
    {
        if (!Directory.Exists(workspaceDir)) return null;
        if (AgentToolset.RunnableProjects(workspaceDir) is not { Count: 1 } runnable) return null;

        var projectDir = Path.GetDirectoryName(Path.Combine(workspaceDir, runnable[0]));
        return projectDir is null ? null : Path.Combine(projectDir, KitDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Installs the kit straight onto trunk — clone, install, commit, push — and returns whether
    /// anything was committed. No agent and no task.
    /// </summary>
    public static bool ApplyToTrunk(
        ForgePaths paths, string project, ThemeChoice theme, PromptLibrary prompts)
    {
        var workspaces = new WorkspaceManager(paths, project);
        var clone = paths.RoleWorkspace(project, "theme");
        workspaces.PrepareTrunkClone(clone);

        return Ensure(clone, theme, prompts)
            && workspaces.CommitAndPushTrunk(clone, $"style: {theme.Describe()}");
    }

    /// <summary>
    /// theme.css: the theme's palettes, then the mode that maps one of them into the tokens the
    /// kit reads, then the knob block that overrides both.
    /// </summary>
    private static string ThemeStylesheet(ThemeChoice theme, PromptLibrary prompts) =>
        $"""
        /* Generated by Forge — {theme.Describe()}.
           Rewritten on every task run: edits here are discarded. Change the look by
           changing the project's theme, never by editing this file. */

        {prompts.UiAsset($"themes/{theme.Theme}.css").TrimEnd()}

        {prompts.UiAsset($"modes/{theme.Mode}.css").TrimEnd()}

        {theme.Knobs().TrimEnd()}
        """ + "\n";

    /// <summary>
    /// Writes a file only when its contents would change, creating its directory if needed.
    /// </summary>
    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
