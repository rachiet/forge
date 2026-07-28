using Dapper;
using Forge.Core.Db;
using Forge.Core.Model;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

public class TypeHandlerTests
{
    private sealed record HandlerProbe
    {
        public IReadOnlyList<string> Paths { get; init; } = [];
        public RequirementsRef? Ref { get; init; }
    }

    [Fact]
    public void Json_list_and_requirements_ref_round_trip_through_sqlite()
    {
        using var conn = Database.Open(":memory:");
        conn.Execute("CREATE TABLE probe (paths TEXT, ref TEXT)");
        conn.Execute(
            "INSERT INTO probe (paths, ref) VALUES (@Paths, @Ref)",
            new
            {
                Paths = (IReadOnlyList<string>)["src/a.cs", "docs/design/02-data-model.md"],
                Ref = (RequirementsRef?)RequirementsRef.Parse("02-todos-read.md@v3"),
            });

        var raw = conn.QuerySingle<(string, string)>("SELECT paths, ref FROM probe");
        Assert.Equal("""["src/a.cs","docs/design/02-data-model.md"]""", raw.Item1);
        Assert.Equal("02-todos-read.md@v3", raw.Item2);

        var probe = conn.QuerySingle<HandlerProbe>("SELECT paths AS Paths, ref AS Ref FROM probe");
        Assert.Equal(["src/a.cs", "docs/design/02-data-model.md"], probe.Paths);
        Assert.Equal(new RequirementsRef("02-todos-read.md", 3), probe.Ref);
    }

    [Theory]
    [InlineData(TaskStatus.InReview, "in_review")]
    [InlineData(TaskStatus.Qa, "qa")]
    [InlineData(TaskStatus.InProgress, "in_progress")]
    public void Task_status_maps_to_snake_case(TaskStatus status, string text)
    {
        Assert.Equal(text, SnakeCaseEnum.ToSnakeCase(status));
        Assert.Equal(status, SnakeCaseEnum.Parse<TaskStatus>(text));
    }

    [Theory]
    [InlineData(MessageType.ChangeRequest, "change_request")]
    [InlineData(MessageType.SystemNudge, "system_nudge")]
    [InlineData(MilestoneStatus.DemoReady, "demo_ready")]
    public void Multi_word_enums_map_to_snake_case<T>(T value, string text) where T : struct, Enum
    {
        Assert.Equal(text, SnakeCaseEnum.ToSnakeCase(value));
        Assert.Equal(value, SnakeCaseEnum.Parse<T>(text));
    }

    [Theory]
    [InlineData("InReview")]
    [InlineData("IN_REVIEW")]
    [InlineData("inreview")]
    [InlineData("nonsense")]
    [InlineData("")]
    public void Parse_rejects_non_snake_case_text(string text) =>
        Assert.Throws<FormatException>(() => SnakeCaseEnum.Parse<TaskStatus>(text));
}
