using System.ComponentModel;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Llm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>`forge init <name>` — creates a project's directory, database and bare repo.</summary>
public sealed class ProjectInitCommand : Command<ProjectInitCommand.Settings>
{
    /// <summary>The command's arguments.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Project name (letters, digits, '-', '_').")]
        public required string Name { get; init; }

        [CommandOption("-p|--provider <NAME>")]
        [Description("Whose models this project runs on. Required; it cannot be changed by a fallback later.")]
        public string? Provider { get; init; }

        [CommandOption("-b|--budget <USD>")]
        [Description("Hard spend cap in USD for this project.")]
        public decimal? BudgetUsd { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();

        // Asked for here rather than defaulted: the provider decides which models every
        // agent runs on, and a project that guessed one would bill the client for it.
        if (!LlmClientFactory.IsSupported(settings.Provider))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]--provider is required.[/] Supported: {string.Join(", ", LlmClientFactory.Supported)}.");
            return 1;
        }

        ProjectBootstrap.Init(paths, settings.Name);

        using (var conn = Database.OpenProject(paths.ProjectDb(settings.Name)))
        {
            _ = new ProjectSettings(conn)
            {
                Provider = settings.Provider!,
                BudgetUsd = settings.BudgetUsd,
            };
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Initialized project '{settings.Name}'[/] at {paths.ProjectDir(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  provider:   {settings.Provider}");
        AnsiConsole.MarkupLineInterpolated($"  db:         {paths.ProjectDb(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  bare repo:  {paths.ProjectBareRepo(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  workspaces: {paths.WorkspacesDir(settings.Name)}");
        return 0;
    }
}
