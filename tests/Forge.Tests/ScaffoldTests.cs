using Forge.Core.Agents;
using Forge.Core.Design;
using Forge.Core.Scaffolding;

namespace Forge.Tests;

/// <summary>
/// The project layout the harness builds, which every later gate depends on: one runnable
/// project, nothing nested, everything in the solution. These run the real SDK, since the
/// thing under test is what `dotnet new` actually produces — but never a build, since a
/// build started inside the test host blocks on the outer run's MSBuild nodes.
/// </summary>
public class ScaffoldTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"forge-scaffold-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }

    private static ScaffoldPlan Plan(params string[] libraries) =>
        new("MyBooks", "MyBooks.App", libraries, "MyBooks.Tests");

    [Fact]
    public void The_layout_has_one_runnable_project_and_nothing_nested_inside_another()
    {
        var result = SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books", "MyBooks.Storage"));

        Assert.True(result.Ok, result.Refusal);
        Assert.True(File.Exists(Path.Combine(_repo, "MyBooks.sln")));
        Assert.True(Directory.Exists(Path.Combine(_repo, "src", "MyBooks.App", "wwwroot")));

        // The property the whole pipeline rests on: CiRunner, UiKit and DeliveryPlan all
        // ask for the runnable project and expect exactly one answer.
        Assert.Equal(["src/MyBooks.App/MyBooks.App.csproj"], AgentToolset.RunnableProjects(_repo));

        // Every project sits at the path its name implies, so none is inside another's folder.
        var found = Directory.EnumerateFiles(_repo, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_repo, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(
            ["src/MyBooks.App/MyBooks.App.csproj", "src/MyBooks.Books/MyBooks.Books.csproj",
             "src/MyBooks.Storage/MyBooks.Storage.csproj", "tests/MyBooks.Tests/MyBooks.Tests.csproj"],
            found);
    }

    [Fact]
    public void Every_project_is_listed_in_the_solution_so_CI_cannot_pass_on_nothing()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));

        var solution = File.ReadAllText(Path.Combine(_repo, "MyBooks.sln")).Replace('\\', '/');
        foreach (var project in SolutionScaffold.AllProjects(Plan("MyBooks.Books")))
            Assert.Contains(project, solution, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_app_references_its_libraries_and_the_tests_reference_everything()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));

        var app = File.ReadAllText(Path.Combine(_repo, "src", "MyBooks.App", "MyBooks.App.csproj"));
        Assert.Contains("MyBooks.Books.csproj", app, StringComparison.OrdinalIgnoreCase);

        var tests = File.ReadAllText(Path.Combine(_repo, "tests", "MyBooks.Tests", "MyBooks.Tests.csproj"));
        Assert.Contains("MyBooks.App.csproj", tests, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MyBooks.Books.csproj", tests, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applying_the_same_plan_again_changes_nothing()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));
        var before = Directory.EnumerateFiles(_repo, "*.csproj", SearchOption.AllDirectories).Count();

        var again = SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));

        Assert.True(again.Ok, again.Refusal);
        Assert.Empty(again.Created);
        Assert.Equal(before, Directory.EnumerateFiles(_repo, "*.csproj", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void A_change_request_adding_a_module_creates_only_that_module()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));

        var added = SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books", "MyBooks.Storage"));

        Assert.Equal(["src/MyBooks.Storage/MyBooks.Storage.csproj"], added.Created);
        Assert.Single(AgentToolset.RunnableProjects(_repo));
    }

    [Fact]
    public void Every_module_gets_a_stub_saying_it_still_needs_describing()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));

        var report = CoverageGate.CheckModules(_repo);
        Assert.Equal(["src/MyBooks.App/MODULE.md", "src/MyBooks.Books/MODULE.md"], report.Undescribed);
        Assert.False(report.Complete);
    }

    [Fact]
    public void A_module_the_principal_described_stops_being_reported_and_survives_a_re_run()
    {
        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books"));
        var written = Path.Combine(_repo, "src", "MyBooks.Books", "MODULE.md");
        File.WriteAllText(written, "# MyBooks.Books\n\nHolds the reading list and its rules.\n");

        SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books", "MyBooks.Storage"));

        Assert.Equal("# MyBooks.Books\n\nHolds the reading list and its rules.\n", File.ReadAllText(written));
        Assert.Equal(
            ["src/MyBooks.App/MODULE.md", "src/MyBooks.Storage/MODULE.md"],
            CoverageGate.CheckModules(_repo).Undescribed);
    }

    [Fact]
    public void A_second_solution_under_another_name_is_refused()
    {
        SolutionScaffold.Ensure(_repo, Plan());

        var refused = SolutionScaffold.Ensure(_repo, new ScaffoldPlan("Other", "Other.App", [], "Other.Tests"));

        Assert.False(refused.Ok);
        Assert.Contains("MyBooks", refused.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_runnable_project_is_refused_so_the_repo_keeps_exactly_one()
    {
        SolutionScaffold.Ensure(_repo, Plan());

        // Same solution, different app: the repo would end up serving from two projects.
        var refused = SolutionScaffold.Ensure(_repo, new ScaffoldPlan("MyBooks", "MyBooks.Web", [], "MyBooks.Tests"));

        Assert.False(refused.Ok);
        Assert.Contains("src/MyBooks.App/MyBooks.App.csproj", refused.Refusal!, StringComparison.Ordinal);
        Assert.Single(AgentToolset.RunnableProjects(_repo));
    }

    [Fact]
    public void A_name_that_is_not_a_project_name_is_refused_with_the_form_that_would_work()
    {
        var refused = SolutionScaffold.Ensure(_repo, new ScaffoldPlan("MyBooks", "../escape", [], "T"));

        Assert.False(refused.Ok);
        Assert.Contains("MyBooks.Storage", refused.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_projects_with_the_same_name_are_refused_since_the_name_decides_the_directory()
    {
        var refused = SolutionScaffold.Ensure(_repo, Plan("MyBooks.Books", "myBooks.books"));

        Assert.False(refused.Ok);
        Assert.Contains("more than once", refused.Refusal!, StringComparison.Ordinal);
    }
}
