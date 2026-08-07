namespace Forge.Core.Agents;

/// <summary>
/// A role's file-access scope inside its workspace, enforced by the toolset on every path.
///
/// This is how "the PM never sees code" becomes true rather than aspirational:
/// the PM's recipe scopes it to PROJECT.md, STATUS.md and docs/, so a request to
/// read src/ is refused by the toolset, not by the model's good manners.
/// </summary>
public sealed record PathScope(IReadOnlyList<string> Prefixes)
{
    /// <summary>Unrestricted within the jail — what an engineer gets.</summary>
    public static readonly PathScope Workspace = new([]);

    public bool IsUnrestricted => Prefixes.Count == 0;

    /// <summary>
    /// Prefix match on the workspace-relative path. Directory prefixes end in '/'
    /// so that "docs/" admits docs/requirements/INDEX.md but not docs-archive/x.md.
    /// </summary>
    public bool Allows(string relativePath)
    {
        if (IsUnrestricted) return true;

        var path = relativePath.Replace('\\', '/').TrimStart('.', '/');
        if (path.Length == 0) return true; // the workspace root itself is always listable

        return Prefixes.Any(prefix => prefix.EndsWith('/')
            ? path.StartsWith(prefix, StringComparison.Ordinal)
            : path.Equals(prefix, StringComparison.Ordinal));
    }

    public string Describe() =>
        IsUnrestricted ? "the whole workspace" : string.Join(", ", Prefixes);
}
