using Dapper;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Model;
using Forge.Core.Workspaces;

namespace Forge.Core;

/// <summary>Creates the on-disk layout for a client project under ForgeDataRoot.</summary>
public static class ProjectBootstrap
{
    // One line per paragraph: the chat bubble is pre-wrap, so a hard-wrapped literal would
    // break mid-sentence in a narrow panel instead of reflowing.
    public const string Greeting =
        "Hi, welcome to Forge. I'm Iris. I'm here to turn your idea into reality.\n\n" +
        "Tell me what you'd like to build and my team will build it for you. I'll ask a few questions along the way to make sure we get it right, and I'll always check with you before anyone starts work.\n\n" +
        "You'll see progress here as it happens, and you can message me any time to change something or add an idea.\n\n" +
        "Don't worry about being technical, that's our job. Just describe the idea that's on your mind.\n\n" +
        "What would you like to forge today?";

    /// <summary>The conventions file's name, the same in Forge's templates/ and the client repo.</summary>
    public const string ConventionsFile = "CONVENTIONS.md";

    public static void Init(ForgePaths paths, string name)
    {
        ForgePaths.ValidName(name);

        // A project exists in three places — directory, registry row, bare repo — which a
        // half-finished init can leave disagreeing. Check all of them before writing any.
        using var global = Database.OpenGlobal(paths.GlobalDb);
        var registered = global.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM projects WHERE name = @name", new { name }) > 0;

        if (Directory.Exists(paths.ProjectDir(name)) || registered)
            throw new InvalidOperationException(
                $"Project '{name}' already exists at {paths.ProjectDir(name)}. " +
                "Delete that directory and its registry row to recreate it.");

        Directory.CreateDirectory(paths.ProjectDir(name));
        Directory.CreateDirectory(paths.WorkspacesDir(name));

        using (var project = Database.OpenProject(paths.ProjectDb(name)))
        {
            new MessageRepository(project).Insert(
                Message.Create(MessageType.Answer, "pm", "client", Greeting));
        }

        InitBareRepo(paths.ProjectBareRepo(name), name);

        global.Execute("INSERT INTO projects (name) VALUES (@name)", new { name });
    }

    /// <summary>
    /// Creates the bare repo and gives it a seed commit, so cloning and branching from it work
    /// without a first-task special case. The commit carries a stub PROJECT.md for the PM to
    /// replace, a .gitignore, and Forge's base CONVENTIONS.md, which the Principal extends
    /// rather than authors.
    /// </summary>
    private static void InitBareRepo(string repoPath, string project)
    {
        Git.Require(Path.GetDirectoryName(repoPath)!,
            "init", "--bare", "--initial-branch", WorkspaceManager.TrunkBranch, repoPath);

        var seed = Path.Combine(Path.GetTempPath(), $"forge-seed-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(seed);
            Git.Require(seed, "init", "--initial-branch", WorkspaceManager.TrunkBranch);
            File.WriteAllText(Path.Combine(seed, "PROJECT.md"),
                $"# {project}\n\nCreated by Forge. Requirements and design not yet authored.\n");
            File.WriteAllText(Path.Combine(seed, ConventionsFile),
                PromptLibrary.Resolve().Template(ConventionsFile));
            // HOME points at the workspace, so the SDK drops its caches inside the jail.
            // Without these entries the harness's commit-all sweeps them into every branch.
            File.WriteAllText(Path.Combine(seed, ".gitignore"), """
                bin/
                obj/
                .dotnet/
                .nuget/
                .local/
                .config/
                .cache/
                .templateengine/
                .aspnet/
                Library/
                """ + "\n");
            Git.Require(seed, "add", "-A");
            Git.Require(seed, "commit", "-m", "chore: initialise project repository");
            Git.Require(seed, "push", repoPath, WorkspaceManager.TrunkBranch);
        }
        finally
        {
            if (Directory.Exists(seed)) Directory.Delete(seed, recursive: true);
        }
    }
}
