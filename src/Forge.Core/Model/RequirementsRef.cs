namespace Forge.Core.Model;

/// <summary>
/// Pointer into docs/requirements, e.g. "02-todos-read.md".
/// Parse-don't-validate at every boundary: a directory prefix and a trailing "@v3" version
/// suffix are normalised away, so a RequirementsRef in hand is always a bare requirement file
/// name — the form the coverage gates compare against the files on disk.
/// </summary>
public readonly record struct RequirementsRef(string File)
{
    public static RequirementsRef Parse(string text)
    {
        var trimmed = text?.Trim() ?? "";
        // Refs carry no version, but stored rows and models still write "01-todos.md@v1";
        // dropping the suffix is what stops that spelling from failing a coverage gate.
        var at = trimmed.IndexOf('@');
        var file = System.IO.Path.GetFileName(at >= 0 ? trimmed[..at] : trimmed);

        if (file.Length == 0)
            throw new FormatException(
                $"Malformed requirements ref '{text}': expected a requirement file name "
                + "like '02-todos-read.md'.");

        return new RequirementsRef(file);
    }

    public override string ToString() => File;
}
