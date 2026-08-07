using System.Text.RegularExpressions;

namespace Forge.Core.Agents;

/// <summary>Thrown when a tool block cannot be parsed at all.</summary>
public sealed class ToolCallException(string message) : Exception(message);

/// <summary>One parsed tool invocation.</summary>
/// <param name="Name">The tool being called.</param>
/// <param name="Args">Its arguments, as raw strings.</param>
/// <param name="Raw">The original text the call was parsed from, for logging a refusal.</param>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, string> Args, string Raw = "")
{
    /// <summary>A required argument; throws when the call did not carry it.</summary>
    public string Arg(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ToolCallException($"Tool '{Name}' requires a non-empty <arg name=\"{name}\">.");

    /// <summary>An optional argument, or null when the call did not carry it.</summary>
    public string? Optional(string name) =>
        Args.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>An optional argument as an integer, or null when absent or unparseable.</summary>
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
/// Parses tool calls out of a model's text turn. The protocol is tag-delimited rather than
/// JSON, so file contents full of quotes, backslashes and newlines need no escaping.
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
    /// <summary>Matches one tool block.</summary>
    private static partial Regex ToolBlock();

    [GeneratedRegex(@"<arg\s+name\s*=\s*""([a-z_]+)""\s*>(.*?)</arg\s*>", RegexOptions.Singleline)]
    /// <summary>Matches one argument inside a tool block.</summary>
    private static partial Regex ArgBlock();

    /// <summary>Every tool call in a model turn, in the order it emitted them.</summary>
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
    /// Turns a raw argument into the value the tool receives. Strips the layout newlines
    /// the tag form introduces — the one after the opening tag and the indentation before
    /// the closing tag — so content the model wrote on its own lines round-trips
    /// byte-for-byte, then removes any wrapper the model added around it. Cleanups are
    /// applied here, in order, so every tool and every provider gets the same value.
    /// </summary>
    /// <summary>Trims an argument's surrounding layout newlines and unwraps any CDATA.</summary>
    private static string Normalize(string raw)
    {
        var value = raw;
        if (value.StartsWith("\r\n", StringComparison.Ordinal)) value = value[2..];
        else if (value.StartsWith('\n')) value = value[1..];

        var lastNewline = value.LastIndexOf('\n');
        if (lastNewline >= 0 && value[(lastNewline + 1)..].All(c => c is ' ' or '\t'))
            value = value[..lastNewline];

        return UnwrapCdata(value);
    }

    /// <summary>
    /// Remove a CDATA section that wraps the WHOLE argument, and nothing else.
    /// </summary>
    /// <remarks>
    /// The protocol above is tag-shaped but is not XML — it is a regex over raw text,
    /// chosen precisely so file content needs no escaping. Some models read the shape
    /// rather than the rule and apply real XML conventions to it, wrapping content full
    /// of `&lt;` and `&amp;` in CDATA to protect it. Nothing then consumes the markers, so they
    /// are written to disk as the first and last characters of the file. That shipped a
    /// project whose index.html, script.js and style.css each began `&lt;![CDATA[`: the CSS
    /// would not parse, the JS died on line 1, and the page rendered `]]&gt;` as text — while
    /// every HTTP check still returned 200, so QA saw nothing wrong.
    ///
    /// Here rather than in write_file because the same habit reaches `run` commands and
    /// bug text; one place covers every tool and every provider.
    ///
    /// Only an argument that is ENTIRELY one section is unwrapped. Requiring the trailing
    /// `]]&gt;` to be the first one in the value is what makes that precise: a legitimate XML
    /// file holding two sections also starts with the opener and ends with the closer, and
    /// stripping its outer markers would silently corrupt it.
    /// </remarks>
    /// <summary>Strips a CDATA wrapper, which models sometimes add around file contents.</summary>
    private static string UnwrapCdata(string value)
    {
        const string open = "<![CDATA[";
        const string close = "]]>";

        var body = value.Trim();
        if (!body.StartsWith(open, StringComparison.Ordinal) ||
            !body.EndsWith(close, StringComparison.Ordinal) ||
            body.Length < open.Length + close.Length)
            return value;

        var inner = body[open.Length..^close.Length];
        // A second section means the markers are content, not a wrapper.
        if (inner.Contains(close, StringComparison.Ordinal)) return value;

        // The markers sit on their own lines, so shed the newline each one introduced —
        // and only that one, leaving the file's own blank lines and indentation intact.
        if (inner.StartsWith("\r\n", StringComparison.Ordinal)) inner = inner[2..];
        else if (inner.StartsWith('\n')) inner = inner[1..];
        if (inner.EndsWith("\r\n", StringComparison.Ordinal)) inner = inner[..^2];
        else if (inner.EndsWith('\n')) inner = inner[..^1];

        return inner;
    }
}
