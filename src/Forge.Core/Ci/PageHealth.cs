using System.Globalization;
using System.Text.RegularExpressions;
using Forge.Core.Ui;

namespace Forge.Core.Ci;

/// <summary>
/// The rules a rendered page must pass whatever the project is: it loaded, it said nothing was
/// broken, it fits, and what it shows is what it meant to show. Pure functions over a
/// <see cref="PageSnapshot"/> — no browser, no project knowledge — so they are the same for
/// every generated application and can be tested without one.
///
/// Deliberately not here: whether the page looks good, or does what the client asked. The first
/// is the client's call; the second is the acceptance suite's, which knows the requirements.
/// </summary>
public static partial class PageHealth
{
    /// <summary>Text below this contrast ratio against its own background is unreadable (WCAG AA).</summary>
    private const double MinimumContrast = 4.5;

    /// <summary>How many problems of one kind are listed before the rest are counted.</summary>
    private const int MaxPerRule = 5;

    /// <summary>
    /// Everything wrong with the rendered pages, as lines an engineer can act on, or an empty
    /// list when they are healthy.
    /// </summary>
    public static IReadOnlyList<string> Problems(IEnumerable<PageSnapshot> pages)
    {
        List<string> problems = [];
        foreach (var page in pages)
        {
            foreach (var error in page.ConsoleErrors.Take(MaxPerRule))
                problems.Add($"{page.Path}: the browser reported an error — {error}");

            foreach (var request in page.FailedRequests.Take(MaxPerRule))
                problems.Add($"{page.Path}: a request the page made did not succeed — {request}");

            if (page.OverflowsSideways)
                problems.Add($"{page.Path}: the page scrolls sideways at {page.ViewportWidth}px "
                           + $"(it needs {page.ScrollWidth}px). Lay it out so it fits the window.");

            // The `hidden` attribute loses to any class that sets `display`, so an element meant
            // to be hidden can render in full. Markup says hidden, the browser says otherwise.
            foreach (var element in page.Elements.Where(e => e.MarkedHidden && e.Visible).Take(MaxPerRule))
                problems.Add($"{page.Path}: {Name(element)} is marked hidden but is visible on the "
                           + "page. A class setting `display` overrides the `hidden` attribute — "
                           + "toggle a class, or add `[hidden] { display: none !important; }`.");

            foreach (var element in Unreadable(page).Take(MaxPerRule))
                problems.Add($"{page.Path}: {Name(element)} has text too faint to read against its "
                           + $"own background ({element.Ink} on {element.Background}). Use the kit's "
                           + "ink and surface tokens together rather than mixing them.");
        }
        return problems;
    }

    /// <summary>Elements whose text does not meet the minimum contrast against their own background.</summary>
    private static IEnumerable<PageElement> Unreadable(PageSnapshot page) =>
        page.Elements.Where(e =>
            e.Visible && e.Text.Length > 0 &&
            Rgb(e.Ink) is { } ink && Rgb(e.Background) is { } background &&
            Contrast(ink, background) < MinimumContrast);

    /// <summary>
    /// The WCAG contrast ratio between two colours, from their relative luminance. Both are
    /// opaque by the time they are compared: a transparent background is skipped, since the
    /// element's real backdrop is its ancestor's and the browser does not report that.
    /// </summary>
    public static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (light, dark) = Luminance(a) >= Luminance(b) ? (Luminance(a), Luminance(b))
                                                        : (Luminance(b), Luminance(a));
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>
    /// How far apart two colours are, 0 to 1, as the mean channel difference. Crude next to a
    /// perceptual metric, and enough for the only question asked of it: are these two surfaces
    /// meant to be different actually different.
    /// </summary>
    public static double Distance((double R, double G, double B) a, (double R, double G, double B) b) =>
        (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;

    /// <summary>
    /// A computed `rgb(r, g, b)` or `rgba(r, g, b, a)` as channels in 0..1, or null when it is
    /// not one — `transparent`, a gradient, or a colour space the page reported verbatim.
    /// </summary>
    public static (double R, double G, double B)? Rgb(string colour)
    {
        var match = RgbPattern().Match(colour ?? "");
        if (!match.Success) return null;

        // Fully transparent tells us nothing about what is behind it.
        if (match.Groups[4].Success &&
            double.TryParse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha)
            && alpha < 0.99) return null;

        return (Channel(match.Groups[1].Value), Channel(match.Groups[2].Value), Channel(match.Groups[3].Value));
    }

    /// <summary>Relative luminance, gamma-corrected per WCAG.</summary>
    private static double Luminance((double R, double G, double B) c) =>
        0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(double channel) =>
        channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static double Channel(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture) / 255.0;

    /// <summary>How an element is named in a message: its handle if it has one, else what it is.</summary>
    private static string Name(PageElement element) =>
        element.TestId is { Length: > 0 } id ? $"`{id}`"
        : element.Text is { Length: > 0 } text ? $"the {element.Tag} reading \"{Cut(text)}\""
        : $"a {element.Tag}";

    private static string Cut(string text) => text.Length <= 30 ? text : text[..29] + "…";

    [GeneratedRegex(@"rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)\s*(?:[,/]\s*([\d.]+)\s*)?\)")]
    private static partial Regex RgbPattern();
}
