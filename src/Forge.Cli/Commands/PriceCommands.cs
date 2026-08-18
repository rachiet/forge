using System.ComponentModel;
using Forge.Core;
using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// What Forge thinks things cost. Worth running before a build rather than after:
/// with `--project` it resolves that project's provider and prices the model behind
/// every tier, so a model id that cannot be priced shows up here instead of stopping
/// a run partway through. Without one it reports the price table itself, which is the
/// only machine-wide thing here — a provider belongs to a project, so there is no
/// machine default to describe.
/// </summary>
public sealed class PricesShowCommand : Command<PricesShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--project <NAME>")]
        [Description("Price the models this project will actually run on. Without it, only the price table is reported.")]
        public string? Project { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var paths = ForgePaths.Resolve();

        // A provider belongs to a project, so tiers can only be resolved for one.
        string? provider = null;
        if (settings.Project is { Length: > 0 } name)
        {
            var dbPath = paths.ProjectDb(name);
            if (!File.Exists(dbPath))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]No project '{name}' at {dbPath}.[/]");
                return 1;
            }
            using var conn = Forge.Core.Db.Database.OpenProject(dbPath);
            try
            {
                provider = new ProjectSettings(conn).Provider;
            }
            catch (InvalidOperationException ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
                return 1;
            }
            AnsiConsole.MarkupLineInterpolated($"[bold]Provider[/]  {provider}  (project {name})");
        }

        var catalog = LlmSetup.Prices(paths);

        try
        {
            var snapshot = catalog.Snapshot;
            var age = catalog.Age();
            var staleness = catalog.IsStale() ? "[yellow](stale)[/]" : "[green](fresh)[/]";
            AnsiConsole.MarkupLine($"[bold]Prices[/]    {snapshot.Models.Count:N0} models, " +
                $"fetched {age.TotalHours:F1}h ago");
            AnsiConsole.MarkupLine($"           {staleness}  snapshot [grey]{snapshot.Id}[/]");
            AnsiConsole.MarkupLineInterpolated($"[grey]           {snapshot.Source}[/]\n");
        }
        catch (ModelNotPricedException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }

        if (provider is null)
        {
            AnsiConsole.MarkupLine("[grey]Pass --project <NAME> to see the models a project will "
                + "actually run on, and what they cost.[/]");
            return 0;
        }

        var client = LlmClientFactory.Create(provider);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Tier");
        table.AddColumn("Model");
        table.AddColumn(new TableColumn("Input /Mtok").RightAligned());
        table.AddColumn(new TableColumn("Output /Mtok").RightAligned());
        table.AddColumn(new TableColumn("Cache read").RightAligned());
        table.AddColumn(new TableColumn("Cache write").RightAligned());

        var failed = false;
        foreach (var tier in Enum.GetValues<ModelTier>())
        {
            var model = client.ModelFor(tier);
            try
            {
                var p = catalog.For(model);
                table.AddRow(
                    tier.ToString().ToLowerInvariant(),
                    Markup.Escape(model),
                    PerMillion(p.InputPerToken),
                    PerMillion(p.OutputPerToken),
                    p.CacheReadPerToken is { } r ? PerMillion(r) : "[grey]—[/]",
                    p.CacheWritePerToken is { } w ? PerMillion(w) : "[grey]—[/]");
            }
            catch (ModelNotPricedException)
            {
                failed = true;
                table.AddRow(
                    tier.ToString().ToLowerInvariant(),
                    Markup.Escape(model),
                    "[red]not priced[/]", "[red]—[/]", "[red]—[/]", "[red]—[/]");
            }
        }
        AnsiConsole.Write(table);

        if (failed)
        {
            AnsiConsole.MarkupLine("\n[red]At least one model has no price.[/] Forge refuses to run a " +
                "model it cannot price — fix the id in that provider's adapter tier map.");
            return 1;
        }
        return 0;
    }

    private static string PerMillion(decimal perToken) => $"${perToken * 1_000_000m:F2}";
}

/// <summary>Force a price refresh regardless of TTL.</summary>
public sealed class PricesUpdateCommand : Command<PricesUpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var paths = ForgePaths.Resolve();
        var catalog = LlmSetup.Prices(paths);

        try
        {
            var snapshot = catalog.Update();
            AnsiConsole.MarkupLineInterpolated(
                $"[green]Prices updated.[/] {snapshot.Models.Count:N0} models from {snapshot.Source}");
            AnsiConsole.MarkupLineInterpolated($"[grey]Cached at {paths.PriceCache}[/]");
            return 0;
        }
        catch (ModelNotPricedException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return 1;
        }
    }
}
