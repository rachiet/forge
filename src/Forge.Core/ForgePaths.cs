namespace Forge.Core;

/// <summary>
/// The single source of path truth. `ForgeDataRoot` (env FORGE_HOME, default
/// ~/forge-data) is the only path the code hard-knows; everything else is derived.
/// Client project data never lives inside the Forge source repo.
/// </summary>
public sealed class ForgePaths
{
    public const string EnvVar = "FORGE_HOME";

    public string DataRoot { get; }

    public ForgePaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Data root must be a non-empty path.", nameof(dataRoot));
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public static ForgePaths Resolve() =>
        new(Environment.GetEnvironmentVariable(EnvVar)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "forge-data"));

    public string GlobalDb => Path.Combine(DataRoot, "forge.db");
    public string VaultDir => Path.Combine(DataRoot, "vault");
    public string ProjectsDir => Path.Combine(DataRoot, "projects");

    /// <summary>
    /// The cached model price table. Machine-wide rather than per project: prices
    /// are a property of the world, and a copy inside each project.db would drift
    /// as many ways as there are projects.
    /// </summary>
    public string PriceCache => Path.Combine(DataRoot, "prices", "litellm.json");

    /// <summary>
    /// Where Playwright's browsers are installed. Machine-wide for the same reason as the price
    /// cache, and emphatically not per workspace: the download is ~100MB, and a task workspace
    /// is deleted after its merge.
    /// </summary>
    public string BrowsersDir => Path.Combine(DataRoot, "browsers");

    public string ProjectDir(string project) => Path.Combine(ProjectsDir, ValidName(project));
    public string ProjectDb(string project) => Path.Combine(ProjectDir(project), "project.db");
    /// <summary>The project's bare repo, which is the source of truth for its code.</summary>
    public string ProjectBareRepo(string project) => Path.Combine(ProjectDir(project), "repo.git");

    /// <summary>
    /// The client's working copy of the finished project, checked out from trunk. Outside
    /// <see cref="WorkspacesDir"/>, which is scratch space the harness deletes.
    /// </summary>
    public string ProjectBuild(string project) => Path.Combine(ProjectDir(project), "build");

    /// <summary>The project's log file, and the default sink's destination.</summary>
    public string ProjectLog(string project) => Path.Combine(ProjectDir(project), "forge.log");
    /// <summary>The directory holding a project's task and role workspaces.</summary>
    public string WorkspacesDir(string project) => Path.Combine(ProjectDir(project), "workspaces");

    /// <summary>
    /// HOME for every command an agent runs. Outside <see cref="WorkspacesDir"/> so the caches
    /// a toolchain writes there (.dotnet, .local/share/NuGet) never land inside a git checkout,
    /// and shared across a project's tasks so a fresh workspace does not repopulate them.
    /// </summary>
    public string AgentHome(string project) => Path.Combine(ProjectDir(project), "agent-home");

    /// <summary>The tool executor's jail for one task. Created on claim, deleted after merge.</summary>
    public string TaskWorkspace(string project, long taskId) =>
        Path.Combine(WorkspacesDir(project), $"task-{taskId}");

    /// <summary>
    /// A long-lived workspace for a role that works on docs rather than tasks —
    /// the PM's requirements tree, for instance. Kept between chat turns because a
    /// conversation is not a unit of work with a merge at the end.
    /// </summary>
    public string RoleWorkspace(string project, string role) =>
        Path.Combine(WorkspacesDir(project), ValidName(role));

    /// <summary>Project names become directory names; refuse anything that could traverse.</summary>
    public static string ValidName(string project)
    {
        if (string.IsNullOrWhiteSpace(project) ||
            project.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException(
                $"Invalid project name '{project}': use letters, digits, '-' or '_'.", nameof(project));
        }
        return project;
    }
}
