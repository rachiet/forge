using Forge.Core.Db;
using Forge.Core.Design;
using Forge.Core.Model;
using Forge.Core.Qa;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// The contract and the links that hang off it. Everything here is a set comparison the
/// harness can make without asking a model: an id that does not exist, a requirement no
/// operation serves, an operation no task builds, an operation no test covers.
/// </summary>
public class ApiContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-contract-{Guid.NewGuid():N}");

    private const string Valid = """
        openapi: 3.0.0
        info: { title: LinkShort, version: "1.0" }
        paths:
          /api/shorten:
            post:
              operationId: shorten-create
              x-requirement: 01-url-shortening.md
              responses:
                "201": { description: created }
                "400": { description: bad url }
          /{code}:
            get:
              operationId: redirect-follow
              x-requirement: 02-redirection.md
              responses:
                "302": { description: found }
                "404": { description: unknown code }
        """;

    public ApiContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* disposable temp dir */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_valid_contract_yields_its_operations()
    {
        var (contract, errors) = ApiContract.Validate(Valid);

        Assert.Empty(errors);
        Assert.Equal(["shorten-create", "redirect-follow"], contract!.OperationIds);
        Assert.Equal("POST /api/shorten", contract.Operations[0].Signature);
        Assert.Equal("01-url-shortening.md", contract.Operations[0].Requirement);
    }

    [Fact]
    public void An_operation_without_an_id_is_refused()
    {
        // Without an id nothing downstream can name it: no task claims it, no test covers
        // it, and it would sit outside every gate while looking like a complete contract.
        var (contract, errors) = ApiContract.Validate("""
            openapi: 3.0.0
            info: { title: t, version: "1.0" }
            paths:
              /api/things:
                get:
                  x-requirement: 01-things.md
                  responses:
                    "200": { description: ok }
                    "404": { description: missing }
            """);

        Assert.Null(contract);
        Assert.Contains(errors, e => e.Contains("operationId", StringComparison.Ordinal));
    }

    [Fact]
    public void An_operation_without_a_requirement_link_is_refused()
    {
        var (contract, errors) = ApiContract.Validate("""
            openapi: 3.0.0
            info: { title: t, version: "1.0" }
            paths:
              /api/things:
                get:
                  operationId: things-list
                  responses:
                    "200": { description: ok }
                    "404": { description: missing }
            """);

        Assert.Null(contract);
        Assert.Contains(errors, e => e.Contains("x-requirement", StringComparison.Ordinal));
    }

    [Fact]
    public void An_operation_documenting_only_success_is_refused()
    {
        // The error cases are the half of the contract an implementation usually gets
        // wrong, and a contract that does not state them cannot be tested for them.
        var (contract, errors) = ApiContract.Validate("""
            openapi: 3.0.0
            info: { title: t, version: "1.0" }
            paths:
              /api/things:
                get:
                  operationId: things-list
                  x-requirement: 01-things.md
                  responses:
                    "200": { description: ok }
            """);

        Assert.Null(contract);
        Assert.Contains(errors, e => e.Contains("4xx", StringComparison.Ordinal));
    }

    [Fact]
    public void Nonsense_is_refused_rather_than_thrown()
    {
        var (contract, errors) = ApiContract.Validate("this is not a contract at all");

        Assert.Null(contract);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_slice_carries_only_the_named_operations()
    {
        var contract = ApiContract.Validate(Valid).Contract!;

        var slice = contract.Slice(["redirect-follow"]);

        Assert.Contains("redirect-follow", slice, StringComparison.Ordinal);
        Assert.DoesNotContain("shorten-create", slice, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_ids_are_named_back()
    {
        var contract = ApiContract.Validate(Valid).Contract!;

        Assert.Equal(["link-stats"], contract.Unknown(["shorten-create", "link-stats"]));
    }

    // ---------- the design-time gates ----------

    [Fact]
    public void A_requirement_no_operation_serves_is_reported()
    {
        WriteContract(Valid);
        WriteRequirements("01-url-shortening.md", "02-redirection.md", "03-analytics.md");
        using var conn = OpenBoard();

        var report = CoverageGate.CheckContract(conn, _root);

        Assert.Equal(["03-analytics.md"], report.RequirementsWithNoOperation);
        Assert.False(report.Complete);
    }

    [Fact]
    public void A_requirement_the_contract_declares_non_http_is_not_a_gap()
    {
        // A user interface, a performance target or a refactoring has no endpoint to name it.
        // The Principal says so in the document rather than inventing an operation, which QA
        // would then have to write a test against.
        WriteContract(Valid.Replace(
            "paths:",
            $"{ApiContract.NonHttpExtension}:\n  - 03-web-ui.md\npaths:",
            StringComparison.Ordinal));
        WriteRequirements("01-url-shortening.md", "02-redirection.md", "03-web-ui.md");
        using var conn = OpenBoard();

        var report = CoverageGate.CheckContract(conn, _root);

        Assert.Empty(report.RequirementsWithNoOperation);
    }

    [Fact]
    public void An_operation_no_task_claims_is_reported()
    {
        WriteContract(Valid);
        WriteRequirements("01-url-shortening.md", "02-redirection.md");
        using var conn = OpenBoard();
        new TaskRepository(conn).Insert(TaskRecord.Create(
            TaskType.Task, "Shorten", "Build it", 60_000,
            acceptanceCriteria: "works", contractOps: ["shorten-create"]));

        var report = CoverageGate.CheckContract(conn, _root);

        Assert.Equal(["redirect-follow"], report.OperationsWithNoTask);
    }

    [Fact]
    public void A_fully_linked_plan_is_complete()
    {
        WriteContract(Valid);
        WriteRequirements("01-url-shortening.md", "02-redirection.md");
        using var conn = OpenBoard();
        var tasks = new TaskRepository(conn);
        tasks.Insert(TaskRecord.Create(TaskType.Task, "Shorten", "Build it", 60_000,
            acceptanceCriteria: "works", contractOps: ["shorten-create"]));
        tasks.Insert(TaskRecord.Create(TaskType.Task, "Redirect", "Build it", 60_000,
            acceptanceCriteria: "works", contractOps: ["redirect-follow"]));

        Assert.True(CoverageGate.CheckContract(conn, _root).Complete);
    }

    [Fact]
    public void A_project_with_no_contract_is_complete()
    {
        // No HTTP surface is not a gap: a CLI project has nothing for these gates to check.
        WriteRequirements("01-something.md");
        using var conn = OpenBoard();

        Assert.True(CoverageGate.CheckContract(conn, _root).Complete);
    }

    [Fact]
    public void Contract_ops_survive_a_round_trip()
    {
        using var conn = OpenBoard();
        var tasks = new TaskRepository(conn);

        var id = tasks.Insert(TaskRecord.Create(
            TaskType.Task, "Both", "Build it", 60_000,
            acceptanceCriteria: "works", contractOps: ["shorten-create", "redirect-follow"])).Id;

        Assert.Equal(["shorten-create", "redirect-follow"], tasks.Get(id).ContractOps);
    }

    // ---------- the QA-time gate ----------

    [Fact]
    public void Declared_operations_are_read_from_the_test_source()
    {
        var dir = Path.Combine(_root, "tests", "acceptance");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ShortenTests.cs"), """
            public class ShortenTests
            {
                [Fact]
                [Trait("operation", "shorten-create")]
                public void Shorten_WithValidUrl_Returns201() { }

                [Fact]
                [Trait( "operation" , "redirect-follow" )]
                public void Redirect_WithKnownCode_Returns302() { }
            }
            """);

        var declared = AcceptanceSuite.DeclaredOperations(_root);

        Assert.Equal(2, declared.Count);
        Assert.Contains("shorten-create", declared);
        Assert.Contains("redirect-follow", declared);
    }

    [Fact]
    public void No_suite_declares_no_operations()
    {
        Assert.Empty(AcceptanceSuite.DeclaredOperations(_root));
        Assert.False(AcceptanceSuite.Exists(_root));
    }

    [Fact]
    public void A_suite_that_does_not_compile_is_not_run_rather_than_failed()
    {
        // A failed run becomes a bug against the product, and the Principal rejecting that bug
        // completes the project — so a suite that never compiled must report NotRun instead.
        WriteRunnableApp();
        var dir = Path.Combine(_root, "tests", "acceptance");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AcceptanceTests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Tests.cs"), "this is not valid C#");

        var result = AcceptanceSuite.Run(_root);

        Assert.False(result.Ran);
        Assert.False(result.Passed);
        Assert.Contains("does not compile", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_harness_scaffolds_the_suite_so_qa_never_picks_a_framework_or_a_package_version()
    {
        // A QA round invented a Microsoft.NET.Test.Sdk version that does not exist, and the two
        // rounds after it wrote differently-named projects beside the broken one.
        Assert.Null(AcceptanceSuite.EnsureScaffold(_root));

        var dir = Path.Combine(_root, "tests", "acceptance");
        Assert.True(File.Exists(Path.Combine(dir, "AcceptanceTests.csproj")));
        // The helper that hands QA a client, so it never has to read the variable itself.
        Assert.Contains(AcceptanceSuite.BaseUrlVariable, File.ReadAllText(Path.Combine(dir, "Api.cs")));
        // The template's placeholder would be a test covering no operation.
        Assert.False(File.Exists(Path.Combine(dir, "UnitTest1.cs")));
        Assert.Single(Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_existing_suite_is_left_alone()
    {
        // Round two must inherit the suite it wrote, not have it replaced underneath it.
        var dir = Path.Combine(_root, "tests", "acceptance");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Mine.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(dir, "MyTests.cs"), "// written last round");

        Assert.Null(AcceptanceSuite.EnsureScaffold(_root));

        Assert.True(File.Exists(Path.Combine(dir, "MyTests.cs")));
        Assert.False(File.Exists(Path.Combine(dir, "AcceptanceTests.csproj")));
    }

    /// <summary>A minimal runnable web project, so Discover finds something to start.</summary>
    private void WriteRunnableApp()
    {
        var dir = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
    }

    [Fact]
    public void A_suite_that_cannot_run_is_not_a_pass()
    {
        // Ran and Passed are deliberately separate: "nothing ran" must never be readable
        // as "everything passed", which is the whole failure this design exists to close.
        var result = AcceptanceSuite.Run(_root);

        Assert.False(result.Ran);
        Assert.False(result.Passed);
    }

    private void WriteContract(string yaml)
    {
        var file = Path.Combine(_root, ApiContract.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, yaml);
    }

    private void WriteRequirements(params string[] names)
    {
        var dir = Path.Combine(_root, "docs", "requirements");
        Directory.CreateDirectory(dir);
        foreach (var name in names) File.WriteAllText(Path.Combine(dir, name), $"# {name}\n");
    }

    private SqliteConnection OpenBoard() =>
        Database.OpenProject(Path.Combine(_root, $"board-{Guid.NewGuid():N}.db"));
}
