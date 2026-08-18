using System.Data;
using Forge.Core;
using Forge.Core.Llm;
using Forge.Core.Llm.Pricing;
using Forge.Core.Logging;

namespace Forge.Cli.Commands;

/// <summary>
/// One place where a supervised LLM client is assembled, so the provider, the price
/// table and the budget are wired identically for `run`, `chat` and `design`. The
/// undecorated adapter never escapes this file — every call an agent makes goes
/// through the supervisor.
/// </summary>
internal static class LlmSetup
{
    /// <summary>
    /// Everything comes from the project's own settings. The provider it was created
    /// with picks the adapter, and the adapter answers for its own tiers — so two
    /// projects on one machine can run on different providers and neither can end up
    /// asking one provider for another's model. Likewise the budget: a cap that lives
    /// only in a CLI flag is no cap at all on the next run that omits it.
    /// </summary>
    public static MeteredLlmClient Metered(
        ForgePaths paths, IDbConnection conn, decimal? projectBudgetUsd = null, ForgeLogger? logger = null)
    {
        var settings = new ProjectSettings(conn);

        return new MeteredLlmClient(
            // Retry sits inside metering: the budget is checked once per logical call,
            // and only the attempt that returns is billed to the ledger.
            new RetryingLlmClient(LlmClientFactory.Create(settings.Provider), logger),
            conn,
            Prices(paths, logger),
            // An explicit CLI flag is a fixed cap for this invocation; otherwise the
            // stored setting is read per call, so the board's Raise button takes
            // effect on a build already in flight.
            projectBudgetUsd,
            liveBudgetUsd: () => settings.BudgetUsd);
    }

    public static PriceCatalog Prices(ForgePaths paths, ForgeLogger? logger = null) =>
        new(paths.PriceCache, logger: logger);

    public const string BudgetDescription =
        "Hard project-wide spend cap in USD (e.g. 25.00). Calls are refused once it is reached.";
}
