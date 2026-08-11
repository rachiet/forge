using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core.Scaffolding;

/// <summary>The projects a repo should hold: one runnable app, its libraries, and one test project.</summary>
/// <param name="Solution">The .sln file's name, without its extension.</param>
/// <param name="App">The one Web SDK project, which serves the pages and the API.</param>
/// <param name="Libraries">Class libraries the app references, in the order they were named.</param>
/// <param name="Tests">The unit test project, which references the app and every library.</param>
public sealed record ScaffoldPlan(
    string Solution, string App, IReadOnlyList<string> Libraries, string Tests);

/// <summary>What a scaffold run did: what it created, or why it refused.</summary>
/// <param name="Refusal">Why the plan could not be built, or null when it was.</param>
/// <param name="Created">Repo-relative paths of the projects this run added.</param>
public sealed record ScaffoldResult(string? Refusal, IReadOnlyList<string> Created)
{
    public bool Ok => Refusal is null;
}

/// <summary>
/// Creates a repo's project layout: the solution, the single runnable web project with its
/// wwwroot, a class library per module, and one test project, all referenced and all listed in
/// the solution. Trusted harness code that runs `dotnet` directly, like <see cref="Ci.CiRunner"/>.
///
/// Additive and idempotent: a project already on disk is left exactly as it is, so the same plan
/// can be applied again when a change request adds a module. Every project it creates is placed
/// at a path derived from its name, which is what keeps a repo to one runnable project and
/// nothing nested inside another project's directory.
/// </summary>
public static partial class SolutionScaffold
{
    /// <summary>
    /// The line a stub MODULE.md ships with. The design gate reports any module still
    /// carrying it, which is how "the Principal never described this module" is detected
    /// rather than hoped against.
    /// </summary>
    public const string ModuleTodo = "forge:todo";

    /// <summary>How long one `dotnet` invocation may take before it is killed.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Applies a plan to a checkout, creating only what is missing.</summary>
    public static ScaffoldResult Ensure(string workspaceDir, ScaffoldPlan plan)
    {
        if (Invalid(plan) is { } refusal) return new ScaffoldResult(refusal, []);

        if (Conflicts(workspaceDir, plan) is { } conflict) return new ScaffoldResult(conflict, []);

        Directory.CreateDirectory(workspaceDir);
        var created = new List<string>();

        var solution = Path.Combine(workspaceDir, $"{plan.Solution}.sln");
        if (!File.Exists(solution) &&
            Dotnet(workspaceDir, "new", "sln", "-n", plan.Solution) is { Length: > 0 } slnError)
        {
            return new ScaffoldResult(slnError, created);
        }

        // The app first: it is the one project every other path in Forge looks for.
        if (Create(workspaceDir, "web", $"src/{plan.App}", plan.App, created) is { } appError)
            return new ScaffoldResult(appError, created);
        Directory.CreateDirectory(Path.Combine(workspaceDir, "src", plan.App, "wwwroot"));
        StubModule(workspaceDir, $"src/{plan.App}", plan.App);

        foreach (var library in plan.Libraries)
        {
            if (Create(workspaceDir, "classlib", $"src/{library}", library, created) is { } libError)
                return new ScaffoldResult(libError, created);
            StubModule(workspaceDir, $"src/{library}", library);
        }

        if (Create(workspaceDir, "xunit", $"tests/{plan.Tests}", plan.Tests, created) is { } testError)
            return new ScaffoldResult(testError, created);

        // The app uses its libraries; the tests use everything. Both are what makes the
        // solution build as one thing rather than a directory of unrelated projects.
        foreach (var library in plan.Libraries)
            Reference(workspaceDir, $"src/{plan.App}/{plan.App}.csproj", $"src/{library}/{library}.csproj");
        Reference(workspaceDir, $"tests/{plan.Tests}/{plan.Tests}.csproj", $"src/{plan.App}/{plan.App}.csproj");
        foreach (var library in plan.Libraries)
            Reference(workspaceDir, $"tests/{plan.Tests}/{plan.Tests}.csproj", $"src/{library}/{library}.csproj");

        foreach (var project in AllProjects(plan))
            AddToSolution(workspaceDir, $"{plan.Solution}.sln", project);

        return new ScaffoldResult(null, created);
    }

