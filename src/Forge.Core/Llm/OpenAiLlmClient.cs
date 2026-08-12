using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Llm;

/// <summary>
/// The OpenAI adapter: translates Forge's request and response records to the Responses API and
/// back, over HttpClient. Tool calls travel in the provider's own `tools` / `function_call`
/// fields rather than as text the harness has to parse, so a call cannot arrive malformed.
/// Wrap it in MeteredLlmClient before handing it to an agent loop, or nothing is ledgered and no
/// budget is enforced.
/// </summary>
public sealed class OpenAiLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "OPENAI_API_KEY";
    public const string ProviderName = "openai";
    public const string Endpoint = "https://api.openai.com/v1/responses";

    /// <summary>
    /// This provider's answer to each tier. The ids are price-table keys, so every default is
    /// priceable — an unpriced model refuses to run.
    /// </summary>
    private static readonly IReadOnlyDictionary<ModelTier, string> DefaultModels =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Fast] = "gpt-5-nano",
            [ModelTier.Coding] = "gpt-5",
            [ModelTier.Reasoning] = "gpt-5.4",
        };

    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<ModelTier, string> _models;

    public OpenAiLlmClient(string? apiKey = null, LlmConfig? config = null, HttpClient? http = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable(ApiKeyVariable);

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _models = TierMap.Resolve(config, DefaultModels);
    }

    public string ModelFor(ModelTier tier) => _models[tier];

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = $"OpenAI returned {(int)response.StatusCode}: {Truncate(payload)}";
            throw TransientFailure.IsTransient(response.StatusCode)
                ? new TransientLlmException(detail, retryAfter: TransientFailure.RetryAfter(response))
                : new HttpRequestException(detail, null, response.StatusCode);
        }

        return ParseResponse(payload);
    }

    /// <summary>
    /// The Responses request: the system prompt as `instructions`, the conversation as `input`,
    /// and the tool schemas as `tools`. `max_output_tokens` bounds reasoning tokens as well as
    /// visible output.
    /// </summary>
    internal static string BuildBody(LlmRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_output_tokens"] = request.MaxTokens,
            ["input"] = BuildInput(request.Messages),
        };

        if (request.System is { Length: > 0 } system) body["instructions"] = system;
        if (request.Tools.Count > 0) body["tools"] = BuildTools(request.Tools);

        return body.ToJsonString();
    }

    /// <summary>
    /// The conversation as Responses input items. A plain turn is a role/content message; a turn
    /// that called tools becomes one `function_call` item per call, and the results that answer
    /// them become `function_call_output` items carrying the same `call_id`.
    /// </summary>
    private static JsonArray BuildInput(IReadOnlyList<LlmMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Content is { Length: > 0 })
            {
                input.Add(new JsonObject
                {
                    ["role"] = message.Role == "assistant" ? "assistant" : "user",
                    ["content"] = message.Content,
                });
            }

            foreach (var call in message.ToolCalls)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                });
            }

            foreach (var result in message.ToolResults)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = result.CallId,
                    ["output"] = result.Output,
                });
            }
        }
        return input;
    }

    /// <summary>
    /// The tool schemas, flat as this API declares them. `strict` is left off deliberately: it
    /// requires every property to be required and additionalProperties false, which no tool with
    /// an optional argument can satisfy.
    /// </summary>
    private static JsonArray BuildTools(IReadOnlyList<LlmToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.ParametersJson),
            });
        }
        return array;
    }

    /// <summary>
    /// Reads the response: text from the `message` items, calls from the `function_call` items,
    /// and the four token buckets from `usage`. `input_tokens` is the whole prompt and
    /// `input_tokens_details.cached_tokens` the part of it served from cache, so the cached count
    /// is subtracted to leave the uncached remainder TokensIn means everywhere. Cache writes stay
    /// inside TokensIn and CacheWriteTokens is left at zero, since OpenAI bills them at the
    /// ordinary input rate. `output_tokens` already includes reasoning tokens.
    /// </summary>
    internal static LlmResponse ParseResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var type = Text(item, "type");
                if (type == "function_call")
                {
                    calls.Add(new LlmToolCall(
                        Text(item, "call_id") ?? Text(item, "id") ?? "",
                        Text(item, "name") ?? "",
                        Text(item, "arguments") ?? "{}"));
                }
                else if (type == "message" && item.TryGetProperty("content", out var parts)
                                           && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                        if (Text(part, "type") == "output_text" && Text(part, "text") is { } chunk)
                            text.Append(chunk);
                }
            }
        }

        // `status` is "completed" or "incomplete"; the reason for an incomplete one — hitting
        // max_output_tokens, say — is the part worth reporting.
        var stopReason = Text(root, "status");
        // Present but null on a completed response, so the kind is checked before reading it.
        if (root.TryGetProperty("incomplete_details", out var incomplete) &&
            incomplete.ValueKind == JsonValueKind.Object &&
            Text(incomplete, "reason") is { Length: > 0 } why)
        {
            stopReason = why;
        }

        var inputTokens = 0;
        var outputTokens = 0;
        var cachedTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = Int(usage, "input_tokens");
            outputTokens = Int(usage, "output_tokens");
            if (usage.TryGetProperty("input_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                cachedTokens = Int(details, "cached_tokens");
            }
        }

        return new LlmResponse
        {
            Content = text.ToString(),
            StopReason = stopReason,
            ToolCalls = calls,
            Usage = new LlmUsage(Math.Max(0, inputTokens - cachedTokens), outputTokens, cachedTokens),
        };
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
