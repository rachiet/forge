using System.ComponentModel;
using Forge.Core;
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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        ProjectBootstrap.Init(paths, settings.Name);
        AnsiConsole.MarkupLineInterpolated(
            $"[green]Initialized project '{settings.Name}'[/] at {paths.ProjectDir(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  db:         {paths.ProjectDb(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  bare repo:  {paths.ProjectBareRepo(settings.Name)}");
        AnsiConsole.MarkupLineInterpolated($"  workspaces: {paths.WorkspacesDir(settings.Name)}");
        return 0;
    }
}
