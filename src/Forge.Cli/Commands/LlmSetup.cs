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
    /// The project's own settings win over the machine's. `llm.json` is one file for
    /// the whole data root, so without this every project on the machine would share
    /// one provider — and the second project created would silently run on the first
    /// one's models. Likewise the budget: a cap that lives only in a CLI flag is no cap
    /// at all on the next run that omits it.
    /// </summary>
    public static MeteredLlmClient Metered(
        ForgePaths paths, IDbConnection conn, decimal? projectBudgetUsd = null, ForgeLogger? logger = null)
    {
        var settings = new ProjectSettings(conn);
        var config = LlmConfig.Load(paths.DataRoot, settings.Provider);

        return new MeteredLlmClient(
            LlmClientFactory.Create(config),
            conn,
            Prices(paths, logger),
            projectBudgetUsd ?? settings.BudgetUsd);
    }

    public static PriceCatalog Prices(ForgePaths paths, ForgeLogger? logger = null) =>
        new(paths.PriceCache, logger: logger);

    public const string BudgetDescription =
        "Hard project-wide spend cap in USD (e.g. 25.00). Calls are refused once it is reached.";
}
