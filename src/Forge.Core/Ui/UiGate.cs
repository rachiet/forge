using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core.Ui;

/// <summary>
/// Checks that a repo's pages are built from the UI kit rather than from hand-rolled styling.
/// Four rules, each with a true or false answer: no inline style attributes, no literal colours
/// or font names outside the kit, no stylesheet of the application's own beyond app.css, and no
/// class that is neither a kit class nor defined in app.css. A repo the kit is not installed in
/// passes without being read.
///
/// The message it returns is the one the engineer resumes with: it names the offending file and
/// line, and the form that would have been accepted.
/// </summary>
public static partial class UiGate
{
    /// <summary>Directories holding build output, dependencies or git state rather than source.</summary>
    private static readonly string[] Ignored = [".git", "bin", "obj", "node_modules"];

    /// <summary>How many offending lines of one kind are listed before the rest are summarised.</summary>
    private const int MaxExamples = 8;

    /// <summary>
    /// The colour keywords a declaration is refused for: the common ones, in every spelling a
    /// page is likely to use. `transparent` and `currentColor` are absent — both take the theme's
    /// colour rather than naming one.
    /// </summary>
    private static readonly HashSet<string> NamedColours = new(StringComparer.OrdinalIgnoreCase)
    {
        "white", "black", "red", "green", "blue", "yellow", "orange", "purple", "pink", "brown",
        "gray", "grey", "silver", "gold", "navy", "teal", "olive", "maroon", "lime", "aqua",
        "cyan", "magenta", "beige", "ivory", "khaki", "coral", "salmon", "crimson", "indigo",
        "violet", "turquoise", "plum", "orchid", "tomato", "azure", "lavender",
    };

