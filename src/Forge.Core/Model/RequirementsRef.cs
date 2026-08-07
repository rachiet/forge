namespace Forge.Core.Model;

/// <summary>
/// Version-stamped pointer into docs/requirements, e.g. "02-todos-read.md@v3".
/// Parse-don't-validate at the DB boundary: malformed text throws, so a
/// RequirementsRef in hand is always well-formed.
/// </summary>
public readonly record struct RequirementsRef(string File, int Version)
{
    public static RequirementsRef Parse(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var at = text.LastIndexOf('@');
            if (at > 0 && at < text.Length - 1)
            {
                var file = text[..at];
                var version = text[(at + 1)..];
                // A dotted version parses to its major: "v1.0" and "v1" are the same
                // requirement. Nothing compares minors — coverage matches on the file alone —
                // and a PM that stamps its documents "v1.0" was otherwise refusing every task
                // the Principal built from them, which cost two whole design runs to diagnose.
                if (version.Length > 1 && version[0] == 'v' &&
                    Major(version[1..]) is { } n && n > 0)
                {
                    return new RequirementsRef(file, n);
                }
            }
        }
        throw new FormatException(
            $"Malformed requirements ref '{text}': expected '<file>@v<version>' like '02-todos-read.md@v3'.");
    }

    /// <summary>
    /// The major number of "3" or "1.0" or "2.1.4", or null when the text is not a version
    /// at all. Every part must be numeric, so "1.0-draft" is still rejected — this widens
    /// what counts as a version, it does not stop checking.
    /// </summary>
    private static int? Major(string version)
    {
        var parts = version.Split('.');
        if (parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit))) return null;
        return int.TryParse(parts[0], out var major) ? major : null;
    }

    public override string ToString() => $"{File}@v{Version}";
}
