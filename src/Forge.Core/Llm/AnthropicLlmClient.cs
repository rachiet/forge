using Anthropic;
using Anthropic.Models.Messages;
using ForgeMessage = Forge.Core.Llm.LlmMessage;

namespace Forge.Core.Llm;

/// <summary>
/// The Anthropic adapter: translates Forge's request and response records to the Messages API
/// and back. Wrap it in MeteredLlmClient before handing it to an agent loop, or nothing is
/// ledgered and no budget is enforced.
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
            [ModelTier.Fast] = "claude-haiku-4-5",
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
        var messages = request.Messages.Select(ToSdkMessage).ToList();
        if (messages.Count > 0)
            messages[^1] = WithCacheBreakpoint(request.Messages[^1]);

        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = messages,
        };
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

        return new LlmResponse
        {
            Content = text,
            StopReason = message.StopReason?.ToString(),
            Usage = new LlmUsage(
                (int)message.Usage.InputTokens,
                (int)message.Usage.OutputTokens,
                (int)(message.Usage.CacheReadInputTokens ?? 0),
                (int)(message.Usage.CacheCreationInputTokens ?? 0)),
        };
    }

    private static MessageParam ToSdkMessage(ForgeMessage message) => new()
    {
        Role = message.Role == "assistant" ? Role.Assistant : Role.User,
        Content = message.Content,
    };

    /// <summary>
    /// The same message with its content as a single text block carrying a cache breakpoint.
    /// A plain string content cannot carry one; a block list can.
    /// </summary>
    private static MessageParam WithCacheBreakpoint(ForgeMessage message) => new()
    {
        Role = message.Role == "assistant" ? Role.Assistant : Role.User,
        Content = new List<ContentBlockParam>
        {
            new TextBlockParam { Text = message.Content, CacheControl = new CacheControlEphemeral() },
        },
    };
}
