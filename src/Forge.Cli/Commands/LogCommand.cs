using System.ComponentModel;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

public sealed class LogCommand : Command<LogCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("-t|--task <ID>")]
        [Description("Only show the trail for one task.")]
        public long? TaskId { get; init; }

        [CommandOption("-e|--events")]
        [Description("Show the full event stream (every tool call, transition, git op) instead of just messages.")]
        public bool Events { get; init; }

        [CommandOption("-d|--domain <DOMAIN>")]
        [Description("With --events, keep only one domain: message, tool, git, lifecycle, llm, error.")]
        public string? Domain { get; init; }
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

        if (settings.Events) return ShowEvents(paths, settings);

        using var conn = Database.OpenProject(dbPath);
        var messages = new MessageRepository(conn).Log(settings.TaskId);
        var ledger = new LedgerRepository(conn);

        if (messages.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No messages logged yet.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("When (UTC)");
            table.AddColumn("From → To");
            table.AddColumn("Type");
            table.AddColumn("Task");
            table.AddColumn("Message");
            foreach (var m in messages)
            {
                table.AddRow(
                    Markup.Escape(m.CreatedAt ?? ""),
                    Markup.Escape($"{m.FromAgent} → {m.ToAgent}"),
                    Markup.Escape(Forge.Core.Model.SnakeCaseEnum.ToSnakeCase(m.Type)),
                    m.TaskId?.ToString() ?? "-",
                    Markup.Escape(m.Payload));
            }
            AnsiConsole.Write(table);
        }

        var (tokensIn, tokensOut) = settings.TaskId is { } taskId
            ? ledger.TaskTotals(taskId)
            : ledger.ProjectTotals();
        var scope = settings.TaskId is { } t ? $" (task {t})" : "";
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]Token spend{scope}: {tokensIn:N0} in / {tokensOut:N0} out[/]");
        return 0;
    }

    /// <summary>
    /// The full narrative: every tool call, transition, and git op in order. Whole
    /// project with no --task; one task's slice with it — same rows, one filter,
    /// exactly as the log file stores them.
    /// </summary>
    private static int ShowEvents(ForgePaths paths, Settings settings)
    {
        var entries = (IEnumerable<Forge.Core.Logging.LogEntry>)
            LogReader.Read(paths.ProjectLog(settings.Project), settings.TaskId);

        // A domain filter is now an equality check on the domain column — this is
        // the "skip the tool calls, show me just the messages" case.
        if (settings.Domain is { Length: > 0 } domain)
            entries = entries.Where(e => e.Type.Domain() == domain);

        var rows = entries.ToList();
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matching events logged yet.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Time (UTC)");
        table.AddColumn("Task");
        table.AddColumn("Domain");
        table.AddColumn("Action");
        table.AddColumn("Message");
        foreach (var e in rows)
        {
            table.AddRow(
                Markup.Escape(e.Timestamp.ToString("HH:mm:ss")),
                e.Task?.ToString() ?? "[grey]—[/]",
                Markup.Escape(e.Type.Domain()),
                e.Type.Action() is { Length: > 0 } action ? Markup.Escape(action) : "[grey]—[/]",
                Markup.Escape(e.Message));
        }

        var label = settings.TaskId is { } t ? $"task {t}" : $"project '{settings.Project}'";
        var domainNote = settings.Domain is { Length: > 0 } d ? $", domain '{d}'" : "";
        AnsiConsole.MarkupLineInterpolated($"[grey]Event stream for {label}{domainNote}: {rows.Count} entries[/]\n");
        AnsiConsole.Write(table);
        return 0;
    }
}
