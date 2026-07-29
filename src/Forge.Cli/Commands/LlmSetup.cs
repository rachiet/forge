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
    public static MeteredLlmClient Metered(
        ForgePaths paths, IDbConnection conn, decimal? projectBudgetUsd, ForgeLogger? logger = null)
    {
        var config = LlmConfig.Load(paths.DataRoot);
        return new MeteredLlmClient(
            LlmClientFactory.Create(config),
            conn,
            Prices(paths, logger),
            projectBudgetUsd);
    }

    public static PriceCatalog Prices(ForgePaths paths, ForgeLogger? logger = null) =>
        new(paths.PriceCache, logger: logger);

    public const string BudgetDescription =
        "Hard project-wide spend cap in USD (e.g. 25.00). Calls are refused once it is reached.";
}
