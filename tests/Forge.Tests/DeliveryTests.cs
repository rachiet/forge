using Forge.Core.Board;

namespace Forge.Tests;

/// <summary>
/// Working out how the client starts the finished project, read from the checkout
/// rather than asked of an agent.
/// </summary>
public class DeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-deliver-{Guid.NewGuid():N}");

    public DeliveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp dir is disposable */ }
        GC.SuppressFinalize(this);
    }

    private void Project(string relativePath, string xml)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, xml);
    }

    private const string WebApp = """
        <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup>
          <TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """;

    private const string WasmApp = """
        <Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><PropertyGroup>
          <TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """;

    private const string ConsoleApp = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
          <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """;

    private const string Library = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
          <TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>
        """;

    /// <summary>The shape Forge's scaffold actually produces: no explicit OutputType.</summary>
    private const string TestProject = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
          <TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
            <PackageReference Include="xunit" Version="2.9.2" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>A test project that declares Exe — the Exe check alone would let it through.</summary>
    private const string ExplicitExeTestProject = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
          <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" /></ItemGroup>
        </Project>
        """;

    [Fact]
    public void Finds_the_web_app_and_ignores_its_tests()
    {
        // The shape Forge actually generates: one app under src/, one test project
        // under tests/. Running the test project would run the tests, not the product.
        Project("src/Weatherboard/Weatherboard.csproj", WasmApp);
        Project("tests/Weatherboard.Tests/Weatherboard.Tests.csproj", TestProject);

        var delivery = DeliveryPlan.For(_root);

        Assert.NotNull(delivery);
        Assert.Equal("dotnet run --project src/Weatherboard/Weatherboard.csproj", delivery.Command);
        Assert.Equal(_root, delivery.Directory);
    }

    [Fact]
    public void Finds_a_console_app()
    {
        Project("src/Tool/Tool.csproj", ConsoleApp);

        Assert.Equal("dotnet run --project src/Tool/Tool.csproj", DeliveryPlan.For(_root)?.Command);
    }

    [Fact]
    public void Finds_a_web_api()
    {
        Project("src/Api/Api.csproj", WebApp);

        Assert.Equal("dotnet run --project src/Api/Api.csproj", DeliveryPlan.For(_root)?.Command);
    }

    [Fact]
    public void A_library_only_repo_has_nothing_to_run()
    {
        // Nothing to hand over is a real outcome, not a failure: the harness says so
        // rather than inventing a command the client would watch fail.
        Project("src/Lib/Lib.csproj", Library);
        Project("tests/Lib.Tests/Lib.Tests.csproj", TestProject);

        Assert.Null(DeliveryPlan.For(_root));
    }

    [Fact]
    public void A_test_project_declaring_Exe_is_still_not_the_product()
    {
        Project("tests/Lib.Tests/Lib.Tests.csproj", ExplicitExeTestProject);

        Assert.Null(DeliveryPlan.For(_root));
    }

    [Fact]
    public void A_docs_only_repo_has_nothing_to_run()
    {
        File.WriteAllText(Path.Combine(_root, "PROJECT.md"), "# docs");

        Assert.Null(DeliveryPlan.For(_root));
    }

    [Fact]
    public void A_missing_checkout_has_nothing_to_run()
    {
        Assert.Null(DeliveryPlan.For(Path.Combine(_root, "never-created")));
    }

    [Fact]
    public void Malformed_project_files_are_skipped_rather_than_thrown_on()
    {
        Project("src/Broken/Broken.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><oops");
        Project("src/Good/Good.csproj", ConsoleApp);

        Assert.Equal("dotnet run --project src/Good/Good.csproj", DeliveryPlan.For(_root)?.Command);
    }
}
