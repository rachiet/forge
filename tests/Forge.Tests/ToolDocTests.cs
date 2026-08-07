using Forge.Core.Agents;

namespace Forge.Tests;

/// <summary>
/// The tool surface as the model reads it. These assert the shape rather than the wording:
/// one line saying what the tool does, then every argument on its own line tagged Required
/// or Optional — the structure that stops a required argument being mentioned only in
/// passing, which is how a QA round emitted nine write_file calls with no path.
/// </summary>
public class ToolDocTests
{
    [Fact]
    public void A_tool_renders_its_summary_then_one_line_per_argument()
    {
        var lines = AgentToolset.Catalogue["write_file"].Render("write_file").Split('\n');

        Assert.StartsWith("write_file — create or overwrite a file.", lines[0]);
        Assert.Contains("path [Required]", lines[1]);
        Assert.Contains("content [Required]", lines[2]);
    }

    [Fact]
    public void Optional_arguments_say_so_in_the_same_place()
    {
        var rendered = AgentToolset.Catalogue["read_file"].Render("read_file");

        Assert.Contains("path [Required]", rendered);
        Assert.Contains("start [Optional]", rendered);
        Assert.Contains("end [Optional]", rendered);
    }

    [Fact]
    public void Every_tool_documents_a_summary_and_every_argument()
    {
        // A tool the model is offered with no description, or an argument with no
        // explanation, is one it has to guess at — the whole point of the structure.
        Assert.All(AgentToolset.Catalogue, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Summary), $"{entry.Key} has no summary");
            Assert.All(entry.Value.Args, arg =>
            {
                Assert.False(string.IsNullOrWhiteSpace(arg.Name), $"{entry.Key} has an unnamed argument");
                Assert.False(string.IsNullOrWhiteSpace(arg.Description),
                    $"{entry.Key}.{arg.Name} has no description");
            });
        });
    }

    [Fact]
    public void Required_arguments_are_listed_before_optional_ones()
    {
        // Reading order is the only ordering the model gets; a required argument buried
        // under three optional ones reads like an afterthought.
        Assert.All(AgentToolset.Catalogue, entry =>
        {
            var required = entry.Value.Args.Select(a => a.Required).ToList();
            Assert.Equal(required.OrderByDescending(r => r), required);
        });
    }
}
