using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Llm;

namespace Forge.Tests;

/// <summary>
/// A test double that replays a queued script of model turns and records what it
/// was asked. Lets the agent loop be tested end to end without a network call —
/// the loop is deterministic harness code, so the model is the only thing worth faking.
/// </summary>
public sealed class ScriptedLlmClient(params ScriptedTurn[] turns) : ILlmClient
{
    public string ModelFor(ModelTier tier) => TestPrices.For(tier);

    private readonly Queue<ScriptedTurn> _turns = new(turns);

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
    public ScriptedTurn Fallback { get; init; } = "Nothing left to do.";

    /// <summary>What every scripted turn reports as its reason for stopping.</summary>
    public string StopReason { get; init; } = "end_turn";

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        var turn = _turns.Count > 0 ? _turns.Dequeue() : Fallback;

        return Task.FromResult(new LlmResponse
        {
            Content = turn.Text,
            StopReason = StopReason,
            ToolCalls = turn.Calls,
            Usage = new LlmUsage(100, 50),
        });
    }

    /// <summary>One scripted turn that calls a tool, built the way an adapter reports one.</summary>
    public static ScriptedTurn Tool(string name, params (string Name, string Value)[] args)
    {
        var arguments = new JsonObject();
        foreach (var arg in args) arguments[arg.Name] = arg.Value;
        return new ScriptedTurn("", [new LlmToolCall($"call_{name}", name, arguments.ToJsonString())]);
    }

    /// <summary>Several calls in one turn, which the loop runs in order.</summary>
    public static ScriptedTurn Turn(params ScriptedTurn[] calls) =>
        new("", [.. calls.SelectMany(call => call.Calls)]);
}

/// <summary>
/// One turn of a script: prose, or the calls a model made. A plain string is prose, so a test
/// that scripts an agent saying something writes the string and nothing else.
/// </summary>
public sealed record ScriptedTurn(string Text, IReadOnlyList<LlmToolCall> Calls)
{
    public static implicit operator ScriptedTurn(string text) => new(text, []);
}
