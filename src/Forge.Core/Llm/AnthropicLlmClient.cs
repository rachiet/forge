using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ForgeMessage = Forge.Core.Llm.LlmMessage;

namespace Forge.Core.Llm;

/// <summary>
/// The Anthropic adapter: translates Forge's request and response records to the Messages API
/// and back. Tool calls travel as the API's own `tool_use` and `tool_result` blocks rather than
/// as text the harness has to parse. Wrap it in MeteredLlmClient before handing it to an agent
/// loop, or nothing is ledgered and no budget is enforced.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";
    public const string ProviderName = "anthropic";

    /// <summary>
    /// This provider's answer to each tier, and the only place an Anthropic model is named.
    /// Any entry can be overridden in llm.json.
    /// </summary>
    private static readonly IReadOnlyDictionary<ModelTier, string> DefaultModels =
        new Dictionary<ModelTier, string>
        {
            [ModelTier.Coding] = "claude-sonnet-5",
            [ModelTier.Reasoning] = "claude-opus-4-8",
        };

    private readonly AnthropicClient _client;
    private readonly IReadOnlyDictionary<ModelTier, string> _models;

    /// <summary>
    /// Builds the client. The API key comes from the environment forge_env populates at
    /// startup, never from the database or a task packet; pass one explicitly only in tests.
    /// </summary>
    public AnthropicLlmClient(string? apiKey = null, LlmConfig? config = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable(ApiKeyVariable);

        _client = string.IsNullOrWhiteSpace(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = apiKey };

        _models = TierMap.Resolve(config, DefaultModels);
    }

    public string ModelFor(ModelTier tier) => _models[tier];

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Two cache breakpoints: one after the system prompt, which is identical on every
        // turn, and one on the last message, which caches the conversation so far. The next
        // turn then reads that prefix at cache rates and writes only its new suffix.
        var messages = request.Messages
            .Select((message, index) => ToSdkMessage(message, cacheHere: index == request.Messages.Count - 1))
            .ToList();

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = messages,
        };
        if (request.Tools.Count > 0)
            parameters = parameters with { Tools = request.Tools.Select(ToSdkTool).ToList() };
        if (request.System is { Length: > 0 } system)
            parameters = parameters with
            {
                System = new List<TextBlockParam>
                {
                    new() { Text = system, CacheControl = new CacheControlEphemeral() },
                },
            };

        // The SDK throws rather than returning a status; translate to Forge's transient type.
        Message message;
        try
        {
            message = await _client.Messages.Create(parameters, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Anthropic.Exceptions.AnthropicApiException e) when (TransientFailure.IsTransient(e.StatusCode))
        {
            throw new TransientLlmException($"Anthropic returned {(int)e.StatusCode}: {e.Message}", e);
        }
        catch (Anthropic.Exceptions.AnthropicIOException e)
        {
            throw new TransientLlmException($"Anthropic connection failed: {e.Message}", e);
        }

        var text = string.Concat(message.Content
            .Select(block => block.Value)
            .OfType<TextBlock>()
            .Select(block => block.Text));

        var calls = message.Content
            .Select(block => block.Value)
            .OfType<ToolUseBlock>()
            .Select(block => new LlmToolCall(block.ID, block.Name, JsonSerializer.Serialize(block.Input)))
            .ToList();

        return new LlmResponse
        {
            Content = text,
            StopReason = message.StopReason?.ToString(),
            ToolCalls = calls,
            Usage = new LlmUsage(
                (int)message.Usage.InputTokens,
                (int)message.Usage.OutputTokens,
                (int)(message.Usage.CacheReadInputTokens ?? 0),
                (int)(message.Usage.CacheCreationInputTokens ?? 0)),
        };
    }

    /// <summary>
    /// One turn as the SDK's blocks: the results a following turn returns, its text, and the
    /// calls an assistant turn made. Results come first because the API refuses a turn whose
    /// tool_result blocks do not open it. The last block of the last message carries a cache
    /// breakpoint, so the next turn reads this whole prefix at cache rates and writes only its
    /// new suffix.
    /// </summary>
    internal static MessageParam ToSdkMessage(ForgeMessage message, bool cacheHere)
    {
        var blocks = new List<ContentBlockParam>();

        foreach (var result in message.ToolResults)
            blocks.Add(new ToolResultBlockParam { ToolUseID = result.CallId, Content = result.Output });

        if (message.Content is { Length: > 0 })
            blocks.Add(new TextBlockParam { Text = message.Content });

        foreach (var call in message.ToolCalls)
        {
            blocks.Add(new ToolUseBlockParam
            {
                ID = call.Id,
                Name = call.Name,
                Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.ArgumentsJson) ?? [],
            });
        }

        // A turn has to carry something; an empty one would be rejected.
        if (blocks.Count == 0) blocks.Add(new TextBlockParam { Text = message.Content });

        if (cacheHere) blocks[^1] = WithCacheControl(blocks[^1]);

        return new MessageParam
        {
            Role = message.Role == "assistant" ? Role.Assistant : Role.User,
            Content = blocks,
        };
    }

    /// <summary>The same block with a cache breakpoint on it, whichever kind of block it is.</summary>
    private static ContentBlockParam WithCacheControl(ContentBlockParam block) => block.Value switch
    {
        TextBlockParam text => text with { CacheControl = new CacheControlEphemeral() },
        ToolUseBlockParam use => use with { CacheControl = new CacheControlEphemeral() },
        ToolResultBlockParam result => result with { CacheControl = new CacheControlEphemeral() },
        _ => block,
    };

    /// <summary>
    /// One tool as the SDK declares it. The JSON Schema Forge holds as text is split into the
    /// properties map and required list the API takes.
    /// </summary>
    internal static ToolUnion ToSdkTool(LlmToolDefinition tool)
    {
        using var schema = JsonDocument.Parse(tool.ParametersJson);
        var root = schema.RootElement;

        var properties = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object)
            foreach (var property in declared.EnumerateObject())
                properties[property.Name] = property.Value.Clone();

        var required = new List<string>();
        if (root.TryGetProperty("required", out var names) && names.ValueKind == JsonValueKind.Array)
            required.AddRange(names.EnumerateArray().Select(name => name.GetString() ?? "").Where(n => n.Length > 0));

        return new ToolUnion(new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        });
    }
}
