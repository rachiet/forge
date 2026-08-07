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

    /// <summary>Same name in Forge's templates/ and in the client repo's root.</summary>
    public const string ConventionsFile = "CONVENTIONS.md";

    public static void Init(ForgePaths paths, string name)
    {
        ForgePaths.ValidName(name);

        // A project exists in three places — the directory, the registry row, the
        // bare repo — and they can disagree if a previous init half-finished. Check
        // all three up front so a broken remnant reports "already exists" instead of
        // being silently completed, and so the registry INSERT never surprises us
        // with a primary-key violation partway through.
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
    /// The bare repo gets a seed commit immediately. An empty repo has no HEAD, so
    /// cloning one and branching from it fails — every task workspace would have to
    /// special-case "is this the first task?". One commit at init removes the case.
    /// PROJECT.md is a stub here; the PM authors the real one in M2.
    /// </summary>
    /// <remarks>
    /// CONVENTIONS.md is seeded from Forge's own template for the same reason .gitignore
    /// is: it is knowledge the harness already has, and a model asked to re-derive it every
    /// project produces a different answer each time. Two finished projects disagreed on
    /// their error-response shape and their test naming for no reason, and the second began
    /// with none of the rules the first had paid for in failed tasks. The Principal appends
    /// what is genuinely project-specific; the house rules arrive before it runs.
    /// </remarks>
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
            // The tool executor points HOME at the task workspace (so agents can't
            // read ~/forge_env), which makes the .NET SDK drop its caches —
            // .dotnet/, .nuget/, .local/ — inside the jail. Without this file the
            // harness's own commit-all sweeps that junk into every task branch and
            // reviewers reject it over and over. Seeded at birth, not left for an
            // agent to remember.
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
