using System.Data;
using Forge.Core.Db;

namespace Forge.Core.Design;

/// <summary>One requirement file and whether a task claims to implement it.</summary>
/// <param name="File">The requirement's file name, e.g. `01-polls.md`.</param>
/// <param name="Covered">Whether any task names it.</param>
/// <param name="TaskIds">The tasks that name it, empty when none do.</param>
public sealed record RequirementCoverage(string File, bool Covered, IReadOnlyList<long> TaskIds);

/// <summary>Every requirement file and the tasks covering it.</summary>
public sealed record CoverageReport(IReadOnlyList<RequirementCoverage> Requirements)
{
    /// <summary>Requirement files no task names.</summary>
    public IReadOnlyList<string> Uncovered =>
        Requirements.Where(r => !r.Covered).Select(r => r.File).ToList();

    /// <summary>Whether every requirement has at least one task.</summary>
    public bool Complete => Uncovered.Count == 0;
}

/// <summary>Gaps in the contract's links, in both directions. Both empty means the plan is joined up.</summary>
/// <param name="RequirementsWithNoOperation">
/// Requirement files no operation names in its <c>x-requirement</c>.
/// </param>
/// <param name="OperationsWithNoTask">
/// Operations no task claims in its <c>contract_ops</c>.
/// </param>
public sealed record ContractReport(
    IReadOnlyList<string> RequirementsWithNoOperation,
    IReadOnlyList<string> OperationsWithNoTask)
{
    /// <summary>Whether both directions are fully linked.</summary>
    public bool Complete => RequirementsWithNoOperation.Count == 0 && OperationsWithNoTask.Count == 0;

    /// <summary>The gaps as one line, for a log or an escalation.</summary>
    public string Describe() => string.Join("; ", new[]
    {
        RequirementsWithNoOperation.Count > 0
            ? $"requirement(s) with no contract operation: {string.Join(", ", RequirementsWithNoOperation)}"
            : null,
        OperationsWithNoTask.Count > 0
            ? $"contract operation(s) with no task: {string.Join(", ", OperationsWithNoTask)}"
            : null,
    }.Where(part => part is not null));
}

/// <summary>
/// Checks that the design's artifacts are linked to each other, by comparing sets rather than
/// asking a model: requirement files on disk against the requirements_ref and contract_ops
/// columns, and against the contract's operations.
/// </summary>
public static class CoverageGate
{
    /// <summary>Reports which requirement files are named by at least one task.</summary>
    public static CoverageReport Check(IDbConnection conn, string workspaceRoot)
    {
        var requirementFiles = RequirementFiles(workspaceRoot);

        // Keyed by file; the version is ignored, so a task against v2 still covers the file.
        var byFile = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var task in new TaskRepository(conn).List())
            if (task.RequirementsRef is { } req)
                (byFile.TryGetValue(req.File, out var list) ? list : byFile[req.File] = []).Add(task.Id);

        var coverage = requirementFiles
            .Select(file => new RequirementCoverage(
                file,
                byFile.ContainsKey(file),
                byFile.TryGetValue(file, out var ids) ? ids : []))
            .ToList();

        return new CoverageReport(coverage);
    }

    /// <summary>
    /// Reports requirements no operation serves, and operations no task claims. A project with
    /// no contract has no HTTP surface and reports complete.
    /// </summary>
    public static ContractReport CheckContract(IDbConnection conn, string workspaceRoot)
    {
        if (ApiContract.Load(workspaceRoot) is not { } contract) return new ContractReport([], []);

        var covered = contract.CoveredRequirements;
        var uncovered = RequirementFiles(workspaceRoot)
            .Where(file => !covered.Contains(file))
            .ToList();

        var claimed = new TaskRepository(conn).List()
            .SelectMany(task => task.ContractOps)
            .ToHashSet(StringComparer.Ordinal);
        var unbuilt = contract.OperationIds.Where(id => !claimed.Contains(id)).ToList();

        return new ContractReport(uncovered, unbuilt);
    }

    /// <summary>The requirement file names in docs/requirements/, excluding INDEX.md.</summary>
    private static List<string> RequirementFiles(string workspaceRoot)
    {
        var dir = Path.Combine(workspaceRoot, "docs", "requirements");
        return Directory.Exists(dir)
            ? [.. Directory.EnumerateFiles(dir, "*.md")
                .Select(Path.GetFileName)
                .Where(name => name is not null &&
                    !name.Equals("INDEX.md", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)]
            : [];
    }
}
