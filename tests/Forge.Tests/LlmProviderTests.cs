using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;

namespace Forge.Tests;

/// <summary>
/// The provider is the whole of a project's model configuration: it is chosen once, at
/// creation, and each adapter answers for its own tiers from there. These protect the
/// two halves of that — a project cannot run without naming a provider, and no model id
/// can reach an adapter that did not supply it.
/// </summary>
public class LlmProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-provider-{Guid.NewGuid():N}");

    private readonly ForgePaths _paths;

    public LlmProviderTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new ForgePaths(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ProjectSettings Settings(string project)
    {
        ProjectBootstrap.Init(_paths, project);
        return new ProjectSettings(Database.OpenProject(_paths.ProjectDb(project)));
    }

    [Fact]
    public void Each_provider_answers_for_every_tier_out_of_its_own_map()
    {
        foreach (var provider in LlmClientFactory.Supported)
        {
            var client = LlmClientFactory.Create(provider);
            foreach (var tier in Enum.GetValues<ModelTier>())
                Assert.False(string.IsNullOrWhiteSpace(client.ModelFor(tier)), $"{provider}/{tier}");
        }
    }

    [Fact]
    public void The_provider_alone_decides_which_models_a_project_runs_on()
    {
        Assert.Equal("claude-opus-4-8", LlmClientFactory.Create("anthropic").ModelFor(ModelTier.Reasoning));
        Assert.Equal("claude-sonnet-5", LlmClientFactory.Create("anthropic").ModelFor(ModelTier.Coding));
        Assert.Equal("gpt-5.4", LlmClientFactory.Create("openai").ModelFor(ModelTier.Reasoning));
        Assert.Equal("gpt-5", LlmClientFactory.Create("openai").ModelFor(ModelTier.Coding));
    }

    /// <summary>
    /// The defect this design removes: a project that chose OpenAI once ran its coding
    /// tier on a Gemini model id, because a machine-wide file supplied the id and only
    /// the provider was overridden. No id survives a provider now, because none exists
    /// outside the adapter that will call it.
    /// </summary>
    [Fact]
    public void No_model_id_can_reach_an_adapter_that_did_not_supply_it()
    {
        var openai = LlmClientFactory.Create("openai");

        foreach (var tier in Enum.GetValues<ModelTier>())
            Assert.StartsWith("gpt-", openai.ModelFor(tier));
    }

    [Fact]
    public void Provider_aliases_resolve_to_the_right_adapter()
    {
        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create("claude"));
        Assert.IsType<OpenAiLlmClient>(LlmClientFactory.Create("OpenAI"));
        Assert.IsType<GeminiLlmClient>(LlmClientFactory.Create("google"));
    }

    [Fact]
    public void An_unknown_provider_names_the_ones_that_exist()
    {
        var ex = Assert.Throws<NotSupportedException>(() => LlmClientFactory.Create("gpt-please"));

        Assert.Contains("gpt-please", ex.Message);
        Assert.Contains("anthropic", ex.Message);
    }

    [Fact]
    public void A_project_with_no_provider_refuses_rather_than_guessing_one()
    {
        var settings = Settings("unset");

        var ex = Assert.Throws<InvalidOperationException>(() => settings.Provider);
        Assert.Contains("llm_provider", ex.Message);
        Assert.Contains("anthropic", ex.Message);
        Assert.Null(settings.ProviderOrNull);
    }

    [Fact]
    public void A_provider_that_is_not_one_of_the_three_is_refused_at_the_point_of_storing_it()
    {
        var settings = Settings("bogus");

        var ex = Assert.Throws<ArgumentException>(() => settings.Provider = "grok");
        Assert.Contains("grok", ex.Message);
        Assert.Contains("gemini", ex.Message);
    }

    [Fact]
    public void Each_project_keeps_its_own_provider()
    {
        var alpha = Settings("alpha");
        var beta = Settings("beta");

        alpha.Provider = "openai";
        beta.Provider = "gemini";

        Assert.Equal("openai", alpha.Provider);
        Assert.Equal("gemini", beta.Provider);
    }

    /// <summary>
    /// No recipe names a model, so a provider swap stays a project setting rather than a
    /// code change. If this ever fails, a hardcoded id has crept back in.
    /// </summary>
    [Fact]
    public void No_recipe_names_a_model_only_a_tier()
    {
        AgentRecipe[] recipes =
        [
            AgentRecipe.Engineer, AgentRecipe.Pm, AgentRecipe.Principal, AgentRecipe.PrincipalReview,
            AgentRecipe.PrincipalTriage, AgentRecipe.Qa, AgentRecipe.PrincipalImplementer,
        ];

        Assert.All(recipes, r => Assert.Contains(r.Tier, Enum.GetValues<ModelTier>()));
        Assert.Equal(ModelTier.Coding, AgentRecipe.Engineer.Tier);
        Assert.Equal(ModelTier.Reasoning, AgentRecipe.Principal.Tier);
    }
}
