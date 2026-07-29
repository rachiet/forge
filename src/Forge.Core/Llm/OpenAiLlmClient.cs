using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Llm;

/// <summary>
/// The OpenAI provider adapter (spec §11). Deliberately thin: it translates Forge's
/// request/response records to the Chat Completions API and back, and does nothing
/// else. Hand-rolled over HttpClient rather than the SDK — Forge uses none of the SDK
/// surface (no streaming, no function calling, no structured output; tool calls are
/// parsed out of plain text), so a dependency would buy nothing.
///
/// Never hand one of these to an agent loop directly — wrap it in MeteredLlmClient
/// so the ledger is written and budgets are enforced.
///
/// Shapes below follow openai-openapi's CreateChatCompletionRequest / CompletionUsage.
/// </summary>
public sealed class OpenAiLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "OPENAI_API_KEY";
    public const string ProviderName = "openai";
    public const string Endpoint = "https://api.openai.com/v1/chat/completions";

    /// <summary>
    /// Ids are the price table's keys, so every default is priceable out of the box —
    /// an unpriced model refuses to run, and a default that cannot run is not a default.
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
            throw new HttpRequestException(
                $"OpenAI returned {(int)response.StatusCode}: {Truncate(payload)}", null, response.StatusCode);

        return ParseResponse(payload);
    }

    /// <summary>
    /// The system prompt travels as a `system` message rather than a top-level field.
    /// `system` and not `developer`: the newer reasoning models accept both (they treat
    /// system as developer), while older chat models only know system — so system is the
    /// one spelling that works across everything an operator might pin in llm.json.
    /// </summary>
    internal static string BuildBody(LlmRequest request)
    {
        var messages = new JsonArray();
        if (request.System is { Length: > 0 } system)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        foreach (var message in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = message.Role == "assistant" ? "assistant" : "user",
                ["content"] = message.Content,
            });
        }

        // max_completion_tokens, not the deprecated max_tokens: it is the only one that
        // bounds reasoning tokens as well as visible output.
        return new JsonObject
        {
            ["model"] = request.Model,
            ["max_completion_tokens"] = request.MaxTokens,
            ["messages"] = messages,
        }.ToJsonString();
    }

    /// <summary>
    /// Usage translation, per CompletionUsage. Two asymmetries against Anthropic that
    /// the adapter is here to absorb:
    ///
    /// - `prompt_tokens` is the WHOLE prompt and `cached_tokens` a subset of it, whereas
    ///   Anthropic's input_tokens already excludes cached ones. Subtracting keeps
    ///   LlmUsage.TokensIn meaning the same thing for every provider.
    /// - `cache_write_tokens` is reported but NOT separately priced: OpenAI bills cache
    ///   writes at the ordinary input rate. They therefore stay inside TokensIn and
    ///   CacheWriteTokens is left at zero — mapping them across would ask the pricer for
    ///   a cache-write rate that legitimately does not exist.
    ///
    /// `completion_tokens` already includes reasoning tokens (the spec is explicit that
    /// they count for billing), so no adjustment is needed on the output side.
    /// </summary>
    internal static LlmResponse ParseResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = "";
        string? stopReason = null;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var body) &&
                body.ValueKind == JsonValueKind.String)
            {
                text = body.GetString() ?? "";
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                stopReason = finish.GetString();
        }

        var promptTokens = 0;
        var completionTokens = 0;
        var cachedTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            promptTokens = Int(usage, "prompt_tokens");
            completionTokens = Int(usage, "completion_tokens");
            if (usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.ValueKind == JsonValueKind.Object)
            {
                cachedTokens = Int(details, "cached_tokens");
            }
        }

        return new LlmResponse
        {
            Content = text,
            StopReason = stopReason,
            Usage = new LlmUsage(Math.Max(0, promptTokens - cachedTokens), completionTokens, cachedTokens),
        };
    }

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
