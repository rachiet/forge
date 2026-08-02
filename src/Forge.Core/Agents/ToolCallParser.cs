using System.Text.RegularExpressions;

namespace Forge.Core.Agents;

public sealed class ToolCallException(string message) : Exception(message);

/// <summary>
/// One parsed tool invocation from a model turn. Arguments are raw strings;
/// <paramref name="Raw"/> is the original block the call was parsed from.
/// </summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, string> Args, string Raw = "")
{
    public string Arg(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ToolCallException($"Tool '{Name}' requires a non-empty <arg name=\"{name}\">.");

    public string? Optional(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public int? OptionalInt(string name)
    {
        var raw = Optional(name);
        if (raw is null) return null;
        if (!int.TryParse(raw.Trim(), out var value))
            throw new ToolCallException($"Tool '{Name}': <arg name=\"{name}\"> must be an integer, got '{raw}'.");
        return value;
    }
}

/// <summary>
/// Parses tool calls out of a model's text turn.
///
/// The protocol is tag-delimited rather than JSON on purpose: engineers write
/// file contents containing quotes, backslashes and newlines constantly, and a
/// JSON payload turns every such write into an escaping problem the model gets
/// wrong. Raw text between tags has no escaping rules to get wrong.
///
///   &lt;tool name="write_file"&gt;
///   &lt;arg name="path"&gt;src/Foo.cs&lt;/arg&gt;
///   &lt;arg name="content"&gt;
///   public sealed class Foo { }
///   &lt;/arg&gt;
///   &lt;/tool&gt;
/// </summary>
public static partial class ToolCallParser
{
    [GeneratedRegex(@"<tool\s+name\s*=\s*""([a-z_]+)""\s*>(.*?)</tool\s*>", RegexOptions.Singleline)]
    private static partial Regex ToolBlock();

    [GeneratedRegex(@"<arg\s+name\s*=\s*""([a-z_]+)""\s*>(.*?)</arg\s*>", RegexOptions.Singleline)]
    private static partial Regex ArgBlock();

    public static IReadOnlyList<ToolCall> Parse(string content)
    {
        List<ToolCall> calls = [];
        foreach (Match tool in ToolBlock().Matches(content))
        {
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match arg in ArgBlock().Matches(tool.Groups[2].Value))
                args[arg.Groups[1].Value] = Normalize(arg.Groups[2].Value);
            calls.Add(new ToolCall(tool.Groups[1].Value, args, tool.Value));
        }
        return calls;
    }

    /// <summary>
    /// Strip only the layout newlines the tag form introduces — the one after the
    /// opening tag and the indentation before the closing tag — so that file
    /// content the model wrote on its own lines round-trips byte-for-byte.
    /// </summary>
    private static string Normalize(string raw)
    {
        var value = raw;
        if (value.StartsWith("\r\n", StringComparison.Ordinal)) value = value[2..];
        else if (value.StartsWith('\n')) value = value[1..];

        var lastNewline = value.LastIndexOf('\n');
        if (lastNewline >= 0 && value[(lastNewline + 1)..].All(c => c is ' ' or '\t'))
            value = value[..lastNewline];

        return value;
    }
}
