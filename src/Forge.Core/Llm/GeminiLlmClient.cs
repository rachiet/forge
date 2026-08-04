using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Llm;

/// <summary>
/// The Google Gemini provider adapter (spec §11). Thin by design: it translates
/// Forge's request/response records to `generateContent` and back. Hand-rolled over
/// HttpClient for the same reason as the OpenAI adapter — none of an SDK's surface
/// would be used.
///
/// Never hand one of these to an agent loop directly — wrap it in MeteredLlmClient
/// so the ledger is written and budgets are enforced.
///
/// Shapes below follow the v1beta discovery document's GenerateContentRequest,
/// Content/Part, GenerationConfig and UsageMetadata.
/// </summary>
public sealed class GeminiLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "GEMINI_API_KEY";
    public const string ProviderName = "gemini";
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    /// <summary>
    /// Ids carry the price table's `gemini/` prefix so they are priceable as written;
    /// <see cref="WireModel"/> strips it for the URL. One canonical id travels through
    /// the recipe, the ledger and the pricer, and the provider-specific spelling is
    /// resolved at the one place that talks to the provider.
    /// </summary>
    private static readonly IReadOnlyDictionary<ModelTier, string> DefaultModels =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Fast] = "gemini/gemini-3.1-flash-lite",
            [ModelTier.Coding] = "gemini/gemini-2.5-flash",
            // Not a pro model: the pro tier is quota-limited on this account and hangs
            // often enough to stall a build. Revisit when pro capacity is reliable.
            [ModelTier.Reasoning] = "gemini/gemini-3.6-flash",
        };

    private const string ModelPrefix = "gemini/";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<ModelTier, string> _models;

    public GeminiLlmClient(string? apiKey = null, LlmConfig? config = null, HttpClient? http = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyVariable);
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _models = TierMap.Resolve(config, DefaultModels);
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
        // Header rather than ?key=, so the credential never lands in a URL that some
        // proxy or error message might echo.
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
    /// Gemini's conversation shape differs from the other two in three ways, all
    /// absorbed here: the system prompt is `systemInstruction` (not a message), the
    /// assistant role is spelled `model`, and content is a list of parts rather than
    /// a string.
    /// </summary>
    internal static string BuildBody(LlmRequest request)
    {
        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            contents.Add(new JsonObject
            {
                ["role"] = message.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = message.Content }),
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

        return body.ToJsonString();
    }

    /// <summary>
    /// Usage translation, per UsageMetadata. The two things worth stating:
    ///
    /// - `promptTokenCount` is documented as "still the total effective prompt size"
    ///   when content is cached, so `cachedContentTokenCount` is a subset and is
    ///   subtracted — the same normalisation the OpenAI adapter performs, so
    ///   LlmUsage.TokensIn means one thing everywhere.
    /// - output is `candidatesTokenCount` PLUS `thoughtsTokenCount`. Thinking tokens are
    ///   reported separately but billed as output, and omitting them would undercount
    ///   every call to a thinking model.
    ///
    /// Cache writes have no counterpart: Gemini's implicit cache is not separately
    /// charged, and explicit cached content is billed as storage rather than per call.
    /// </summary>
    internal static LlmResponse ParseResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        var text = new StringBuilder();
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
            Usage = new LlmUsage(Math.Max(0, prompt - cached), output, cached),
        };
    }

    private static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
