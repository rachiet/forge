using Forge.Core.Agents;
using Forge.Core.Llm;

namespace Forge.Tests;

public class LlmConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"forge-llmconfig-{Guid.NewGuid():N}");

    public LlmConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LlmConfig.ProviderEnvVar, null);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_root, "llm.json"), json);

    [Fact]
    public void With_no_file_at_all_the_default_provider_applies()
    {
        var config = LlmConfig.Load(_root);

        Assert.Equal("anthropic", config.Provider);
        Assert.Null(config.Override(ModelTier.Coding));
    }

    [Fact]
    public void A_named_provider_and_per_tier_overrides_are_read()
    {
        WriteConfig("""
            { "provider": "anthropic",
              "models": { "reasoning": "claude-opus-4-8", "coding": "my-pinned-model" } }
            """);

        var config = LlmConfig.Load(_root);

        Assert.Equal("my-pinned-model", config.Override(ModelTier.Coding));
        Assert.Equal("claude-opus-4-8", config.Override(ModelTier.Reasoning));
        Assert.Null(config.Override(ModelTier.Fast));   // unnamed tiers keep the provider default
    }

    [Fact]
    public void The_environment_beats_the_file_for_the_provider()
    {
        WriteConfig("""{ "provider": "anthropic" }""");
        Environment.SetEnvironmentVariable(LlmConfig.ProviderEnvVar, "some-other");

        Assert.Equal("some-other", LlmConfig.Load(_root).Provider);
    }

    [Fact]
    public void Malformed_json_is_an_error_rather_than_a_silent_default()
    {
        WriteConfig("{ not json at all");

        var ex = Assert.Throws<InvalidOperationException>(() => LlmConfig.Load(_root));
        Assert.Contains("llm.json", ex.Message);
    }

    [Fact]
    public void An_unknown_provider_names_the_ones_that_exist()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => LlmClientFactory.Create(new LlmConfig { Provider = "gpt-please" }));

        Assert.Contains("gpt-please", ex.Message);
        Assert.Contains("anthropic", ex.Message);
    }

    [Fact]
    public void The_client_resolves_each_tier_and_honours_an_override()
    {
        var plain = LlmClientFactory.Create(new LlmConfig());
        Assert.Equal("claude-opus-4-8", plain.ModelFor(ModelTier.Reasoning));
        Assert.Equal("claude-sonnet-5", plain.ModelFor(ModelTier.Coding));
        Assert.Equal("claude-haiku-4-5", plain.ModelFor(ModelTier.Fast));

        var pinned = LlmClientFactory.Create(new LlmConfig
        {
            Models = new Dictionary<string, string> { ["coding"] = "pinned-coder" },
        });
        Assert.Equal("pinned-coder", pinned.ModelFor(ModelTier.Coding));
        Assert.Equal("claude-opus-4-8", pinned.ModelFor(ModelTier.Reasoning));
    }

    /// <summary>
    /// The point of the refactor: no recipe names a model, so a provider swap is a
    /// config edit. If this ever fails, a hardcoded id has crept back in.
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
