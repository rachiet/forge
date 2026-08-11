using Forge.Core.Db;

namespace Forge.Core.Ui;

/// <summary>
/// The look of one project's user interface: a theme from the kit plus four knobs, each a value
/// from a closed set. Stored in project_meta and rendered into theme.css by <see cref="UiKit"/>.
/// </summary>
/// <param name="Theme">A theme id from the kit, e.g. `slate`.</param>
/// <param name="Mode">`light`, `dark`, or `auto` to follow the viewer's system.</param>
/// <param name="Accent">A named accent colour, or null to keep the theme's own.</param>
/// <param name="Density">How tight the spacing is, relative to the theme.</param>
/// <param name="Radius">How round the corners are, relative to the theme.</param>
public sealed record ThemeChoice(
    string Theme,
    string Mode = ThemeChoice.DefaultMode,
    string? Accent = null,
    string Density = ThemeChoice.DefaultDensity,
    string Radius = ThemeChoice.DefaultRadius)
{
    /// <summary>The theme a project gets when nobody chose one.</summary>
    public const string DefaultTheme = "slate";

    /// <summary>Follows the viewer's system setting.</summary>
    public const string DefaultMode = "auto";

    /// <summary>The theme's own spacing, neither tightened nor loosened.</summary>
    public const string DefaultDensity = "normal";

    /// <summary>The theme's own corners, neither sharpened nor rounded.</summary>
    public const string DefaultRadius = "default";

    // The project_meta key each field is stored under.
    private const string ThemeKey = "ui_theme";
    private const string ModeKey = "ui_mode";
    private const string AccentKey = "ui_accent";
    private const string DensityKey = "ui_density";
    private const string RadiusKey = "ui_radius";

    /// <summary>How the viewer's colour scheme is decided. Each maps to one file in modes/.</summary>
    public static readonly IReadOnlyList<string> Modes = ["light", "dark", "auto"];

    /// <summary>Spacing multiplier applied on top of the theme's own density.</summary>
    public static readonly IReadOnlyDictionary<string, double> Densities =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            // Tighter rows and padding, for data-heavy screens.
            ["compact"] = 0.88,
            // The theme's own spacing, unchanged.
            ["normal"] = 1.0,
            // Looser, for reading-led or sparse screens.
            ["roomy"] = 1.15,
        };

    /// <summary>Corner-radius multiplier applied on top of the theme's own character.</summary>
    public static readonly IReadOnlyDictionary<string, double> Radii =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            // Squarer than the theme draws them.
            ["sharp"] = 0.45,
            // The theme's own corners, unchanged.
            ["default"] = 1.0,
            // Rounder than the theme draws them.
            ["round"] = 1.6,
        };

    /// <summary>
    /// The accent colours that can be chosen, each as the oklch hue and chroma the kit's accent
    /// ramp is built from.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double Hue, double Chroma)> Accents =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["red"] = (25, 0.17),
            ["orange"] = (48, 0.16),
            ["amber"] = (78, 0.15),
            ["green"] = (150, 0.14),
            ["teal"] = (188, 0.12),
            ["blue"] = (245, 0.15),
            ["indigo"] = (275, 0.16),
            ["violet"] = (302, 0.18),
            ["pink"] = (352, 0.16),
            // A near-grey accent, for interfaces that should not have a colour at all.
            ["slate"] = (265, 0.035),
        };

    /// <summary>The project's choice, falling back to the defaults for anything never chosen.</summary>
    public static ThemeChoice From(ProjectMetaRepository meta) => new(
        meta.Get(ThemeKey) is { Length: > 0 } theme ? theme : DefaultTheme,
        meta.Get(ModeKey) is { Length: > 0 } mode ? mode : DefaultMode,
        meta.Get(AccentKey) is { Length: > 0 } accent ? accent : null,
        meta.Get(DensityKey) is { Length: > 0 } density ? density : DefaultDensity,
        meta.Get(RadiusKey) is { Length: > 0 } radius ? radius : DefaultRadius);

    /// <summary>Writes this choice to project_meta, one row per field.</summary>
    public void Save(ProjectMetaRepository meta)
    {
        meta.Set(ThemeKey, Theme);
        meta.Set(ModeKey, Mode);
        meta.Set(AccentKey, Accent ?? "");
        meta.Set(DensityKey, Density);
        meta.Set(RadiusKey, Radius);
    }

    /// <summary>
    /// Why this choice cannot be used, naming the values that would have been accepted, or null
    /// when every field is valid. Theme ids are passed in, since they come from the kit on disk.
    /// </summary>
    public string? Invalid(IReadOnlyList<string> availableThemes)
    {
        if (!availableThemes.Contains(Theme, StringComparer.Ordinal))
            return $"there is no theme called '{Theme}'. Available: {string.Join(", ", availableThemes)}.";

        if (!Modes.Contains(Mode, StringComparer.Ordinal))
            return $"'{Mode}' is not a mode. Available: {string.Join(", ", Modes)}.";

        if (Accent is { Length: > 0 } accent && !Accents.ContainsKey(accent))
            return $"there is no accent called '{accent}'. Available: {string.Join(", ", Accents.Keys)}.";

        if (!Densities.ContainsKey(Density))
            return $"'{Density}' is not a density. Available: {string.Join(", ", Densities.Keys)}.";

        if (!Radii.ContainsKey(Radius))
            return $"'{Radius}' is not a radius. Available: {string.Join(", ", Radii.Keys)}.";

        return null;
    }

    /// <summary>
    /// The four knobs as the custom properties the kit reads, as one `:root` block. Appended to
    /// theme.css after the theme and the mode, so it overrides both.
    /// </summary>
    public string Knobs()
    {
        var lines = new List<string>
        {
            $"  --fg-density-scale: {Densities[Density].ToString(System.Globalization.CultureInfo.InvariantCulture)};",
            $"  --fg-radius-mult: {Radii[Radius].ToString(System.Globalization.CultureInfo.InvariantCulture)};",
        };

        if (Accent is { Length: > 0 } name && Accents.TryGetValue(name, out var colour))
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            lines.Add($"  --fg-accent-h: {colour.Hue.ToString(culture)};");
            lines.Add($"  --fg-accent-c: {colour.Chroma.ToString(culture)};");
        }

        return $"/* Knobs: {Describe()} */\n:root {{\n{string.Join("\n", lines)}\n}}\n";
    }

    /// <summary>This choice in one line, for a log line or a tool observation.</summary>
    public string Describe() =>
        $"theme {Theme}, {Mode} mode, {Accent ?? "theme"} accent, {Density} density, {Radius} corners";
}
