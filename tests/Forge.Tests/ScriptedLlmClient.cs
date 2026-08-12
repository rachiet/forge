using System.Text.Json;
using Forge.Core.Agents;
using Forge.Core.Llm;

namespace Forge.Tests;

/// <summary>
/// A test double that replays a queued script of model turns and records what it
/// was asked. Lets the agent loop be tested end to end without a network call —
/// the loop is deterministic harness code, so the model is the only thing worth faking.
/// </summary>
public sealed class ScriptedLlmClient(params string[] turns) : ILlmClient
{
    public string ModelFor(ModelTier tier) => TestPrices.For(tier);

    private readonly Queue<string> _turns = new(turns);

    public List<LlmRequest> Requests { get; } = [];
    public int Calls => Requests.Count;

    /// <summary>The system prompt of the most recent call — used to assert on context assembly.</summary>
    public string? LastSystemPrompt => Requests.LastOrDefault()?.System;

    /// <summary>
    /// What the harness sent back on the turn before request <paramref name="index"/>: the tool
    /// results and any text alongside them. Tool output travels in ToolResults now, so a test
    /// that reads only Content sees an empty string.
    /// </summary>
    public string Observations(int index)
    {
        var message = Requests[index].Messages[^1];
        return string.Join("\n", new[] { message.Content }
            .Concat(message.ToolResults.Select(r => $"[{r.Name}]\n{r.Output}"))
            .Where(part => part.Length > 0));
    }

    /// <summary>The task packet (first user turn) of the most recent call.</summary>
    public string? LastTaskPacket => Requests.LastOrDefault()?.Messages.FirstOrDefault()?.Content;

    /// <summary>Emitted once the script runs dry, so a loop under test can't hang on an empty queue.</summary>
    public string Fallback { get; init; } = "Nothing left to do.";

    /// <summary>What every scripted turn reports as its reason for stopping.</summary>
    public string StopReason { get; init; } = "end_turn";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var content = _turns.Count > 0 ? _turns.Dequeue() : Fallback;
        // Scripts are written in the readable `<tool …>` form; the loop reads structured
        // calls, so the double parses its own script the way a provider would return one.
        var calls = ToolCallParser.Parse(content)
            .Select((call, index) => new LlmToolCall(
                $"call_{index}", call.Name, JsonSerializer.Serialize(call.Args)))
            .ToList();

        return Task.FromResult(new LlmResponse
        {
            Content = calls.Count > 0 ? "" : content,
            StopReason = StopReason,
            ToolCalls = calls,
            Usage = new LlmUsage(100, 50),
        });
    }

    public static string Tool(string name, params (string Name, string Value)[] args)
    {
        var body = string.Join("\n", args.Select(a => $"<arg name=\"{a.Name}\">\n{a.Value}\n</arg>"));
        return $"<tool name=\"{name}\">\n{body}\n</tool>";
    }
}