    /// <summary>
    /// Values that name no font of their own and are therefore allowed, such as `font: inherit`.
    /// </summary>
    private static readonly HashSet<string> Inherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "inherit", "initial", "unset", "revert", "none",
    };

    /// <summary>
    /// What is wrong with the repo's styling, or null when nothing is. Reports the first rule
    /// broken, with its examples, rather than every rule at once.
    /// </summary>
    public static string? Check(string workspaceDir)
    {
        if (UiKit.KitRoot(workspaceDir) is not { } kitRoot || !Directory.Exists(kitRoot)) return null;

        var pages = SourceFiles(workspaceDir, kitRoot, ".html", ".htm").ToList();
        var stylesheets = SourceFiles(workspaceDir, kitRoot, ".css").ToList();

        return StrayStylesheet(workspaceDir, stylesheets)
            ?? InlineStyles(workspaceDir, pages)
            ?? RemoteStylesheets(workspaceDir, pages)
            ?? HardCodedValues(workspaceDir, pages, stylesheets)
            ?? UnknownClasses(workspaceDir, kitRoot, pages, stylesheets);
    }

    /// <summary>
    /// How much styling the application defines of its own — how many classes, over how many
    /// lines of app.css, and their names — or null when it defines none.
    /// </summary>
    public static string? CustomStyleReport(string workspaceDir)
    {
        if (UiKit.KitRoot(workspaceDir) is not { } kitRoot || !Directory.Exists(kitRoot)) return null;

        var appStylesheets = SourceFiles(workspaceDir, kitRoot, ".css")
            .Where(f => string.Equals(Path.GetFileName(f), UiKit.AppStylesheet, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (appStylesheets.Count == 0) return null;

        var lines = appStylesheets.Sum(f => File.ReadAllLines(f).Count(l => l.Trim().Length > 0));
        var classes = appStylesheets.SelectMany(f => DefinedClasses(File.ReadAllText(f))).Distinct().ToList();
        if (classes.Count == 0) return null;

        return $"This repo defines {classes.Count} class(es) of its own in {UiKit.AppStylesheet} "
             + $"({lines} non-blank lines): {string.Join(", ", classes.Take(20))}. "
             + "The UI kit covers buttons, forms, cards, lists, tables, modals, tabs, menus and "
             + "layout; bespoke CSS for anything the kit already provides should be sent back.";
    }

    // ---- the four rules ----

    /// <summary>Any stylesheet in the repo other than the one permitted app.css.</summary>
    private static string? StrayStylesheet(string workspaceDir, IReadOnlyList<string> stylesheets)
    {
        var stray = stylesheets
            .Where(f => !string.Equals(Path.GetFileName(f), UiKit.AppStylesheet, StringComparison.OrdinalIgnoreCase))
            .Select(f => Relative(workspaceDir, f))
            .ToList();
        if (stray.Count == 0) return null;

        return $"""
            This repo has stylesheets of its own beyond the one that is allowed:

            {Bullets(stray)}

            The look of this application comes from the UI kit at {UiKit.KitDirectory}, which is
            already linked from your pages. Delete these files and use the kit's classes. If some
            layout genuinely has no kit equivalent, put it in a single `{UiKit.AppStylesheet}`
            beside your pages, written only with the `var(--fg-…)` tokens listed in {UiKit.CatalogueFile}.
            """;
    }

    /// <summary>Any element carrying a style attribute.</summary>
    private static string? InlineStyles(string workspaceDir, IReadOnlyList<string> pages)
    {
        var found = new List<string>();
        foreach (var page in pages)
            foreach (var line in Lines(page))
                if (InlineStylePattern().IsMatch(line.Text))
                    found.Add($"{Relative(workspaceDir, page)}:{line.Number}  {line.Text.Trim()}");

        if (found.Count == 0) return null;

        return $"""
            These elements carry an inline `style` attribute:

            {Bullets(found)}

            Inline styles are invisible to the theme, so they survive dark mode and the client's
            accent colour unchanged and make the page look broken. Use a kit class instead — see
            {UiKit.CatalogueFile} for the layout, spacing and width utilities that replace them
            (`fg-row`, `fg-stack`, `fg-gap-3`, `fg-w-lg`, `fg-pad-4`).
            """;
    }

    /// <summary>Any page loading a stylesheet or font from another host.</summary>
    private static string? RemoteStylesheets(string workspaceDir, IReadOnlyList<string> pages)
    {
        var found = new List<string>();
        foreach (var page in pages)
            foreach (var line in Lines(page))
            {
                var loadsStyles = line.Text.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                               || line.Text.Contains("@import", StringComparison.OrdinalIgnoreCase);
                var offHost = line.Text.Contains("://", StringComparison.Ordinal)
                           || line.Text.Contains("\"//", StringComparison.Ordinal)
                           || line.Text.Contains("'//", StringComparison.Ordinal);
                if (loadsStyles && offHost)
                    found.Add($"{Relative(workspaceDir, page)}:{line.Number}  {line.Text.Trim()}");
            }

        if (found.Count == 0) return null;

        return $"""
            These pages load a stylesheet or font from another host:

            {Bullets(found)}

            Nothing is fetched from the network: the application must run offline, and a remote
            stylesheet would override the theme wherever it loaded. The kit's fonts and styles are
            already in the repo at {UiKit.KitDirectory}.
            """;
    }

    /// <summary>A colour or font written as a literal rather than taken from the theme.</summary>
    private static string? HardCodedValues(
        string workspaceDir, IReadOnlyList<string> pages, IReadOnlyList<string> stylesheets)
    {
        var found = new List<string>();

        foreach (var file in stylesheets)
            foreach (var line in Lines(file))
                if (LiteralIn(line.Text) is { } problem)
                    found.Add($"{Relative(workspaceDir, file)}:{line.Number}  {problem}");

        // A style block written into a page is CSS too, and is read the same way.
        foreach (var page in pages)
        {
            var html = File.ReadAllText(page);
            foreach (var block in StyleBlockPattern().Matches(html).Select(m => m.Groups["body"].Value))
                foreach (var line in block.Split('\n'))
                    if (LiteralIn(line) is { } problem)
                        found.Add($"{Relative(workspaceDir, page)} (in <style>)  {problem}");
        }

        if (found.Count == 0) return null;

        return $"""
            These declarations hard-code a colour or a font:

            {Bullets(found)}

            Every colour and font in this application comes from the theme, so a literal one stays
            put when the client switches to dark mode or asks for a different accent. Replace it
            with a token: `var(--fg-ink)`, `var(--fg-ink-muted)`, `var(--fg-surface)`,
            `var(--fg-border)`, `var(--fg-accent)`, `var(--fg-danger)`, `var(--fg-font-sans)`.
            {UiKit.CatalogueFile} lists them all. Only `transparent`, `currentColor` and `inherit`
            may be written directly.
            """;
    }

    /// <summary>A class used on an element that neither the kit nor app.css defines.</summary>
    private static string? UnknownClasses(
        string workspaceDir, string kitRoot, IReadOnlyList<string> pages, IReadOnlyList<string> stylesheets)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(kitRoot, "*.css"))
            known.UnionWith(DefinedClasses(File.ReadAllText(file)));
        foreach (var file in stylesheets)
            known.UnionWith(DefinedClasses(File.ReadAllText(file)));

        var unknown = new List<string>();
        foreach (var page in pages)
            foreach (var line in Lines(page))
                foreach (Match match in ClassAttributePattern().Matches(line.Text))
                    foreach (var name in match.Groups["classes"].Value
                                 .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // A token holding anything but class characters is a template
                        // expression, filled in at render time rather than a class name.
                        if (!ClassNamePattern().IsMatch(name) || known.Contains(name)) continue;
                        unknown.Add($"{Relative(workspaceDir, page)}:{line.Number}  class=\"…{name}…\"");
                    }

        if (unknown.Count == 0) return null;

        return $"""
            These classes are used but never defined — not by the UI kit, and not by
            {UiKit.AppStylesheet}:

            {Bullets(unknown.Distinct().ToList())}

            A class nothing defines styles nothing, so the element renders unstyled. Use the kit
            class for what you are building ({UiKit.CatalogueFile} lists every one), or, if this
            really is layout the kit has no answer for, define it in `{UiKit.AppStylesheet}` using
            only `var(--fg-…)` tokens.
            """;
    }

    // ---- reading files ----

    /// <summary>One line of a file, with its 1-based number.</summary>
    private readonly record struct SourceLine(int Number, string Text);

    private static IEnumerable<SourceLine> Lines(string file) =>
        File.ReadAllLines(file).Select((text, index) => new SourceLine(index + 1, text));

    /// <summary>
    /// Every file with one of these extensions in the repo, excluding build output, git state
    /// and the kit's own directory.
    /// </summary>
    private static IEnumerable<string> SourceFiles(string workspaceDir, string kitRoot, params string[] extensions) =>
        Directory.EnumerateFiles(workspaceDir, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(kitRoot, StringComparison.Ordinal))
            .Where(f => !Ignored.Any(dir =>
                f.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                 .Any(part => string.Equals(part, dir, StringComparison.OrdinalIgnoreCase))))
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string Relative(string workspaceDir, string file) =>
        Path.GetRelativePath(workspaceDir, file).Replace('\\', '/');

    /// <summary>The examples for one rule, listing at most MaxExamples and counting the rest.</summary>
    private static string Bullets(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items.Take(MaxExamples)) sb.Append("- ").Append(item).Append('\n');
        if (items.Count > MaxExamples) sb.Append($"- …and {items.Count - MaxExamples} more\n");
        return sb.ToString().TrimEnd();
    }

    // ---- reading CSS ----

    /// <summary>
    /// The literal colour or font in one line of CSS, as `property: value`, or null when it has
    /// none. Reads the line one declaration at a time, so a selector or an at-rule is not one.
    /// </summary>
    private static string? LiteralIn(string cssLine)
    {
        foreach (Match declaration in DeclarationPattern().Matches(cssLine))
        {
            var property = declaration.Groups["prop"].Value;
            var value = declaration.Groups["value"].Value.Trim();
            if (value.Length == 0) continue;

            if (ColourFunctionPattern().IsMatch(value))
                return $"{property}: {value}";

            if (value.Split(' ', ',', '(', ')', '/').Any(NamedColours.Contains))
                return $"{property}: {value}";

            var isFont = property.Equals("font-family", StringComparison.OrdinalIgnoreCase)
                      || property.Equals("font", StringComparison.OrdinalIgnoreCase);
            if (isFont && !value.Contains("var(--fg-font", StringComparison.Ordinal)
                       && !Inherited.Contains(value))
                return $"{property}: {value}";
        }

        return null;
    }

    /// <summary>Every class name a stylesheet defines, read from its selectors.</summary>
    private static IEnumerable<string> DefinedClasses(string css) =>
        SelectorClassPattern().Matches(css).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal);

    // ---- patterns ----

    /// <summary>Matches a style attribute on an element.</summary>
    [GeneratedRegex("""\sstyle\s*=\s*["']""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStylePattern();

    /// <summary>Matches a class attribute, capturing the whole space-separated list.</summary>
    [GeneratedRegex("""class\s*=\s*["'](?<classes>[^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttributePattern();

    /// <summary>Matches a plain class name, as opposed to a template expression.</summary>
    [GeneratedRegex("^[A-Za-z][-A-Za-z0-9_]*$")]
    private static partial Regex ClassNamePattern();

    /// <summary>Matches one CSS declaration, capturing its property and its value.</summary>
    [GeneratedRegex("""(?<prop>--?[a-zA-Z][-a-zA-Z0-9]*|[a-zA-Z][-a-zA-Z0-9]*)\s*:\s*(?<value>[^;{}]*)""")]
    private static partial Regex DeclarationPattern();

    /// <summary>Matches a colour written as a hex code or a colour function.</summary>
    [GeneratedRegex("""#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\s*\(""")]
    private static partial Regex ColourFunctionPattern();

    /// <summary>Matches a class selector in a stylesheet, capturing the class name.</summary>
    [GeneratedRegex("""\.(?<name>[A-Za-z][-A-Za-z0-9_]*)""")]
    private static partial Regex SelectorClassPattern();

    /// <summary>Matches a style element written into a page, capturing its body.</summary>
    [GeneratedRegex("""<style[^>]*>(?<body>.*?)</style\s*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlockPattern();
}
