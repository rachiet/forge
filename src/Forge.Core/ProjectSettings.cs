using System.Data;
using System.Globalization;
using Forge.Core.Db;

namespace Forge.Core;

/// <summary>
/// The client's choices for one project — what it may spend, and whose models it
/// runs on.
///
/// Stored in the project's own `project_meta`: a project directory is meant to be
/// self-contained and movable, and the settings that govern a project belong beside
/// the data they govern. The provider is the only model configuration there is — it
/// is chosen once, at creation, and each adapter answers for its own tiers from
/// there.
/// </summary>
public sealed class ProjectSettings(IDbConnection conn)
{
    public const string ProviderKey = "llm_provider";
    public const string BudgetKey = "budget_usd";

    private readonly ProjectMetaRepository _meta = new(conn);

    /// <summary>
    /// Whose models this project runs on, chosen by the client when the project was
    /// created. Required: there is no machine-wide default to fall back to, and a
    /// project that cannot name its provider cannot be priced or run, so reading one
    /// that is missing or unrecognised throws rather than guessing.
    /// </summary>
    public string Provider
    {
        get => Llm.LlmClientFactory.IsSupported(_meta.Get(ProviderKey))
            ? _meta.Get(ProviderKey)!
            : throw new InvalidOperationException(
                $"This project has no valid '{ProviderKey}'. It is chosen when the project is "
              + $"created; supported providers are {string.Join(", ", Llm.LlmClientFactory.Supported)}.");
        set => _meta.Set(ProviderKey, Llm.LlmClientFactory.IsSupported(value)
            ? value
            : throw new ArgumentException(
                $"Unknown provider '{value}'. Supported: "
              + $"{string.Join(", ", Llm.LlmClientFactory.Supported)}.", nameof(value)));
    }

    /// <summary>
    /// The stored provider without the requirement, for callers that must render a
    /// project they cannot run — the board listing every project, for instance.
    /// </summary>
    public string? ProviderOrNull => _meta.Get(ProviderKey) is { Length: > 0 } value ? value : null;

    /// <summary>
    /// The hard spend cap in USD, or null for uncapped. Persisted rather than passed
    /// per command: a budget that only exists as a CLI flag is no budget at all — the
    /// next run without the flag has no cap.
    /// </summary>
    public decimal? BudgetUsd
    {
        get => decimal.TryParse(_meta.Get(BudgetKey), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
        set => _meta.Set(BudgetKey,
            value is { } v && v > 0 ? v.ToString(CultureInfo.InvariantCulture) : "");
    }
}
