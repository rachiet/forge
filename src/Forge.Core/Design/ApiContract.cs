using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Forge.Core.Design;

/// <summary>One operation in the contract: an endpoint, its id, and the requirement it serves.</summary>
public sealed record ContractOperation(string OperationId, string Method, string Path, string Requirement)
{
    public string Signature => $"{Method} {Path}";
}

/// <summary>
/// The project's observable HTTP contract, read from the OpenAPI document the Principal
/// writes. Everything downstream keys off <see cref="ContractOperation.OperationId"/>: a
/// task names the operations it implements, an acceptance test names the operation it
/// exercises, and the coverage gates are set comparisons between those names.
/// </summary>
/// <remarks>
/// Parsed, never pattern-matched. A contract the harness cannot read is one it cannot
/// gate against, so <see cref="Validate"/> refuses a document at the moment it is written
/// rather than letting a malformed one reach the engineers who build from it.
/// </remarks>
public sealed class ApiContract
{
    /// <summary>Repo-relative path of the contract. One document per project.</summary>
    public const string Path = "docs/design/contracts/openapi.yaml";

    /// <summary>The extension linking an operation to the requirement file it serves.</summary>
    public const string RequirementExtension = "x-requirement";

    private readonly OpenApiDocument _document;

    private ApiContract(OpenApiDocument document, IReadOnlyList<ContractOperation> operations)
    {
        _document = document;
        Operations = operations;
    }

    public IReadOnlyList<ContractOperation> Operations { get; }

    public IEnumerable<string> OperationIds => Operations.Select(o => o.OperationId);

    /// <summary>The contract on trunk, or null when the project has no HTTP surface.</summary>
    public static ApiContract? Load(string checkoutDir)
    {
        var file = System.IO.Path.Combine(checkoutDir, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(file) && Validate(File.ReadAllText(file)) is { Contract: { } contract }
            ? contract
            : null;
    }

    /// <summary>
    /// Parses and structurally checks a contract. Errors are written for the model that
    /// produced the document — they name the operation and what is missing from it.
    /// </summary>
    public static (ApiContract? Contract, IReadOnlyList<string> Errors) Validate(string yaml)
    {
        OpenApiDocument document;
        OpenApiDiagnostic diagnostic;
        try
        {
            document = new OpenApiStringReader().Read(yaml, out diagnostic);
        }
        catch (Exception ex)
        {
            return (null, [$"the document is not valid OpenAPI: {ex.Message}"]);
        }

        if (diagnostic.Errors.Count > 0)
            return (null, [.. diagnostic.Errors.Select(e => $"{e.Pointer}: {e.Message}")]);

        var errors = new List<string>();
        var operations = new List<ContractOperation>();

        foreach (var (path, item) in document.Paths ?? [])
            foreach (var (method, operation) in item.Operations)
            {
                var where = $"{method.ToString().ToUpperInvariant()} {path}";

                if (string.IsNullOrWhiteSpace(operation.OperationId))
                {
                    // Without an id the operation cannot be named by a task or a test, so it
                    // would sit outside every gate — the one defect worth refusing outright.
                    errors.Add($"{where}: no operationId. Every operation needs a stable id "
                             + "(kebab-case, e.g. shorten-create) — tasks and tests refer to it by that name.");
                    continue;
                }

                if (operation.Responses is null || operation.Responses.Count == 0)
                    errors.Add($"{where}: no responses documented. State the success status and "
                             + "every error status the client can receive.");
                else if (!operation.Responses.Keys.Any(IsFailure))
                    errors.Add($"{where}: only success responses. Document what the client gets "
                             + "when the input is bad or the thing does not exist (4xx).");

                if (Requirement(operation) is not { Length: > 0 } requirement)
                {
                    errors.Add($"{where}: no {RequirementExtension}. Name the requirement file this "
                             + $"operation serves, e.g. {RequirementExtension}: 01-url-shortening.md");
                    continue;
                }

                operations.Add(new ContractOperation(
                    operation.OperationId, method.ToString().ToUpperInvariant(), path, requirement));
            }

        if (operations.Count == 0 && errors.Count == 0)
            errors.Add("the document declares no operations.");

        var duplicates = operations
            .GroupBy(o => o.OperationId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicates)
            errors.Add($"operationId '{duplicate}' is used more than once; ids must be unique.");

        return errors.Count > 0 ? (null, errors) : (new ApiContract(document, operations), []);
    }

    /// <summary>
    /// The named operations rendered as a standalone OpenAPI document, for a task packet.
    /// Components are carried whole rather than resolved: a schema the engineer needs must
    /// be present, and an unresolved `$ref` in a packet is worse than a few extra lines.
    /// </summary>
    public string Slice(IEnumerable<string> operationIds)
    {
        var wanted = operationIds.ToHashSet(StringComparer.Ordinal);
        var sliced = new OpenApiDocument
        {
            Info = _document.Info,
            Servers = _document.Servers,
            Components = _document.Components,
            Paths = [],
        };

        foreach (var (path, item) in _document.Paths ?? [])
        {
            var kept = item.Operations
                .Where(pair => wanted.Contains(pair.Value.OperationId ?? ""))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (kept.Count == 0) continue;

            sliced.Paths[path] = new OpenApiPathItem
            {
                Parameters = item.Parameters,
                Operations = kept,
            };
        }

        return sliced.SerializeAsYaml(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);
    }

    /// <summary>Which requirement files the contract claims to cover.</summary>
    public IReadOnlyCollection<string> CoveredRequirements =>
        Operations.Select(o => o.Requirement).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids named here that the contract does not define — a typo or an invention.</summary>
    public IReadOnlyList<string> Unknown(IEnumerable<string> operationIds)
    {
        var known = OperationIds.ToHashSet(StringComparer.Ordinal);
        return [.. operationIds.Where(id => !known.Contains(id))];
    }

    private static bool IsFailure(string status) =>
        status.StartsWith('4') || status.StartsWith('5') || status.Equals("default", StringComparison.Ordinal);

    private static string? Requirement(OpenApiOperation operation) =>
        operation.Extensions.TryGetValue(RequirementExtension, out var value) && value is OpenApiString text
            ? text.Value?.Trim()
            : null;
}
