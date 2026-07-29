using System.Text.Json;
using Forge.Core.Llm;
using Forge.Core.Model;

namespace Forge.Tests;

/// <summary>
/// The adapters' whole job is translation, so translation is what these assert —
/// request shape out, usage shape in — against payloads matching the providers'
/// published schemas (openai-openapi CompletionUsage, Gemini v1beta UsageMetadata).
/// Building and parsing are pure functions precisely so this needs no network.
/// </summary>
public class ProviderAdapterTests
{
    private static LlmRequest Request(string model, string? system = "SYSTEM") => new()
    {
        Model = model,
        System = system,
        Messages = [new LlmMessage("user", "first"), new LlmMessage("assistant", "reply"), new LlmMessage("user", "second")],
        MaxTokens = 4096,
        Attribution = new LlmAttribution("eng-1", AgentRole.Engineer, 1),
    };

    // ---------- OpenAI ----------

    [Fact]
    public void OpenAi_sends_the_system_prompt_as_a_message_and_caps_completion_tokens()
    {
        var body = JsonDocument.Parse(OpenAiLlmClient.BuildBody(Request("gpt-5"))).RootElement;

        Assert.Equal("gpt-5", body.GetProperty("model").GetString());
        // max_completion_tokens, not the deprecated max_tokens — it also bounds reasoning tokens.
        Assert.Equal(4096, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));

        var messages = body.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("second", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAi_omits_the_system_message_when_there_is_no_system_prompt()
    {
        var body = JsonDocument.Parse(OpenAiLlmClient.BuildBody(Request("gpt-5", system: null))).RootElement;

        var messages = body.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void OpenAi_subtracts_cached_tokens_so_TokensIn_means_the_uncached_remainder()
    {
        // prompt_tokens is the WHOLE prompt; cached_tokens is a subset of it.
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "choices": [{ "message": { "role": "assistant", "content": "hello" }, "finish_reason": "stop" }],
              "usage": {
                "prompt_tokens": 10000, "completion_tokens": 500, "total_tokens": 10500,
                "prompt_tokens_details": { "cached_tokens": 8000 }
              }
            }
            """);

        Assert.Equal("hello", response.Content);
        Assert.Equal("stop", response.StopReason);
        Assert.Equal(2000, response.Usage.TokensIn);
        Assert.Equal(500, response.Usage.TokensOut);
        Assert.Equal(8000, response.Usage.CacheReadTokens);

        // The whole prompt is recoverable, the invariant LlmUsage documents.
        Assert.Equal(10000, response.Usage.TokensIn + response.Usage.CacheReadTokens);
    }

    [Fact]
    public void OpenAi_keeps_cache_write_tokens_inside_TokensIn_because_they_bill_as_input()
    {
        // OpenAI reports cache_write_tokens but publishes no separate cache-write rate;
        // mapping them to CacheWriteTokens would ask the pricer for a rate that does not
        // exist and throw. They are ordinary input, so they stay in TokensIn.
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "choices": [{ "message": { "content": "x" }, "finish_reason": "stop" }],
              "usage": {
                "prompt_tokens": 5000, "completion_tokens": 10,
                "prompt_tokens_details": { "cached_tokens": 1000, "cache_write_tokens": 4000 }
              }
            }
            """);

        Assert.Equal(4000, response.Usage.TokensIn);
        Assert.Equal(1000, response.Usage.CacheReadTokens);
        Assert.Equal(0, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public void OpenAi_survives_a_response_with_no_usage_or_choices()
    {
        var response = OpenAiLlmClient.ParseResponse("""{ "id": "x" }""");

        Assert.Equal("", response.Content);
        Assert.Equal(0, response.Usage.TokensIn);
    }

    // ---------- Gemini ----------

    [Fact]
    public void Gemini_uses_systemInstruction_parts_and_the_model_role()
    {
        var body = JsonDocument.Parse(GeminiLlmClient.BuildBody(Request("gemini/gemini-2.5-pro"))).RootElement;

        Assert.Equal("SYSTEM",
            body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(4096, body.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());

        var contents = body.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());   // not "assistant"
        Assert.Equal("second", contents[2].GetProperty("parts")[0].GetProperty("text").GetString());

        // The model id never appears in the body — it rides in the URL path.
        Assert.False(body.TryGetProperty("model", out _));
    }

    [Theory]
    [InlineData("gemini/gemini-2.5-pro", "gemini-2.5-pro")]
    [InlineData("gemini-2.5-flash", "gemini-2.5-flash")]
    public void Gemini_strips_the_price_table_prefix_for_the_wire_call(string canonical, string wire) =>
        Assert.Equal(wire, GeminiLlmClient.WireModel(canonical));

    [Fact]
    public void Gemini_counts_thinking_tokens_as_output_and_subtracts_cached_prompt()
    {
        var response = GeminiLlmClient.ParseResponse("""
            {
              "candidates": [{
                "content": { "role": "model", "parts": [{ "text": "answer" }] },
                "finishReason": "STOP"
              }],
              "usageMetadata": {
                "promptTokenCount": 12000, "cachedContentTokenCount": 9000,
                "candidatesTokenCount": 400, "thoughtsTokenCount": 250,
                "totalTokenCount": 12650
              }
            }
            """);

        Assert.Equal("answer", response.Content);
        Assert.Equal("STOP", response.StopReason);
        Assert.Equal(3000, response.Usage.TokensIn);          // 12000 − 9000
        Assert.Equal(650, response.Usage.TokensOut);          // 400 candidates + 250 thoughts
        Assert.Equal(9000, response.Usage.CacheReadTokens);
        Assert.Equal(0, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public void Gemini_drops_thought_parts_from_the_assistant_text()
    {
        // A thought part is private reasoning, not the answer; echoing it back into the
        // conversation would corrupt the loop.
        var response = GeminiLlmClient.ParseResponse("""
            {
              "candidates": [{
                "content": { "parts": [
                  { "text": "private musing", "thought": true },
                  { "text": "the answer" }
                ] },
                "finishReason": "STOP"
              }],
              "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 5 }
            }
            """);

        Assert.Equal("the answer", response.Content);
    }

    [Fact]
    public void Gemini_survives_a_blocked_response_with_no_candidates()
    {
        var response = GeminiLlmClient.ParseResponse(
            """{ "promptFeedback": { "blockReason": "SAFETY" } }""");

        Assert.Equal("", response.Content);
        Assert.Equal(0, response.Usage.TokensOut);
    }

    // ---------- shared contract ----------

    [Fact]
    public void Every_provider_resolves_all_three_tiers_and_honours_overrides()
    {
        foreach (var provider in LlmClientFactory.Supported)
        {
            var client = LlmClientFactory.Create(new LlmConfig { Provider = provider });
            foreach (var tier in Enum.GetValues<ModelTier>())
                Assert.False(string.IsNullOrWhiteSpace(client.ModelFor(tier)), $"{provider}/{tier}");

            var pinned = LlmClientFactory.Create(new LlmConfig
            {
                Provider = provider,
                Models = new Dictionary<string, string> { ["reasoning"] = "pinned" },
            });
            Assert.Equal("pinned", pinned.ModelFor(ModelTier.Reasoning));
        }
    }

    [Fact]
    public void Provider_aliases_resolve_to_the_right_adapter()
    {
        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "claude" }));
        Assert.IsType<OpenAiLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "OpenAI" }));
        Assert.IsType<GeminiLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "google" }));
    }
}
