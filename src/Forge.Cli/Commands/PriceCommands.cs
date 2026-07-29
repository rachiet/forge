using Forge.Core;
using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// What Forge thinks things cost. Worth running before a build rather than after:
/// it resolves the configured provider's model for every tier against the price
/// table, so a model id that cannot be priced shows up here instead of stopping a
/// run partway through.
/// </summary>
public sealed class PricesShowCommand : Command<PricesShowCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var paths = ForgePaths.Resolve();
        var config = LlmConfig.Load(paths.DataRoot);
        var catalog = LlmSetup.Prices(paths);
        var client = LlmClientFactory.Create(config);

        AnsiConsole.MarkupLineInterpolated($"[bold]Provider[/]  {config.Provider}");

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
                "model it cannot price — fix the id in llm.json or the agent recipe.");
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
