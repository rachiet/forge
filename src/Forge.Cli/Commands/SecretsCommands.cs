using System.ComponentModel;
using Dapper;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>`forge secrets set <name>` — stores a client secret in the encrypted vault, prompting for the value.</summary>
public sealed class SecretsSetCommand : Command<SecretsSetCommand.Settings>
{
    /// <summary>The command's arguments.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Secret name (letters, digits, '_'). Agents reference it as {{secret:NAME}}.")]
        public required string Name { get; init; }

        [CommandOption("-d|--description <TEXT>")]
        public string? Description { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        SecretsVault.ValidateName(settings.Name);
        var value = AnsiConsole.Prompt(
            new TextPrompt<string>($"Value for [bold]{settings.Name}[/]:").Secret());

        var paths = ForgePaths.Resolve();
        new SecretsVault(paths.VaultDir).Set(settings.Name, value);

        using var global = Database.OpenGlobal(paths.GlobalDb);
        global.Execute("""
            INSERT INTO secrets_registry (name, description) VALUES (@Name, @Description)
            ON CONFLICT(name) DO UPDATE
              SET description = COALESCE(excluded.description, description),
                  provided_at = datetime('now')
            """, new { settings.Name, settings.Description });

        AnsiConsole.MarkupLineInterpolated(
            $"[green]Stored secret '{settings.Name}'.[/] Reference it as {{{{secret:{settings.Name}}}}}.");
        return 0;
    }
}

/// <summary>`forge secrets list` — lists the stored secrets by name, never their values.</summary>
public sealed class SecretsListCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        using var global = Database.OpenGlobal(paths.GlobalDb);
        var rows = global.Query<(string Name, string? Description, string ProvidedAt)>(
            "SELECT name, description, provided_at FROM secrets_registry ORDER BY name").ToList();

        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No secrets registered.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Name");
        table.AddColumn("Description");
        table.AddColumn("Provided (UTC)");
        foreach (var r in rows)
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Description ?? ""), Markup.Escape(r.ProvidedAt));
        AnsiConsole.Write(table);
        return 0;
    }
}
