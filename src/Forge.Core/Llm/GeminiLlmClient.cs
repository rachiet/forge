using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Llm;

/// <summary>
/// The Google Gemini adapter: translates Forge's request and response records to the v1beta
/// `generateContent` API and back, over HttpClient. Tool calls travel in the provider's own
/// `functionCall` and `functionResponse` parts rather than as text the harness has to parse.
///
/// `generateContent` and not the newer Interactions API: Interactions keeps conversation state
/// on Google's side and its documented multi-turn path is `previous_interaction_id`, while every
/// agent here is stateless and replays its whole conversation each turn. Wrap it in
/// MeteredLlmClient before handing it to an agent loop, or nothing is ledgered and no budget is
/// enforced.
/// </summary>
public sealed class GeminiLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "GEMINI_API_KEY";
    public const string ProviderName = "gemini";
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>
    /// This provider's answer to each tier. Ids carry the price table's `gemini/` prefix so
    /// they are priceable as written; <see cref="WireModel"/> strips it for the URL.
    /// </summary>
    private static readonly IReadOnlyDictionary<ModelTier, string> DefaultModels =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Coding] = "gemini/gemini-2.5-flash",
            // A flash model, not pro: pro capacity is unreliable enough to stall a build.
            [ModelTier.Reasoning] = "gemini/gemini-3.6-flash",
        };

    private const string ModelPrefix = "gemini/";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<ModelTier, string> _models;

    public GeminiLlmClient(string? apiKey = null, HttpClient? http = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyVariable);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _models = DefaultModels;
    }

    public string ModelFor(ModelTier tier) => _models[tier];

    /// <summary>The name the API expects: the canonical id without the price-table prefix.</summary>
    internal static string WireModel(string model) =>
        model.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) ? model[ModelPrefix.Length..] : model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}{WireModel(request.Model)}:generateContent")
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };
        // Sent as a header, so the key never lands in a URL a proxy or error might echo.
        if (!string.IsNullOrWhiteSpace(_apiKey)) message.Headers.Add("x-goog-api-key", _apiKey);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = $"Gemini returned {(int)response.StatusCode}: {Truncate(payload)}";
            throw TransientFailure.IsTransient(response.StatusCode)
                ? new TransientLlmException(detail, retryAfter: TransientFailure.RetryAfter(response))
                : new HttpRequestException(detail, null, response.StatusCode);
        }

        return ParseResponse(payload);
    }

    /// <summary>
    /// Builds the request body. Gemini's shape differs from the other providers in three ways:
    /// the system prompt is `systemInstruction` rather than a message, the assistant role is
    /// spelled `model`, and content is a list of parts rather than a string.
    /// </summary>
    internal static string BuildBody(LlmRequest request)
    {
        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            var parts = new JsonArray();
            if (message.Content is { Length: > 0 }) parts.Add(new JsonObject { ["text"] = message.Content });

            foreach (var call in message.ToolCalls)
            {
                var part = new JsonObject
                {
                    ["functionCall"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["args"] = JsonNode.Parse(call.ArgumentsJson),
                    },
                };
                // The signature the model issued with this call, returned unchanged. Gemini
                // refuses a conversation that replays a call without it.
                if (call.Signature is { Length: > 0 } signature) part["thoughtSignature"] = signature;
                parts.Add(part);
            }

            // A result is wrapped in an object because `response` is a structured value, not
            // a string, and it is paired to its call by name — this API issues no call id.
            foreach (var result in message.ToolResults)
            {
                parts.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = result.Name,
                        ["response"] = new JsonObject { ["result"] = result.Output },
                    },
                });
            }

            if (parts.Count == 0) continue;
            contents.Add(new JsonObject
            {
                // Tool results go back as `user`, the same role the model's caller speaks in.
                ["role"] = message.Role == "assistant" ? "model" : "user",
                ["parts"] = parts,
            });
        }

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = request.MaxTokens },
        };
        if (request.System is { Length: > 0 } system)
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = system }),
            };
        if (request.Tools.Count > 0)
            body["tools"] = new JsonArray(new JsonObject
            {
                ["function_declarations"] = BuildDeclarations(request.Tools),
            });

        return body.ToJsonString();
    }

    /// <summary>The tool schemas, as this API's function declarations.</summary>
    private static JsonArray BuildDeclarations(IReadOnlyList<LlmToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.ParametersJson),
            });
        }
        return array;
    }

    /// <summary>
    /// Translates UsageMetadata into Forge's four token buckets. `promptTokenCount` includes
    /// cached content, so the cached count is subtracted to leave the uncached remainder that
    /// TokensIn means everywhere. Output is `candidatesTokenCount` plus `thoughtsTokenCount`,
    /// which is reported separately but billed as output. Cache writes are always zero:
    /// Gemini does not charge them per call.
    /// </summary>
    internal static LlmResponse ParseResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = new StringBuilder();
        var calls = new List<LlmToolCall>();
        string? stopReason = null;
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var finish) && finish.ValueKind == JsonValueKind.String)
                stopReason = finish.GetString();

            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    // A part flagged `thought` is the model's private reasoning, not its
                    // answer; feeding it back as assistant text would corrupt the loop.
                    if (part.TryGetProperty("thought", out var thought) &&
                        thought.ValueKind == JsonValueKind.True) continue;

                    if (part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                        text.Append(value.GetString());

                    // No call id is issued, so the name is the correlation key both ways.
                    if (part.TryGetProperty("functionCall", out var call) &&
                        call.ValueKind == JsonValueKind.Object)
                    {
                        var name = call.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? "" : "";
                        var args = call.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                            ? a.GetRawText() : "{}";
                        // Sits on the part beside the call, not inside it. Kept so the next
                        // request can replay the call with the signature it was issued.
                        var signature = part.TryGetProperty("thoughtSignature", out var s)
                                     && s.ValueKind == JsonValueKind.String
                            ? s.GetString() : null;
                        calls.Add(new LlmToolCall(name, name, args) { Signature = signature });
                    }
                }
            }
        }

        var prompt = 0;
        var cached = 0;
        var output = 0;
        if (root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            prompt = Int(usage, "promptTokenCount");
            cached = Int(usage, "cachedContentTokenCount");
            output = Int(usage, "candidatesTokenCount") + Int(usage, "thoughtsTokenCount");
        }

        return new LlmResponse
        {
            Content = text.ToString(),
            StopReason = stopReason,
            ToolCalls = calls,
            Usage = new LlmUsage(Math.Max(0, prompt - cached), output, cached),
        };
    }

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
