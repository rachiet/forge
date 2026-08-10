using Forge.Core.Ci;

namespace Forge.Tests;

/// <summary>
/// The check that reads what the browser reads. Each case is a defect that shipped to review
/// on snipboard, or one that the check must NOT invent.
/// </summary>
public sealed class StaticFileCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"forge-static-{Guid.NewGuid():N}");

    public StaticFileCheckTests() => Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void A_sound_repo_reports_nothing()
    {
        Write("wwwroot/app.js", "const go = () => 1;\n");
        Write("wwwroot/data.json", """{"a": 1}""");
        Write("wwwroot/index.html", """<html><body><script src="app.js"></script></body></html>""");

        Assert.Null(StaticFileCheck.Check(_root));
    }

    [Fact]
    public void Html_escaped_javascript_is_caught_before_it_reaches_the_browser()
    {
        // The real defect: `=>` committed as `=&gt;`, which a served .js is never decoded from.
        Write("wwwroot/snippet.js", "const load = (id) =&gt; fetch(id);\n");

        var problems = StaticFileCheck.Check(_root);

        Assert.NotNull(problems);
        Assert.Contains("wwwroot/snippet.js", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void A_javascript_file_ending_in_a_markup_tag_is_caught()
    {
        Write("wwwroot/home.js", "const x = 1;\n</script>\n");

        Assert.Contains("wwwroot/home.js", StaticFileCheck.Check(_root)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_caught()
    {
        Write("wwwroot/config.json", """{"a": 1,}""");

        Assert.Contains("wwwroot/config.json", StaticFileCheck.Check(_root)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_referencing_a_file_that_is_not_in_the_repo_is_caught()
    {
        // Silent in a browser: the page renders and the script simply never loads.
        Write("wwwroot/index.html", """<html><head><script src="/js/home.js"></script></head></html>""");

        var problems = StaticFileCheck.Check(_root);

        Assert.Contains("/js/home.js", problems!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rooted_reference_resolves_against_the_static_root_not_the_repo_root()
    {
        Write("wwwroot/js/home.js", "const x = 1;\n");
        Write("wwwroot/index.html", """<html><head><script src="/js/home.js"></script></head></html>""");

        Assert.Null(StaticFileCheck.Check(_root));
    }

    [Fact]
    public void An_inline_script_that_does_not_parse_is_caught()
    {
        Write("wwwroot/index.html", "<html><body><script>const x = (1 +;</script></body></html>");

        Assert.Contains("inline", StaticFileCheck.Check(_root)!, StringComparison.Ordinal);
    }

    [Fact]
    public void External_urls_and_anchors_are_somebody_elses_to_resolve()
    {
        Write("wwwroot/index.html", """
            <html><head>
              <link href="https://cdn.example.com/a.css">
              <link href="//cdn.example.com/b.css">
              <img src="data:image/png;base64,AAAA">
            </head><body><a href="#top">top</a></body></html>
            """);

        Assert.Null(StaticFileCheck.Check(_root));
    }

    [Fact]
    public void Markup_itself_is_not_judged()
    {
        // HTML defines error recovery, so unclosed tags are not a defect this check invents.
        Write("wwwroot/index.html", "<html><body><div><p>unclosed everything");

        Assert.Null(StaticFileCheck.Check(_root));
    }

    [Fact]
    public void Build_output_and_dependencies_are_not_checked()
    {
        Write("bin/Debug/broken.js", "const f = (a) =&gt; a;\n");
        Write("obj/broken.json", "{,}");
        Write("node_modules/pkg/broken.js", "}}}");

        Assert.Null(StaticFileCheck.Check(_root));
    }
}
