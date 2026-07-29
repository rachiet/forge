using System.Data;
using System.Globalization;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>
/// The client's choices for one project — what it may spend, and whose models it
/// runs on.
///
/// Stored in the project's own `project_meta`, not in the global registry and not in
/// `llm.json`: a project directory is meant to be self-contained and movable, and the
/// settings that govern a project belong beside the data they govern. It also fixes a
/// real bug — `llm.json` is machine-wide, so two projects could not have chosen
/// different providers, and the second choice silently applied to both.
/// </summary>
public sealed class ProjectSettings(IDbConnection conn)
{
    public const string ProviderKey = "llm_provider";
    public const string BudgetKey = "budget_usd";

    private readonly TaskRepository _meta = new(conn);

    /// <summary>The configured provider, or null to fall back to llm.json / the default.</summary>
    public string? Provider
    {
        get => _meta.GetMeta(ProviderKey) is { Length: > 0 } value ? value : null;
        set => _meta.SetMeta(ProviderKey, value ?? "");
    }

    /// <summary>
    /// The hard spend cap in USD, or null for uncapped. Persisted rather than passed
    /// per command: a budget that only exists as a CLI flag is no budget at all — the
    /// next run without the flag has no cap.
    /// </summary>
    public decimal? BudgetUsd
    {
        get => decimal.TryParse(_meta.GetMeta(BudgetKey), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        set => _meta.SetMeta(BudgetKey,
            value is { } v && v > 0 ? v.ToString(CultureInfo.InvariantCulture) : "");
    }
}
