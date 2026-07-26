using Anthropic;
using Anthropic.Models.Messages;
using ForgeMessage = Forge.Core.Llm.LlmMessage;

namespace Forge.Core.Llm;

/// <summary>
/// The provider adapter (spec §11). Deliberately thin: it translates Forge's
/// request/response records to the Anthropic SDK and back, and does nothing else.
///
/// Never hand one of these to an agent loop directly — wrap it in
/// MeteredLlmClient so the ledger is written and budgets are enforced.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    private readonly AnthropicClient _client;

    /// <summary>
    /// Forge authenticates its calls to the Anthropic Messages API with a Claude API
    /// key (`sk-ant-api…`), read from the environment that forge_env populates at
    /// startup — never from the database or a task packet. The SDK sends it as the
    /// `x-api-key` header. Pass a key explicitly only in tests; in normal use the SDK
    /// resolves ANTHROPIC_API_KEY from the environment itself.
    /// </summary>
    public AnthropicLlmClient(string? apiKey = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable(ApiKeyVariable);

        _client = string.IsNullOrWhiteSpace(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = apiKey };
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Prompt caching. Our agent loop re-sends a byte-identical system prompt
        // (role + task-type template + tool protocol) plus a growing conversation
        // prefix on every turn — the ideal caching case. We set two ephemeral
        // breakpoints: one after the system prompt, one on the last message. The
        // system breakpoint reuses the frozen preamble across every turn of a run;
        // the last-message breakpoint caches the whole conversation-so-far, so the
        // next turn reads that prefix (~0.1x) and writes only its new suffix.
        // Cost of a long agent loop drops from ~O(turns^2) to ~O(turns) in input
        // tokens. Cache reads are verified via message.Usage.CacheReadInputTokens.
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

        var message = await _client.Messages.Create(parameters, cancellationToken: ct).ConfigureAwait(false);

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
    /// Same message, but its content carried as a single cacheable text block with
    /// an ephemeral breakpoint — the caching seam for the growing conversation
    /// prefix. A plain string content can't carry cache_control; a block list can.
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
