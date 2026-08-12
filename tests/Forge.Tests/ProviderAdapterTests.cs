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
    public void OpenAi_sends_the_system_prompt_as_instructions_and_caps_output_tokens()
    {
        var body = JsonDocument.Parse(OpenAiLlmClient.BuildBody(Request("gpt-5"))).RootElement;

        Assert.Equal("gpt-5", body.GetProperty("model").GetString());
        // max_output_tokens bounds reasoning tokens as well as visible output.
        Assert.Equal(4096, body.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("SYSTEM", body.GetProperty("instructions").GetString());

        var input = body.GetProperty("input");
        Assert.Equal(3, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("assistant", input[1].GetProperty("role").GetString());
        Assert.Equal("second", input[2].GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAi_omits_instructions_when_there_is_no_system_prompt()
    {
        var body = JsonDocument.Parse(OpenAiLlmClient.BuildBody(Request("gpt-5", system: null))).RootElement;

        Assert.False(body.TryGetProperty("instructions", out _));
        Assert.Equal(3, body.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void OpenAi_sends_tool_schemas_and_no_tools_field_when_there_are_none()
    {
        var plain = JsonDocument.Parse(OpenAiLlmClient.BuildBody(Request("gpt-5"))).RootElement;
        Assert.False(plain.TryGetProperty("tools", out _));

        var withTools = Request("gpt-5") with
        {
            Tools = [new LlmToolDefinition("read_file", "read a file",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")],
        };
        var body = JsonDocument.Parse(OpenAiLlmClient.BuildBody(withTools)).RootElement;

        var tool = body.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("name").GetString());
        // The schema is sent as JSON, not as a string the model would have to parse.
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        // strict would require every property to be required; our tools have optional ones.
        Assert.False(tool.TryGetProperty("strict", out _));
    }

    [Fact]
    public void OpenAi_round_trips_a_call_and_its_result_as_input_items()
    {
        var request = Request("gpt-5") with
        {
            Messages =
            [
                new LlmMessage("user", "do it"),
                new LlmMessage("assistant", "") { ToolCalls = [new LlmToolCall("call_1", "read_file", """{"path":"a.cs"}""")] },
                new LlmMessage("user", "") { ToolResults = [new LlmToolResult("call_1", "read_file", "file body")] },
            ],
        };

        var input = JsonDocument.Parse(OpenAiLlmClient.BuildBody(request)).RootElement.GetProperty("input");

        // An assistant turn with no text contributes only its call, so no empty message is sent.
        Assert.Equal(3, input.GetArrayLength());
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("""{"path":"a.cs"}""", input[1].GetProperty("arguments").GetString());
        Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
        Assert.Equal("call_1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("file body", input[2].GetProperty("output").GetString());
    }

    [Fact]
    public void OpenAi_reads_tool_calls_out_of_the_output_array()
    {
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "status": "completed",
              "output": [
                { "type": "message", "content": [{ "type": "output_text", "text": "on it" }] },
                { "id": "fc_1", "call_id": "call_1", "type": "function_call",
                  "name": "read_file", "arguments": "{\"path\":\"src/App/Program.cs\"}" }
              ],
              "usage": { "input_tokens": 100, "output_tokens": 20 }
            }
            """);

        Assert.Equal("on it", response.Content);
        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("""{"path":"src/App/Program.cs"}""", call.ArgumentsJson);
    }

    [Fact]
    public void OpenAi_subtracts_cached_tokens_so_TokensIn_means_the_uncached_remainder()
    {
        // input_tokens is the WHOLE prompt; cached_tokens is a subset of it.
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "status": "completed",
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": "hello" }] }],
              "usage": {
                "input_tokens": 10000, "output_tokens": 500, "total_tokens": 10500,
                "input_tokens_details": { "cached_tokens": 8000 }
              }
            }
            """);

        Assert.Equal("hello", response.Content);
        Assert.Equal("completed", response.StopReason);
        Assert.Equal(2000, response.Usage.TokensIn);
        Assert.Equal(500, response.Usage.TokensOut);
        Assert.Equal(8000, response.Usage.CacheReadTokens);
        Assert.Equal(0, response.Usage.CacheWriteTokens);

        // The whole prompt is recoverable, the invariant LlmUsage documents.
        Assert.Equal(10000, response.Usage.TokensIn + response.Usage.CacheReadTokens);
    }

    [Fact]
    public void OpenAi_reports_why_a_response_was_cut_short()
    {
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "status": "incomplete",
              "incomplete_details": { "reason": "max_output_tokens" },
              "output": [],
              "usage": { "input_tokens": 5, "output_tokens": 4096 }
            }
            """);

        Assert.Equal("max_output_tokens", response.StopReason);
    }

    [Fact]
    public void OpenAi_survives_a_null_incomplete_details_which_a_completed_response_carries()
    {
        var response = OpenAiLlmClient.ParseResponse("""
            {
              "status": "completed",
              "incomplete_details": null,
              "output": [{ "type": "message", "content": [{ "type": "output_text", "text": "hi" }] }],
              "usage": { "input_tokens": 5, "output_tokens": 2 }
            }
            """);

        Assert.Equal("completed", response.StopReason);
        Assert.Equal("hi", response.Content);
    }

    [Fact]
    public void OpenAi_survives_a_response_with_no_usage_or_output()
    {
        var response = OpenAiLlmClient.ParseResponse("""{ "id": "x" }""");

        Assert.Equal("", response.Content);
        Assert.Empty(response.ToolCalls);
        Assert.Equal(0, response.Usage.TokensIn);
    }

    [Fact]
    public void Gemini_sends_tool_schemas_as_function_declarations()
    {
        var plain = JsonDocument.Parse(GeminiLlmClient.BuildBody(Request("gemini/gemini-2.5-flash"))).RootElement;
        Assert.False(plain.TryGetProperty("tools", out _));

        var withTools = Request("gemini/gemini-2.5-flash") with
        {
            Tools = [new LlmToolDefinition("read_file", "read a file",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")],
        };
        var body = JsonDocument.Parse(GeminiLlmClient.BuildBody(withTools)).RootElement;

        var declaration = body.GetProperty("tools")[0].GetProperty("function_declarations")[0];
        Assert.Equal("read_file", declaration.GetProperty("name").GetString());
        Assert.Equal("object", declaration.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void Gemini_round_trips_a_call_and_its_result_as_parts()
    {
        var request = Request("gemini/gemini-2.5-flash") with
        {
            Messages =
            [
                new LlmMessage("user", "do it"),
                new LlmMessage("assistant", "") { ToolCalls = [new LlmToolCall("read_file", "read_file", """{"path":"a.cs"}""")] },
                new LlmMessage("user", "") { ToolResults = [new LlmToolResult("read_file", "read_file", "file body")] },
            ],
        };

        var contents = JsonDocument.Parse(GeminiLlmClient.BuildBody(request)).RootElement.GetProperty("contents");

        Assert.Equal(3, contents.GetArrayLength());
        var call = contents[1].GetProperty("parts")[0].GetProperty("functionCall");
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("read_file", call.GetProperty("name").GetString());
        Assert.Equal("a.cs", call.GetProperty("args").GetProperty("path").GetString());

        // A result speaks as `user`, and `response` is an object rather than a bare string.
        var result = contents[2].GetProperty("parts")[0].GetProperty("functionResponse");
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
        Assert.Equal("file body", result.GetProperty("response").GetProperty("result").GetString());
    }

    [Fact]
    public void Gemini_reads_a_function_call_part_and_keys_it_by_name()
    {
        var response = GeminiLlmClient.ParseResponse("""
            {
              "candidates": [{
                "finishReason": "STOP",
                "content": { "parts": [
                  { "text": "on it" },
                  { "functionCall": { "name": "read_file", "args": { "path": "src/App/Program.cs" } } }
                ]}
              }],
              "usageMetadata": { "promptTokenCount": 100, "candidatesTokenCount": 20 }
            }
            """);

        Assert.Equal("on it", response.Content);
        var call = Assert.Single(response.ToolCalls);
        // This API issues no call id, so the name carries the correlation both ways.
        Assert.Equal("read_file", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("src/App/Program.cs",
            JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("path").GetString());
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

    // ---------- Anthropic ----------

    /// <summary>The content blocks of a built message, whatever concrete list the SDK used.</summary>
    private static IReadOnlyList<Anthropic.Models.Messages.ContentBlockParam> Blocks(
        Anthropic.Models.Messages.MessageParam message) =>
        [.. (IEnumerable<Anthropic.Models.Messages.ContentBlockParam>)message.Content.Value!];

    [Fact]
    public void Anthropic_splits_the_json_schema_into_properties_and_required()
    {
        var tool = AnthropicLlmClient.ToSdkTool(new LlmToolDefinition("read_file", "read a file",
            """{"type":"object","properties":{"path":{"type":"string"},"start":{"type":"integer"}},"required":["path"]}"""));

        var declared = Assert.IsType<Anthropic.Models.Messages.Tool>(tool.Value);
        Assert.Equal("read_file", declared.Name);
        Assert.Equal(["path", "start"], declared.InputSchema.Properties!.Keys.OrderBy(k => k));
        Assert.Equal(["path"], declared.InputSchema.Required!);
    }

    [Fact]
    public void Anthropic_sends_a_call_as_a_tool_use_block_and_a_result_as_a_tool_result_block()
    {
        var call = AnthropicLlmClient.ToSdkMessage(
            new LlmMessage("assistant", "") { ToolCalls = [new LlmToolCall("toolu_1", "read_file", """{"path":"a.cs"}""")] },
            cacheHere: false);
        var use = Assert.IsType<Anthropic.Models.Messages.ToolUseBlockParam>(Assert.Single(Blocks(call)).Value);
        Assert.Equal("toolu_1", use.ID);
        Assert.Equal("read_file", use.Name);

        var result = AnthropicLlmClient.ToSdkMessage(
            new LlmMessage("user", "") { ToolResults = [new LlmToolResult("toolu_1", "read_file", "file body")] },
            cacheHere: false);
        var back = Assert.IsType<Anthropic.Models.Messages.ToolResultBlockParam>(Assert.Single(Blocks(result)).Value);
        // The id issued with the call is what pairs the result to it.
        Assert.Equal("toolu_1", back.ToolUseID);
    }

    [Fact]
    public void Anthropic_puts_the_cache_breakpoint_on_a_tool_result_when_that_ends_the_turn()
    {
        // The breakpoint has to land on whatever the last block is, not only on text, or a
        // turn that ends in a tool result caches nothing.
        var message = AnthropicLlmClient.ToSdkMessage(
            new LlmMessage("user", "") { ToolResults = [new LlmToolResult("toolu_1", "read_file", "body")] },
            cacheHere: true);

        var last = Assert.IsType<Anthropic.Models.Messages.ToolResultBlockParam>(Blocks(message)[^1].Value);
        Assert.NotNull(last.CacheControl);
    }

    [Fact]
    public void Provider_aliases_resolve_to_the_right_adapter()
    {
        Assert.IsType<AnthropicLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "claude" }));
        Assert.IsType<OpenAiLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "OpenAI" }));
        Assert.IsType<GeminiLlmClient>(LlmClientFactory.Create(new LlmConfig { Provider = "google" }));
    }
}
