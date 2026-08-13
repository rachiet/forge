using System.Data;
using Forge.Core.Db;
using Forge.Core.Workspaces;

namespace Forge.Core.Board;

/// <summary>
/// The commit whose requirements the client last received: everything up to it is the product
/// they have, everything after it is the change they are being asked about. Recorded at
/// handover, and read by the page and the change-request brief to show the delta instead of the
/// whole spec.
/// </summary>
public static class SpecBaseline
{
    /// <summary>Where the sha is kept; a project that has never been delivered has none.</summary>
    public const string Key = "spec_baseline_sha";

    /// <summary>The recorded sha, or null before the first handover.</summary>
    public static string? Get(IDbConnection conn) =>
        new ProjectMetaRepository(conn).Get(Key) is { Length: > 0 } sha ? sha : null;

    /// <summary>
    /// Records trunk's current head as the delivered spec. Called after a handover, so the next
    /// change request diffs against exactly what the client was handed.
    /// </summary>
    public static void Record(ForgePaths paths, string project, IDbConnection conn)
    {
        var head = Git.Run(paths.ProjectBareRepo(project), "rev-parse", WorkspaceManager.TrunkBranch);
        if (head.ExitCode == 0 && head.Stdout.Trim() is { Length: > 0 } sha)
            new ProjectMetaRepository(conn).Set(Key, sha);
    }
}
