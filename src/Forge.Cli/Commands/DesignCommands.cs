using System.ComponentModel;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Design;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// The design phase (spec §12, M3): the Principal turns the PM's requirements into
/// structure, contracts, and a task DAG. Produces `created` tasks — nothing is
/// claimable until `forge design approve` records the client's sign-off.
/// </summary>
public sealed class DesignRunCommand : AsyncCommand<DesignRunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("--project-budget <TOKENS>")]
        [Description("Hard project-wide token cap. Calls are refused once it is reached.")]
        public long? ProjectBudget { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        var dbPath = paths.ProjectDb(settings.Project);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No project '{settings.Project}' at {dbPath}.[/]");
            return 1;
        }

        using var conn = Database.OpenProject(dbPath);
        using var sink = new FileLogSink(paths.ProjectLog(settings.Project));
        var design = new DesignPhase(
            paths, settings.Project, conn,
            new MeteredLlmClient(new AnthropicLlmClient(), conn, settings.ProjectBudget),
            new SecretsVault(paths.VaultDir), PromptLibrary.Resolve(),
            new ForgeLogger(sink, settings.Project));

        DesignOutcome outcome = default!;
        await AnsiConsole.Status().StartAsync("the Principal is designing…", async _ =>
        {
            outcome = await design.RunAsync(cancellationToken);
        });

        AnsiConsole.MarkupLineInterpolated($"\n[bold]Design ended[/] ({outcome.End}) — {outcome.TasksCreated} task(s) created.");
        AnsiConsole.MarkupLineInterpolated($"[grey]{outcome.Summary}[/]\n");

        RenderCoverage(outcome.Coverage);

        if (outcome.Coverage.Complete && outcome.TasksCreated > 0)
            AnsiConsole.MarkupLine("\n[green]Coverage complete.[/] Review the design, then " +
                $"[bold]forge design approve {settings.Project}[/] to release the tasks.");
        else if (!outcome.Coverage.Complete)
            AnsiConsole.MarkupLine("\n[yellow]Coverage incomplete[/] — some requirements have no task. " +
                "Re-run the design phase after the Principal fills the gaps.");

        return 0;
    }

    private static void RenderCoverage(CoverageReport coverage)
    {
        if (coverage.Requirements.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No requirement sections found to cover.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("Requirements coverage");
        table.AddColumn("Requirement");
        table.AddColumn("Covered");
        table.AddColumn("Tasks");
        foreach (var r in coverage.Requirements)
        {
            table.AddRow(
                Markup.Escape(r.File),
                r.Covered ? "[green]yes[/]" : "[red]no[/]",
                r.TaskIds.Count == 0 ? "[grey]—[/]" : string.Join(", ", r.TaskIds));
        }
        AnsiConsole.Write(table);
    }
}

/// <summary>The client sign-off gate: releases the design's `created` tasks to `ready`.</summary>
public sealed class DesignApproveCommand : Command<DesignApproveCommand.Settings>
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
        using var sink = new FileLogSink(paths.ProjectLog(settings.Project));
        var released = DesignPhase.Approve(conn, new ForgeLogger(sink, settings.Project));
        if (released == 0)
            AnsiConsole.MarkupLine("[grey]No tasks were awaiting sign-off.[/]");
        else
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Signed off.[/] {released} task(s) released to the board — run [bold]forge run {settings.Project}[/].");
        return 0;
    }
}
