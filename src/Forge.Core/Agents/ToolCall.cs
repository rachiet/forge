namespace Forge.Core.Agents;

/// <summary>Thrown when a call cannot be used: a missing argument, or one of the wrong kind.</summary>
public sealed class ToolCallException(string message) : Exception(message);

/// <summary>One call the model made, as the toolset takes it.</summary>
/// <param name="Name">The tool being called.</param>
/// <param name="Args">Its arguments, as raw strings; the toolset parses what it needs.</param>
/// <param name="Raw">The arguments as the provider sent them, for logging a refusal.</param>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, string> Args, string Raw = "")
{
    /// <summary>A required argument; throws when the call did not carry it.</summary>
    public string Arg(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ToolCallException($"Tool '{Name}' requires a non-empty `{name}` argument.");

    /// <summary>An optional argument, or null when the call did not carry it.</summary>
    public string? Optional(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>An optional argument as an integer, or null when absent or unparseable.</summary>
    public int? OptionalInt(string name)
    {
        var raw = Optional(name);
        if (raw is null) return null;
        if (!int.TryParse(raw.Trim(), out var value))
            throw new ToolCallException($"Tool '{Name}': `{name}` must be an integer, got '{raw}'.");
        return value;
    }
}
