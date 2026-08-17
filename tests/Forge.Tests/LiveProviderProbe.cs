using Forge.Core.Configuration;
using Forge.Core.Llm;
using Forge.Core.Model;
using Xunit.Abstractions;

namespace Forge.Tests;

/// <summary>
/// Calls each provider for real and checks that a tool definition comes back as a parsed call.
/// It spends money and needs the network, so it is opt-in: every probe returns early unless
/// FORGE_LIVE_PROBE is set as well as the provider's own key. Set it when the adapters change.
/// </summary>
public class LiveProviderProbe(ITestOutputHelper output)
{
    private static readonly LlmToolDefinition ReadFile = new(
        "read_file", "Read a file from the workspace.",
        """{"type":"object","properties":{"path":{"type":"string","description":"the file to read"}},"required":["path"]}""");

    private static LlmRequest Probe(string model) => new()
    {
        Model = model,
        System = "You are a coding agent. Use the tools; do not answer in prose.",
        Messages = [new LlmMessage("user", "Read the file src/App/Program.cs.")],
        MaxTokens = 1024,
        Tools = [ReadFile],
        Attribution = new LlmAttribution("probe", AgentRole.Engineer, null),
    };

    /// <summary>The switch that turns the live probes on; without it the suite stays offline.</summary>
    private const string OptInVariable = "FORGE_LIVE_PROBE";

    /// <summary>True when the probes are switched on and this provider's key is available.</summary>
    private static bool Enabled(string keyVariable)
    {
        EnvFile.Load();
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OptInVariable))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(keyVariable));
    }

    private async Task CallsTheTool(ILlmClient client, string model)
    {
        var response = await client.CompleteAsync(Probe(model));

        output.WriteLine($"model={model} stop={response.StopReason} " +
                         $"in={response.Usage.TokensIn} out={response.Usage.TokensOut} " +
                         $"cacheRead={response.Usage.CacheReadTokens} calls={response.ToolCalls.Count}");
        foreach (var call in response.ToolCalls)
            output.WriteLine($"  → {call.Name}({call.ArgumentsJson}) id={call.Id}");
        if (response.Content.Length > 0) output.WriteLine($"  text: {response.Content[..Math.Min(200, response.Content.Length)]}");

        var made = Assert.Single(response.ToolCalls);
        Assert.Equal("read_file", made.Name);
        Assert.Contains("Program.cs", made.ArgumentsJson, StringComparison.Ordinal);
        Assert.True(response.Usage.TokensOut > 0, "no output tokens were reported");
    }

    [Fact]
    public async Task Anthropic_returns_a_parsed_tool_call()
    {
        if (!Enabled(AnthropicLlmClient.ApiKeyVariable)) { output.WriteLine("not enabled; skipped"); return; }
        var client = new AnthropicLlmClient();
        await CallsTheTool(client, client.ModelFor(ModelTier.Coding));
    }

    [Fact]
    public async Task OpenAi_returns_a_parsed_tool_call()
    {
        if (!Enabled(OpenAiLlmClient.ApiKeyVariable)) { output.WriteLine("not enabled; skipped"); return; }
        var client = new OpenAiLlmClient();
        await CallsTheTool(client, client.ModelFor(ModelTier.Coding));
    }

    [Fact]
    public async Task Gemini_returns_a_parsed_tool_call()
    {
        if (!Enabled(GeminiLlmClient.ApiKeyVariable)) { output.WriteLine("not enabled; skipped"); return; }
        var client = new GeminiLlmClient();
        await CallsTheTool(client, client.ModelFor(ModelTier.Coding));
    }
}
