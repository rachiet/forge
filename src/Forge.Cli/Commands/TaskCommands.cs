using System.ComponentModel;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Model;
using Spectre.Console;
using Spectre.Console.Cli;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Cli.Commands;

/// <summary>
/// Puts work on the board by hand. In M3 the Principal generates tasks from the
/// design; until then this is how a task gets created.
/// </summary>
public sealed class TaskAddCommand : Command<TaskAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandArgument(1, "<title>")]
        [Description("Short task title; also becomes the branch slug.")]
        public required string Title { get; init; }

        [CommandOption("-o|--objective <TEXT>")]
        [Description("What the agent must accomplish. Defaults to the title.")]
        public string? Objective { get; init; }

        [CommandOption("-a|--acceptance <TEXT>")]
        [Description("Acceptance criteria the work is checked against.")]
        public string? Acceptance { get; init; }

        [CommandOption("-t|--type <TYPE>")]
        [Description("task | bug | chore (feature is the Principal's own unit). Default: task.")]
        public string Type { get; init; } = "task";

        [CommandOption("-b|--budget <TOKENS>")]
        [Description("Token budget for the task. Enforced by refusing calls, not by asking the model.")]
        public int Budget { get; init; } = 60_000;

        [CommandOption("-c|--context <PATH>")]
        [Description("Repo-relative path the agent should start from. Repeatable.")]
        public string[] ContextPaths { get; init; } = [];

        [CommandOption("--ready")]
        [Description("Mark the task ready to claim immediately.")]
        public bool Ready { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        var dbPath = paths.ProjectDb(settings.Project);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No project '{settings.Project}' at {dbPath}.[/]");
            return 1;
        }

        using var conn = Database.OpenProject(dbPath);
        var tasks = new TaskRepository(conn);

        var task = tasks.Insert(TaskRecord.Create(
            SnakeCaseEnum.Parse<TaskType>(settings.Type),
            settings.Title,
            settings.Objective ?? settings.Title,
            settings.Budget,
            acceptanceCriteria: settings.Acceptance,
            contextPaths: settings.ContextPaths,
            assignedRole: AgentRole.Engineer,
            createdBy: "human"));

        if (settings.Ready) tasks.Transition(task.Id, TaskStatus.Ready);

        var state = settings.Ready ? "ready" : "created — mark it ready to let a worker claim it";
        AnsiConsole.MarkupLineInterpolated($"Created task [green]{task.Id}[/]: {settings.Title} ({state})");
        return 0;
    }
}

public sealed class TaskListCommand : Command<TaskListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        var dbPath = paths.ProjectDb(settings.Project);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No project '{settings.Project}' at {dbPath}.[/]");
            return 1;
        }

        using var conn = Database.OpenProject(dbPath);
        var tasks = new TaskRepository(conn).List();
        if (tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]The board is empty.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("Status");
        table.AddColumn("Role");
        table.AddColumn("Title");
        table.AddColumn("Budget");
        table.AddColumn("Progress note");
        foreach (var t in tasks)
        {
            table.AddRow(
                t.Id.ToString(),
                Markup.Escape(SnakeCaseEnum.ToSnakeCase(t.Status)),
                Markup.Escape(t.AssignedRole is { } r ? SnakeCaseEnum.ToSnakeCase(r) : "-"),
                Markup.Escape(t.Title),
                $"{t.TokensSpent:N0}/{t.TokenBudget:N0}",
                Markup.Escape(Shorten(t.ProgressNote)));
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static string Shorten(string? note) => note switch
    {
        null or "" => "-",
        { Length: > 60 } => note[..60] + "…",
        _ => note,
    };
}
