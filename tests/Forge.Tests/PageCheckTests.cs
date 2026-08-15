using Forge.Core.Ci;
using Forge.Core.Design;
using Forge.Core.Ui;

namespace Forge.Tests;

/// <summary>
/// The rules that decide whether a rendered page is healthy, and when the browser is opened at
/// all. Pure functions over a snapshot, so they are tested without a browser or a project.
/// </summary>
public class PageHealthTests
{
    private static PageElement Element(
        string testId = "", string text = "", bool visible = true, bool markedHidden = false,
        string background = "rgb(255, 255, 255)", string ink = "rgb(0, 0, 0)") =>
        new(testId, "div", "", text, "", new PageBox(0, 0, 100, 40), visible, markedHidden, background, ink);

    private static PageSnapshot Page(
        IEnumerable<PageElement>? elements = null,
        IEnumerable<string>? errors = null,
        IEnumerable<string>? failures = null,
        int scrollWidth = PageProbe.ViewportWidth) =>
        new("/", "Test", PageProbe.ViewportWidth, scrollWidth,
            [.. elements ?? []], [.. errors ?? []], [.. failures ?? []]);

    [Fact]
    public void A_healthy_page_has_nothing_to_report() =>
        Assert.Empty(PageHealth.Problems([Page([Element(text: "Hello")])]));

    [Fact]
    public void An_element_marked_hidden_that_still_renders_is_reported()
    {
        // The exact defect a DOM check cannot see: `hidden` loses to any class setting
        // `display`, so the markup says hidden and the browser shows it anyway.
        var problems = PageHealth.Problems(
            [Page([Element(testId: "board-name-editing", markedHidden: true, visible: true)])]);

        var problem = Assert.Single(problems);
        Assert.Contains("board-name-editing", problem);
        Assert.Contains("marked hidden but is visible", problem);
    }

    [Fact]
    public void An_element_that_is_hidden_and_stays_hidden_is_fine() =>
        Assert.Empty(PageHealth.Problems([Page([Element(markedHidden: true, visible: false)])]));

    [Fact]
    public void A_page_that_needs_sideways_scrolling_is_reported()
    {
        var problems = PageHealth.Problems([Page([], scrollWidth: PageProbe.ViewportWidth + 400)]);
        Assert.Contains("scrolls sideways", Assert.Single(problems));
    }

    [Fact]
    public void Console_errors_and_failed_requests_are_reported()
    {
        var problems = PageHealth.Problems([Page(
            errors: ["TypeError: cards is not iterable"],
            failures: ["404 http://localhost/app.js"])]);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("TypeError"));
        Assert.Contains(problems, p => p.Contains("404"));
    }

    [Fact]
    public void Text_too_faint_to_read_against_its_own_background_is_reported()
    {
        var problems = PageHealth.Problems([Page([
            Element(text: "Quiet label", ink: "rgb(220, 220, 220)", background: "rgb(255, 255, 255)")])]);

        Assert.Contains("too faint to read", Assert.Single(problems));
    }

    [Fact]
    public void A_transparent_background_is_not_judged_for_contrast() =>
        // The element's real backdrop belongs to an ancestor, and the browser does not report
        // it — so a guess here would be a false failure on every nested element.
        Assert.Empty(PageHealth.Problems([Page([
            Element(text: "On its parent's ground", ink: "rgb(240, 240, 240)",
                    background: "rgba(0, 0, 0, 0)")])]));

    [Theory]
    // Changing a page's markup, styling or scripts can change what it renders.
    [InlineData("src/App/wwwroot/index.html", true)]
    [InlineData("src/App/wwwroot/app.css", true)]
    [InlineData("src/App/Pages/Board.razor", true)]
    // Changing storage or a domain rule cannot, so the browser is never opened for it.
    [InlineData("src/Storage/SqliteCardStore.cs", false)]
    [InlineData("docs/requirements/01-board.md", false)]
    public void The_browser_is_opened_only_for_work_that_could_change_the_page(string file, bool expected) =>
        Assert.Equal(expected, PageCheck.TouchesInterface([file]));

    [Fact]
    public void Contrast_is_computed_the_way_the_accessibility_standard_defines_it()
    {
        var black = PageHealth.Rgb("rgb(0, 0, 0)")!.Value;
        var white = PageHealth.Rgb("rgb(255, 255, 255)")!.Value;

        // Black on white is the standard's maximum, 21:1.
        Assert.Equal(21.0, PageHealth.Contrast(black, white), 1);
        Assert.Equal(1.0, PageHealth.Contrast(white, white), 1);
    }

    [Fact]
    public void Two_tints_that_resolve_to_the_same_colour_are_not_different()
    {
        // The second defect from the live build: `accent-soft` and `success-soft` both came out
        // the same pale green, so two of three columns looked identical.
        var accentSoft = PageHealth.Rgb("rgb(231, 250, 234)")!.Value;
        var successSoft = PageHealth.Rgb("rgb(230, 252, 234)")!.Value;

        Assert.True(PageHealth.Distance(accentSoft, successSoft) < 0.02);
        Assert.True(PageHealth.Distance(accentSoft, PageHealth.Rgb("rgb(255, 243, 224)")!.Value) >= 0.02);
    }

    [Fact]
    public void A_declared_handle_the_page_does_not_carry_is_reported_unless_it_repeats()
    {
        // `recent-entry` is one row per item and CI renders against an empty database, so its
        // absence is the correct state; `figure-hours-tracked` is always there or it is a defect.
        var contract = new InterfaceContract([
            new InterfacePage("/", "04-dashboard.md", [
                new InterfaceElement("figure-hours-tracked", "the hours card", OnDemand: false, Repeats: false),
                new InterfaceElement("recent-entry", "a recent entry row", OnDemand: false, Repeats: true),
            ]),
        ]);

        var problems = PageCheck.MissingHandles(contract, [Page()]).ToList();

        var problem = Assert.Single(problems);
        Assert.Contains("figure-hours-tracked", problem, StringComparison.Ordinal);
    }
}