    /// <summary>
    /// Why this plan cannot be applied to what is already in the checkout: a solution under a
    /// different name, or a runnable project that is not the one being asked for. Both would
    /// leave the repo with two of something every later gate expects one of.
    /// </summary>
    private static string? Conflicts(string workspaceDir, ScaffoldPlan plan)
    {
        if (!Directory.Exists(workspaceDir)) return null;

        var solutions = Directory.EnumerateFiles(workspaceDir, "*.sln", SearchOption.AllDirectories)
            .Where(NotUnderGit)
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .Where(name => !string.Equals(name, plan.Solution, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (solutions.Count > 0)
            return $"this repo already has the solution '{solutions[0]}'. A repo holds one solution, "
                 + $"so pass solution: \"{solutions[0]}\" and add to it rather than starting another.";

        var runnable = Agents.AgentToolset.RunnableProjects(workspaceDir)
            .Where(p => !p.Equals($"src/{plan.App}/{plan.App}.csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (runnable.Count > 0)
            return $"this repo already has a runnable project at {runnable[0]}. Exactly one project "
                 + "runs — it serves the pages and the API on one port — so name that one as `app` "
                 + "and add your modules as libraries.";

        return null;
    }

    private static bool NotUnderGit(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Writes a MODULE.md for a module that has none, carrying the todo marker for the design
    /// gate. An existing file is never touched — the Principal's description outlives a re-run.
    /// </summary>
    private static void StubModule(string workspaceDir, string directory, string name)
    {
        var path = Path.Combine(
            workspaceDir, directory.Replace('/', Path.DirectorySeparatorChar), "MODULE.md");
        if (File.Exists(path)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"""
            # {name}

            <!-- {ModuleTodo} — what this module is for, what belongs in it, and what does not.
                 Replace this line. A module still carrying it is reported at the end of design. -->
            """ + "\n");
    }

    /// <summary>Every project the plan describes, as a repo-relative .csproj path.</summary>
    public static IReadOnlyList<string> AllProjects(ScaffoldPlan plan) =>
    [
        $"src/{plan.App}/{plan.App}.csproj",
        .. plan.Libraries.Select(l => $"src/{l}/{l}.csproj"),
        $"tests/{plan.Tests}/{plan.Tests}.csproj",
    ];

    // ---- steps ----

    /// <summary>
    /// Creates one project from a template at an explicit path, or returns why it could not.
    /// Null when the project is already there — the whole plan is re-appliable.
    /// </summary>
    private static string? Create(
        string workspaceDir, string template, string directory, string name, List<string> created)
    {
        var csproj = Path.Combine(workspaceDir, directory.Replace('/', Path.DirectorySeparatorChar), $"{name}.csproj");
        if (File.Exists(csproj)) return null;

        // -o and -n together: the output path is stated rather than inherited from a working
        // directory, which is what stops a project landing inside another project's folder.
        if (Dotnet(workspaceDir, "new", template, "-o", directory, "-n", name) is { Length: > 0 } error)
            return error;

        created.Add($"{directory}/{name}.csproj");
        return null;
    }

    /// <summary>Adds a project reference when the referring project does not already have it.</summary>
    private static void Reference(string workspaceDir, string from, string to)
    {
        var path = Path.Combine(workspaceDir, from.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return;

        var target = Path.GetFileName(to);
        if (File.ReadAllText(path).Contains(target, StringComparison.OrdinalIgnoreCase)) return;

        Dotnet(workspaceDir, "add", from, "reference", to);
    }

    /// <summary>Lists a project in the solution when it is not listed already.</summary>
    private static void AddToSolution(string workspaceDir, string solution, string project)
    {
        var path = Path.Combine(workspaceDir, solution);
        if (!File.Exists(path)) return;

        // Solution files write paths with backslashes whatever wrote them.
        if (File.ReadAllText(path).Replace('\\', '/').Contains(project, StringComparison.OrdinalIgnoreCase)) return;

        Dotnet(workspaceDir, "sln", solution, "add", project);
    }

    // ---- validation ----

    /// <summary>
    /// Why a plan cannot be built, or null when it can. Written for the agent that wrote the
    /// plan, so it names the offending value and the form that would have been accepted.
    /// </summary>
    private static string? Invalid(ScaffoldPlan plan)
    {
        foreach (var (label, value) in new[]
                 {
                     ("solution", plan.Solution), ("app", plan.App), ("tests", plan.Tests),
                 }.Concat(plan.Libraries.Select(l => ("library", l))))
        {
            if (!ProjectName().IsMatch(value))
                return $"'{value}' is not a usable {label} name. Use letters, digits and dots, "
                     + "starting with a letter — `MyBooks.Storage`. No slashes, spaces or dot-dot.";
        }

        var duplicates = plan.Libraries
            .Append(plan.App).Append(plan.Tests)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            return $"these project names are used more than once: {string.Join(", ", duplicates)}. "
                 + "Every project needs its own name, since the name decides its directory.";

        return null;
    }

    /// <summary>Matches a .NET project name: dotted segments, each starting with a letter.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)*$")]
    private static partial Regex ProjectName();

    // ---- running dotnet ----

    /// <summary>
    /// Runs one dotnet command in the checkout. Returns null when it succeeded, or its output as
    /// the reason it did not.
    /// </summary>
    private static string? Dotnet(string workspaceDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workspaceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start dotnet — is the .NET SDK on PATH?");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            return $"`dotnet {string.Join(' ', args)}` timed out.";
        }

        if (process.ExitCode == 0) return null;

        var output = new StringBuilder(stdout.GetAwaiter().GetResult());
        var error = stderr.GetAwaiter().GetResult();
        if (error.Length > 0) output.Append('\n').Append(error);
        return $"`dotnet {string.Join(' ', args)}` failed:\n{output.ToString().Trim()}";
    }
}
