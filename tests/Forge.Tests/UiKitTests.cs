using System.Text.RegularExpressions;
using Forge.Core.Agents;
using Forge.Core.Ui;

namespace Forge.Tests;

/// <summary>
/// The UI kit's two halves: installing it into a client repo, and the gate that keeps pages
/// built from it. Every test works on a throwaway checkout holding a real runnable project.
/// </summary>
public sealed class UiKitTests : IDisposable
{
    private readonly string _repo = Path.Combine(
        Path.GetTempPath(), $"forge-ui-{Guid.NewGuid():N}");

    private static readonly PromptLibrary Prompts = PromptLibrary.Resolve();

    public UiKitTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        if (Directory.Exists(_repo)) Directory.Delete(_repo, recursive: true);
    }

    // ---- installing ----

    [Fact]
    public void The_kit_lands_beside_the_runnable_project_with_its_catalogue_at_the_repo_root()
    {
        ScaffoldWebProject();

        Assert.True(UiKit.Ensure(_repo, new ThemeChoice("slate"), Prompts));

        Assert.True(File.Exists(Path.Combine(_repo, "app", "wwwroot", "forge-ui", "forge-ui.css")));
        Assert.True(File.Exists(Path.Combine(_repo, "app", "wwwroot", "forge-ui", "forge-ui.js")));
        Assert.True(File.Exists(Path.Combine(_repo, "app", "wwwroot", "forge-ui", "theme.css")));
        Assert.True(File.Exists(Path.Combine(_repo, UiKit.CatalogueFile)));
    }

    [Fact]
    public void A_repo_with_nothing_runnable_gets_no_kit_at_all()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "docs only");

        Assert.False(UiKit.Ensure(_repo, new ThemeChoice("slate"), Prompts));
        Assert.False(File.Exists(Path.Combine(_repo, UiKit.CatalogueFile)));
    }

    [Fact]
    public void The_theme_stylesheet_carries_the_theme_the_mode_and_the_knobs()
    {
        ScaffoldWebProject();
        UiKit.Ensure(_repo, new ThemeChoice("carbon", "dark", "red", "compact", "round"), Prompts);

        var css = File.ReadAllText(Path.Combine(_repo, "app", "wwwroot", "forge-ui", "theme.css"));

        // The theme's own palette, the dark mapping, and the four knobs, in that order.
        Assert.Contains("--fg-dark-canvas", css, StringComparison.Ordinal);
        Assert.Contains("color-scheme: dark", css, StringComparison.Ordinal);
        Assert.Contains("--fg-accent-h: 25", css, StringComparison.Ordinal);
        Assert.Contains("--fg-density-scale: 0.88", css, StringComparison.Ordinal);
        Assert.Contains("--fg-radius-mult: 1.6", css, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agents_edit_to_a_kit_file_is_overwritten_on_the_next_install()
    {
        ScaffoldWebProject();
        var choice = new ThemeChoice("slate");
        UiKit.Ensure(_repo, choice, Prompts);

        var kitFile = Path.Combine(_repo, "app", "wwwroot", "forge-ui", "forge-ui.css");
        File.WriteAllText(kitFile, ".fg-btn { background: hotpink; }");

        UiKit.Ensure(_repo, choice, Prompts);
        Assert.Equal(Prompts.UiAsset("forge-ui.css"), File.ReadAllText(kitFile));
    }

    [Fact]
    public void A_theme_that_does_not_exist_is_refused_with_the_ones_that_do()
    {
        var problem = new ThemeChoice("neon").Invalid(UiKit.Themes(Prompts));

        Assert.NotNull(problem);
        Assert.Contains("slate", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_class_the_catalogue_teaches_is_a_class_the_kit_defines()
    {
        var kit = Prompts.UiAsset("forge-ui.css");
        var defined = Regex.Matches(kit, @"\.(?<name>fg-[A-Za-z0-9_-]+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Anything preceded by a dash or a word character is a token (--fg-space-1) or an
        // attribute (data-fg-open), not a class.
        var taught = Regex.Matches(Prompts.UiAsset(UiKit.CatalogueFile), @"(?<![-\w])fg-[a-z][a-z0-9_-]*")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal);

        Assert.Empty(taught.Where(name => !defined.Contains(name)));
    }

    // ---- the gate ----

    [Fact]
    public void A_page_built_from_the_kit_passes()
    {
        InstallKitAndPage("""
            <div class="fg-shell fg-shell--no-sidebar">
              <main class="fg-main">
                <button class="fg-btn fg-btn--primary">Add</button>
              </main>
            </div>
            """);

        Assert.Null(UiGate.Check(_repo));
    }

    [Fact]
    public void An_inline_style_attribute_is_refused_and_the_refusal_names_the_utility_to_use()
    {
        InstallKitAndPage("""<div class="fg-main" style="padding: 12px">hi</div>""");

        var refusal = UiGate.Check(_repo);
        Assert.NotNull(refusal);
        Assert.Contains("fg-pad-4", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hard_coded_colour_is_refused_but_the_same_rule_written_with_tokens_passes()
    {
        InstallKitAndPage("""<div class="app-hero fg-main">hi</div>""");
        var stylesheet = Path.Combine(_repo, "app", "wwwroot", UiKit.AppStylesheet);

        File.WriteAllText(stylesheet, ".app-hero { background: #ff0044; }");
        Assert.Contains("var(--fg-ink)", UiGate.Check(_repo) ?? "", StringComparison.Ordinal);

        File.WriteAllText(stylesheet, ".app-hero { background: var(--fg-surface); font: inherit; }");
        Assert.Null(UiGate.Check(_repo));
    }

    [Fact]
    public void A_class_nothing_defines_is_refused()
    {
        InstallKitAndPage("""<div class="hero-banner">hi</div>""");

        var refusal = UiGate.Check(_repo);
        Assert.NotNull(refusal);
        Assert.Contains("hero-banner", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_stylesheet_of_the_applications_own_is_refused()
    {
        InstallKitAndPage("""<div class="fg-main">hi</div>""");
        File.WriteAllText(Path.Combine(_repo, "app", "wwwroot", "site.css"), ".x { display: block; }");

        var refusal = UiGate.Check(_repo);
        Assert.NotNull(refusal);
        Assert.Contains("site.css", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repo_the_kit_is_not_installed_in_is_not_judged_by_the_gate()
    {
        ScaffoldWebProject();
        File.WriteAllText(Path.Combine(_repo, "app", "wwwroot", "index.html"),
            """<html><body><div style="color: red" class="whatever">hi</div></body></html>""");

        Assert.Null(UiGate.Check(_repo));
    }

    [Fact]
    public void The_reviewer_is_told_how_much_styling_the_application_added_of_its_own()
    {
        InstallKitAndPage("""<div class="app-hero fg-main">hi</div>""");
        File.WriteAllText(Path.Combine(_repo, "app", "wwwroot", UiKit.AppStylesheet),
            ".app-hero { padding: var(--fg-space-4); }");

        var report = UiGate.CustomStyleReport(_repo);
        Assert.NotNull(report);
        Assert.Contains("app-hero", report, StringComparison.Ordinal);
    }

    // ---- setup ----

    /// <summary>A checkout holding one runnable web project.</summary>
    private void ScaffoldWebProject()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "app", "wwwroot"));
        File.WriteAllText(Path.Combine(_repo, "app", "app.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
    }

    /// <summary>A scaffolded project with the kit installed and one page holding the given body.</summary>
    private void InstallKitAndPage(string body)
    {
        ScaffoldWebProject();
        UiKit.Ensure(_repo, new ThemeChoice("slate"), Prompts);
        File.WriteAllText(Path.Combine(_repo, "app", "wwwroot", "index.html"), $"""
            <!doctype html>
            <html><head>
              <link rel="stylesheet" href="/forge-ui/theme.css">
              <link rel="stylesheet" href="/forge-ui/forge-ui.css">
            </head><body>
            {body}
            </body></html>
            """);
    }
}
