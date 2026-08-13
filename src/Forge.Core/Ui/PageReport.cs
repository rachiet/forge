using System.Text;

namespace Forge.Core.Ui;

/// <summary>Where an element sits on the page, in CSS pixels.</summary>
/// <param name="X">Distance from the page's left edge.</param>
/// <param name="Y">Distance from the top of the document.</param>
/// <param name="Width">Rendered width; zero means it takes no space.</param>
/// <param name="Height">Rendered height.</param>
public sealed record PageBox(double X, double Y, double Width, double Height);

/// <summary>
/// One element as the browser actually rendered it. Everything here is computed, not declared:
/// this is what makes an interface testable, since markup alone cannot say whether an element is
/// visible, where it sits, or what colour it ended up.
/// </summary>
/// <param name="TestId">Its `data-testid`, the stable handle a test addresses it by.</param>
/// <param name="Tag">The element's tag name, lowercase.</param>
/// <param name="Role">Its ARIA role, explicit or implicit; empty when it has none.</param>
/// <param name="Text">Its trimmed text, cut short — enough to recognise it, not to reproduce it.</param>
/// <param name="Classes">The class attribute, so a test can see which kit components are in use.</param>
/// <param name="Box">Where it rendered.</param>
/// <param name="Visible">Whether it is displayed and has a non-zero box.</param>
/// <param name="MarkedHidden">Whether it carries the `hidden` attribute, whatever CSS then did.</param>
/// <param name="Background">Its computed background colour, as rgb().</param>
/// <param name="Ink">Its computed text colour.</param>
public sealed record PageElement(
    string TestId,
    string Tag,
    string Role,
    string Text,
    string Classes,
    PageBox Box,
    bool Visible,
    bool MarkedHidden,
    string Background,
    string Ink);

/// <summary>
/// One page as rendered: its elements, what the browser complained about, and whether it fits.
/// Produced by <see cref="PageProbe"/> from a running application and read by both the health
/// rules and the agent that writes the acceptance suite.
/// </summary>
/// <param name="Path">The path loaded, e.g. `/`.</param>
/// <param name="Title">The document title.</param>
/// <param name="ViewportWidth">The width the page was rendered at.</param>
/// <param name="ScrollWidth">The width the page actually needs; wider than the viewport is overflow.</param>
/// <param name="Elements">The elements worth naming: those with a test id, a role, or a box.</param>
/// <param name="ConsoleErrors">Console errors and page exceptions, in the order they happened.</param>
/// <param name="FailedRequests">Requests that failed or returned 4xx/5xx, as `status url`.</param>
/// <param name="Screenshot">Absolute path of the screenshot taken, when one was.</param>
public sealed record PageSnapshot(
    string Path,
    string Title,
    int ViewportWidth,
    int ScrollWidth,
    IReadOnlyList<PageElement> Elements,
    IReadOnlyList<string> ConsoleErrors,
    IReadOnlyList<string> FailedRequests,
    string? Screenshot = null)
{
    /// <summary>Whether the page needs sideways scrolling at the width it was rendered at.</summary>
    public bool OverflowsSideways => ScrollWidth > ViewportWidth + 1;

    /// <summary>
    /// The page as an agent reads it: one line per element with its handle, position, size and
    /// colours. Deliberately flat and short — this goes into a packet, and an agent writing an
    /// assertion needs handles and numbers, not markup.
    /// </summary>
    public string Describe(int maxElements = 60)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### {Path} — \"{Title}\" ({ViewportWidth}px wide)");
        if (OverflowsSideways) sb.AppendLine($"- the page scrolls sideways: needs {ScrollWidth}px");
        foreach (var error in ConsoleErrors) sb.AppendLine($"- console error: {error}");
        foreach (var request in FailedRequests) sb.AppendLine($"- failed request: {request}");
        sb.AppendLine();

        sb.AppendLine("| testid | tag | role | text | x,y | w×h | visible | background |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var e in Elements.Take(maxElements))
            sb.AppendLine($"| {Or(e.TestId)} | {e.Tag} | {Or(e.Role)} | {Or(Short(e.Text))} | "
                        + $"{e.Box.X:0},{e.Box.Y:0} | {e.Box.Width:0}×{e.Box.Height:0} | "
                        + $"{(e.Visible ? "yes" : "no")}{(e.MarkedHidden ? " (hidden attr)" : "")} | "
                        + $"{Or(e.Background)} |");

        if (Elements.Count > maxElements)
            sb.AppendLine($"| … | {Elements.Count - maxElements} more elements | | | | | | |");
        return sb.ToString();
    }

    private static string Or(string value) => value.Length > 0 ? value : "—";

    private static string Short(string text) =>
        text.Length <= 40 ? text : text[..39].TrimEnd() + "…";
}
