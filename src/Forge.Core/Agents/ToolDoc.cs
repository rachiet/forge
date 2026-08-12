using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Core.Agents;

/// <summary>One argument, as the model is told about it.</summary>
/// <param name="Name">The `name` attribute the call must carry, spelled exactly.</param>
/// <param name="Required">
/// Whether omitting it is refused. Rendered as a literal `[Required]` / `[Optional]` tag
/// rather than left to the reader: a bracketed name in a signature is a convention the
/// model has to know, and one that dropped a required `path` nine times in a row did not.
/// </param>
/// <param name="Description">What the argument is for, and what a wrong value costs.</param>
public sealed record ToolArg(string Name, bool Required, string Description);

/// <summary>
/// A tool as documented to the model: one line saying what it does, then one line per
/// argument. Structured rather than free prose so every tool in the catalogue reads the
/// same way, and so a required argument cannot end up mentioned only in passing halfway
/// through a paragraph — which is what the old single-string entries allowed.
/// </summary>
/// <remarks>
/// This is the only description of the tool surface. It sits beside the implementations it
/// documents and is rendered into the prompt from the recipe's tool list, so a role cannot
/// be told about a tool it does not have, and a tool cannot be described in two places that
/// disagree.
/// </remarks>
public sealed record ToolDoc(string Summary, params ToolArg[] Args)
{
    /// <summary>The prompt form: `name — summary`, then the arguments indented beneath it.</summary>
    public string Render(string name)
    {
        var sb = new StringBuilder($"{name} — {Summary}");
        foreach (var arg in Args)
            sb.Append($"\n    {arg.Name} [{(arg.Required ? "Required" : "Optional")}] — {arg.Description}");
        return sb.ToString();
    }

    /// <summary>
    /// The same tool as a JSON Schema for the provider's own tool-calling surface, so the model
    /// is constrained by the API rather than by a syntax described in the prompt. Every argument
    /// is a string: the toolset reads them as text and parses what it needs, so declaring a
    /// number here would only give the schema a way to reject a value the tool would accept.
    /// </summary>
    public string ParametersJson()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var arg in Args)
        {
            properties[arg.Name] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = arg.Description,
            };
            if (arg.Required) required.Add(arg.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        }.ToJsonString();
    }

    /// <summary>A required argument.</summary>
    public static ToolArg Required(string name, string description) => new(name, true, description);

    /// <summary>An optional argument.</summary>
    public static ToolArg Optional(string name, string description) => new(name, false, description);
}
