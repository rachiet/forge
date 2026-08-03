using System.Xml.Linq;

namespace Forge.Core.Board;

/// <summary>How the client runs the finished project: a directory and a command.</summary>
public sealed record Delivery(string Directory, string Command, string? Url = null);

/// <summary>
/// Works out the command that starts a checked-out project by reading its project
/// files.
/// </summary>
/// <remarks>
/// Derived from the repo rather than asked of an agent, so it cannot describe a way
/// of running the project that does not exist. QA may override the result with
/// <c>how_to_run</c> when it has actually launched the app.
/// </remarks>
public static class DeliveryPlan
{
    /// <summary>
    /// The runnable project's command, or null when the checkout holds nothing that runs
    /// (a library, or a docs-only repo).
    /// </summary>
    public static Delivery? For(string checkoutDir)
    {
        if (!Directory.Exists(checkoutDir)) return null;

        var startup = Directory.EnumerateFiles(checkoutDir, "*.csproj", SearchOption.AllDirectories)
            .Where(IsRunnable)
            .OrderBy(p => p.Contains($"{Path.DirectorySeparatorChar}test", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(p => p.Length)
            .FirstOrDefault();
        if (startup is null) return null;

        var relative = Path.GetRelativePath(checkoutDir, startup).Replace('\\', '/');
        return new Delivery(checkoutDir, $"dotnet run --project {relative}");
    }

    /// <summary>Whether a .csproj produces something that can be started on its own.</summary>
    private static bool IsRunnable(string csproj)
    {
        XDocument doc;
        try { doc = XDocument.Load(csproj); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return false; }

        // A web or WebAssembly SDK is always startable. Under the plain SDK it takes
        // OutputType Exe, which is what separates an app from a class library.
        var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "";
        if (sdk.Contains("Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
            sdk.Contains("Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
            return true;

        var outputType = doc.Descendants("OutputType").FirstOrDefault()?.Value;
        if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)) return false;

        // A test project can declare Exe too, but running it runs the tests rather than
        // the product. Microsoft.NET.Test.Sdk is the reference every test project carries.
        return !doc.Descendants("PackageReference")
            .Select(p => p.Attribute("Include")?.Value ?? "")
            .Any(id => id.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
    }
}
