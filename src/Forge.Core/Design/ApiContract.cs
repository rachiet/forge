using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Forge.Core.Design;

/// <summary>One operation in the contract.</summary>
/// <param name="OperationId">Its stable id, named by tasks and by acceptance tests.</param>
/// <param name="Method">The HTTP method, upper-case.</param>
/// <param name="Path">The URL template, e.g. `/api/polls/{id}`.</param>
/// <param name="Requirement">The requirement file it serves, from `x-requirement`.</param>
public sealed record ContractOperation(string OperationId, string Method, string Path, string Requirement)
{
    /// <summary>`POST /api/polls` — the operation as a human reads it.</summary>
    public string Signature => $"{Method} {Path}";
}

/// <summary>
/// The project's observable HTTP contract, parsed from the OpenAPI document at
/// <see cref="Path"/>. Everything downstream refers to operations by
/// <see cref="ContractOperation.OperationId"/>: a task names the operations it implements, an
/// acceptance test names the operation it exercises, and the coverage gates compare those sets.
/// </summary>
public sealed class ApiContract
{
    /// <summary>Repo-relative path of the contract. One document per project.</summary>
    public const string Path = "docs/design/contracts/openapi.yaml";

    /// <summary>The extension linking an operation to the requirement file it serves.</summary>
    public const string RequirementExtension = "x-requirement";

    /// <summary>
    /// The document-level extension listing requirement files that have no HTTP surface at all
    /// — a user interface, a performance target, a refactoring — so the coverage gate does not
    /// read them as operations the Principal forgot to design.
    /// </summary>
    public const string NonHttpExtension = "x-non-http-requirements";

    private readonly OpenApiDocument _document;

    /// <summary>Private: an instance exists only if <see cref="Validate"/> accepted the document.</summary>
    private ApiContract(OpenApiDocument document, IReadOnlyList<ContractOperation> operations)
    {
        _document = document;
        Operations = operations;
    }

    /// <summary>Every operation the document declares, in document order.</summary>
    public IReadOnlyList<ContractOperation> Operations { get; }

    /// <summary>Just their ids.</summary>
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
    /// Parses a contract and checks its structure: valid OpenAPI, and every operation carrying
    /// an operationId, at least one 4xx or 5xx response, and an `x-requirement`. Returns the
    /// contract, or the errors naming each operation and what it is missing.
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
    /// Renders the named operations as a standalone OpenAPI document, for a task packet.
    /// The whole components section is carried across, so every `$ref` in the slice resolves.
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

    /// <summary>
    /// Requirement files the contract declares as having no endpoint. They are exempt from the
    /// requirement-to-operation gate, and QA reports them as unverified rather than passing
    /// over them silently.
    /// </summary>
    public IReadOnlyCollection<string> NonHttpRequirements =>
        _document.Extensions.TryGetValue(NonHttpExtension, out var value) && value is OpenApiArray listed
            ? listed.OfType<OpenApiString>()
                .Select(entry => entry.Value?.Trim() ?? "")
                .Where(entry => entry.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>Of the ids given, those the contract does not define.</summary>
    public IReadOnlyList<string> Unknown(IEnumerable<string> operationIds)
    {
        var known = OperationIds.ToHashSet(StringComparer.Ordinal);
        return [.. operationIds.Where(id => !known.Contains(id))];
    }

    /// <summary>Whether a response key is an error status: 4xx, 5xx, or `default`.</summary>
    private static bool IsFailure(string status) =>
        status.StartsWith('4') || status.StartsWith('5') || status.Equals("default", StringComparison.Ordinal);

    /// <summary>The operation's `x-requirement` value, or null when it carries none.</summary>
    private static string? Requirement(OpenApiOperation operation) =>
        operation.Extensions.TryGetValue(RequirementExtension, out var value) && value is OpenApiString text
            ? text.Value?.Trim()
            : null;
}
