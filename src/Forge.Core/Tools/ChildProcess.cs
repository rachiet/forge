using System.Diagnostics;

namespace Forge.Core.Tools;

/// <summary>
/// Builds the ProcessStartInfo for every process Forge starts — the agent's jailed commands and
/// the harness's own dotnet, git and application runs alike. It is the only place a child's
/// environment is decided: the inherited one is thrown away and rebuilt from an allowlist, so
/// Forge's provider keys are never handed to code an agent wrote. Construct a ProcessStartInfo
/// anywhere else and that guarantee is gone, which is why every start path goes through here.
///
/// Raises the bar; it is not a sandbox. The child still runs as the same user and can read any
/// path it names outright.
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// A started process's environment, rebuilt from these names when they are present in
    /// Forge's own. Everything absent from the list — the provider keys above all — is dropped.
    /// </summary>
    private static readonly string[] PassThroughNames =
    [
        "PATH",       // where the child finds dotnet, git and node
        "TMPDIR",     // scratch space on Unix
        "TEMP",       // scratch space on Windows
        "TMP",        // scratch space on Windows, older tools
        "LANG",       // text encoding, so output is not mangled
        "LC_ALL",     // text encoding, overriding LANG
        "TERM",       // terminal type, so tools do not assume a dumb one
        "SystemRoot", // Windows system directory, which sockets need
        "ComSpec",    // Windows command interpreter, which some toolchains invoke
    ];

    /// <summary>
    /// A ProcessStartInfo with both streams redirected, no shell, and a scrubbed environment.
    /// </summary>
    /// <param name="fileName">The binary to run; resolved on the allowlisted PATH.</param>
    /// <param name="workingDir">The directory to run it in.</param>
    /// <param name="agentHome">
    /// HOME for the child, created if it does not exist. Null takes the harness's shared home
    /// under the data root, which is what a caller with no project in scope — git — gets. Pass
    /// the project's agent home where it is known, so the toolchain caches written there are
    /// the same ones the agent's own builds populate.
    /// </param>
    /// <param name="extra">
    /// Variables to set on top of the allowlist, applied last so they win. For what a specific
    /// child needs and the allowlist has no business carrying: a base URL, a browser path.
    /// </param>
    public static ProcessStartInfo Create(
        string fileName,
        string workingDir,
        string? agentHome = null,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        Scrub(psi, agentHome, extra);
        return psi;
    }

    /// <summary>The harness's own HOME, for a child started with no project in scope.</summary>
    public static string HarnessHome => Path.Combine(ForgePaths.Resolve().DataRoot, "harness-home");

    /// <summary>
    /// Replaces the child's environment with one built from the allowlist instead of inherited,
    /// and points HOME at the given home so the child does not read the user's dotfiles.
    /// </summary>
    private static void Scrub(
        ProcessStartInfo psi, string? agentHome, IReadOnlyDictionary<string, string>? extra)
    {
        // psi.Environment starts as a copy of Forge's own, so the names to keep are read out
        // of it before it is emptied.
        var inherited = psi.Environment;
        var passThrough = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassThroughNames)
            if (inherited.TryGetValue(name, out var value) && value is not null)
                passThrough[name] = value;

        // DOTNET_/NUGET_ pass through so toolchain caches are shared across tasks.
        foreach (var (name, value) in inherited)
            if (value is not null && (name.StartsWith("DOTNET_", StringComparison.Ordinal) ||
                                      name.StartsWith("NUGET_", StringComparison.Ordinal)))
                passThrough[name] = value;

        psi.Environment.Clear();
        foreach (var (name, value) in passThrough) psi.Environment[name] = value;
        if (extra is not null)
            foreach (var (name, value) in extra) psi.Environment[name] = value;

        // HOME is set rather than left out: unset is not denied — .NET and several toolchains
        // fall back to the passwd entry and find the user's real home anyway.
        var home = agentHome is { Length: > 0 } ? agentHome : HarnessHome;
        Directory.CreateDirectory(home);
        psi.Environment["HOME"] = home;
        psi.Environment["USERPROFILE"] = home;
    }
}
