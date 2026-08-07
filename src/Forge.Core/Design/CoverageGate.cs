using System.Data;
using Forge.Core.Db;

namespace Forge.Core.Design;

/// <summary>One requirement file and whether a task claims to implement it.</summary>
public sealed record RequirementCoverage(string File, bool Covered, IReadOnlyList<long> TaskIds);

public sealed record CoverageReport(IReadOnlyList<RequirementCoverage> Requirements)
{
    public IReadOnlyList<string> Uncovered =>
        Requirements.Where(r => !r.Covered).Select(r => r.File).ToList();

    public bool Complete => Uncovered.Count == 0;
}

/// <summary>
/// The contract's two links: requirements it claims to cover, and operations a task has
/// been created for. Both empty means the plan is fully joined up.
/// </summary>
/// <param name="RequirementsWithNoOperation">
/// Requirement files no operation names in its <c>x-requirement</c>. QA tests the contract,
/// so a requirement absent from it has no observable channel and can never be verified.
/// </param>
/// <param name="OperationsWithNoTask">
/// Operations no task claims in <c>contract_ops</c> — an endpoint nobody was asked to build.
/// </param>
public sealed record ContractReport(
    IReadOnlyList<string> RequirementsWithNoOperation,
    IReadOnlyList<string> OperationsWithNoTask)
{
    public bool Complete => RequirementsWithNoOperation.Count == 0 && OperationsWithNoTask.Count == 0;

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
/// The PM coverage gate (spec §7): every requirement section must map to a task.
/// A mechanical check, not an LLM judgement — it compares the requirement files on
/// disk against the requirements_ref each task carries, so "did the Principal
/// leave a requirement unbuilt?" is answered from ground truth, not a claim.
/// </summary>
public static class CoverageGate
{
    public static CoverageReport Check(IDbConnection conn, string workspaceRoot)
    {
        var requirementFiles = RequirementFiles(workspaceRoot);

        // Which tasks name each requirement file (the version is ignored for coverage —
        // a task against v2 still covers the requirement).
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
    /// Checks the contract's links in both directions. A project with no contract has no
    /// HTTP surface to check, and reports complete.
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
