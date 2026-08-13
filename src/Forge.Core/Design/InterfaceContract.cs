using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;

namespace Forge.Core.Design;

/// <summary>One element of the interface, and the handle it is addressed by.</summary>
/// <param name="TestId">Its `data-testid`, the only way a test is allowed to find it.</param>
/// <param name="Is">What it is, in a phrase — read by the engineer building it and QA testing it.</param>
/// <param name="OnDemand">
/// Whether it starts hidden and appears when the client asks for it. Declared because the
/// difference between "hidden until clicked" and "missing" is the difference between correct
/// and broken, and nothing else can tell them apart.
/// </param>
/// <param name="Repeats">Whether the page shows one per item rather than exactly one.</param>
public sealed record InterfaceElement(string TestId, string Is, bool OnDemand, bool Repeats);

/// <summary>One page of the interface, and the requirement it serves.</summary>
/// <param name="Path">The path it is served at, e.g. `/`.</param>
/// <param name="Requirement">The requirement file this page implements.</param>
/// <param name="Elements">Every element a requirement talks about.</param>
public sealed record InterfacePage(
    string Path, string Requirement, IReadOnlyList<InterfaceElement> Elements);

/// <summary>
/// The interface's observable surface, declared in the contract document's `x-interface`
/// extension: which pages exist and which elements a test may address on them. The counterpart
/// of the OpenAPI operations for everything that has no HTTP surface — a page is verified by
/// rendering it, and this is the vocabulary that verification is written in.
///
/// Lives in the same document as the operations, is validated when that document is written,
/// and is compared against the rendered page by CI: a handle declared here and absent from the
/// DOM fails the task that owed it, so QA never has to guess a selector.
/// </summary>
public sealed record InterfaceContract(IReadOnlyList<InterfacePage> Pages)
{
    /// <summary>The document-level extension carrying it.</summary>
    public const string Extension = "x-interface";

    /// <summary>Element keys the schema defines; anything else is a typo or an invention.</summary>
    private static readonly string[] ElementKeys = ["testid", "is", "visible", "repeats"];

    /// <summary>Page keys the schema defines.</summary>
    private static readonly string[] PageKeys = ["path", "requirement", "elements"];

    /// <summary>The values `visible` accepts. Absent means the element is on the page from the start.</summary>
    private static readonly string[] VisibleValues = ["always", "on-demand"];

    /// <summary>Every handle declared, across every page.</summary>
    public IReadOnlyCollection<string> TestIds =>
        Pages.SelectMany(p => p.Elements).Select(e => e.TestId).ToHashSet(StringComparer.Ordinal);

    /// <summary>The handles expected to be on the page as soon as it loads.</summary>
    public IReadOnlyCollection<string> AlwaysVisibleTestIds =>
        Pages.SelectMany(p => p.Elements).Where(e => !e.OnDemand)
            .Select(e => e.TestId).ToHashSet(StringComparer.Ordinal);

    /// <summary>The requirement files the interface claims to serve.</summary>
    public IReadOnlyCollection<string> CoveredRequirements =>
        Pages.Select(p => p.Requirement).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the extension and checks its shape: a list of pages, each with a path, a
    /// requirement and elements, each element with a kebab-case handle and a phrase saying what
    /// it is. Returns the contract, or the errors — one per problem, naming the offending key
    /// and the values that are allowed, since these are read by the model that must fix them.
    /// A document with no `x-interface` at all is valid: not every project has a page.
    /// </summary>
    public static (InterfaceContract? Contract, IReadOnlyList<string> Errors) Parse(
        IDictionary<string, IOpenApiExtension> extensions)
    {
        if (!extensions.TryGetValue(Extension, out var value)) return (new InterfaceContract([]), []);

        if (value is not OpenApiArray declared)
            return (null, [$"{Extension} must be a list of pages, each with `path`, `requirement` "
                         + "and `elements`."]);

        var errors = new List<string>();
        var pages = new List<InterfacePage>();

        for (var i = 0; i < declared.Count; i++)
        {
            var where = $"{Extension}[{i}]";
            if (declared[i] is not OpenApiObject page)
            {
                errors.Add($"{where}: must be a page with `path`, `requirement` and `elements`.");
                continue;
            }

            foreach (var key in page.Keys.Where(k => !PageKeys.Contains(k, StringComparer.Ordinal)))
                errors.Add($"{where}: unknown key `{key}`. Valid keys: {string.Join(", ", PageKeys)}.");

            if (Text(page, "path") is not { Length: > 0 } path)
            {
                errors.Add($"{where}: `path` is required — the path this page is served at, e.g. `/`.");
                continue;
            }
            if (Text(page, "requirement") is not { Length: > 0 } requirement)
            {
                errors.Add($"{where}: `requirement` is required — the requirement file this page "
                         + "serves, e.g. `01-kanban-board.md`.");
                continue;
            }
            if (!page.TryGetValue("elements", out var raw) || raw is not OpenApiArray rawElements)
            {
                errors.Add($"{where}: `elements` is required and must be a list. Declare every "
                         + "element a requirement talks about, so a test can address it.");
                continue;
            }

            pages.Add(new InterfacePage(
                path,
                Model.RequirementsRef.Parse(requirement).File,
                ReadElements(rawElements, where, errors)));
        }

        var duplicates = pages.SelectMany(p => p.Elements)
            .GroupBy(e => e.TestId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicates)
            errors.Add($"testid '{duplicate}' is declared more than once; a handle names one thing.");

        return errors.Count > 0 ? (null, errors) : (new InterfaceContract(pages), []);
    }

    /// <summary>Reads one page's elements, collecting a message per problem rather than stopping.</summary>
    private static List<InterfaceElement> ReadElements(
        OpenApiArray declared, string where, List<string> errors)
    {
        var elements = new List<InterfaceElement>();
        for (var i = 0; i < declared.Count; i++)
        {
            var at = $"{where}.elements[{i}]";
            if (declared[i] is not OpenApiObject element)
            {
                errors.Add($"{at}: must be an element with `testid` and `is`.");
                continue;
            }

            foreach (var key in element.Keys.Where(k => !ElementKeys.Contains(k, StringComparer.Ordinal)))
                errors.Add($"{at}: unknown key `{key}`. Valid keys: {string.Join(", ", ElementKeys)}.");

            if (Text(element, "testid") is not { Length: > 0 } testId)
            {
                errors.Add($"{at}: `testid` is required — the `data-testid` the element carries.");
                continue;
            }
            if (!KebabCase(testId))
                errors.Add($"{at}: testid '{testId}' must be kebab-case (lowercase letters, digits "
                         + "and dashes), named for the thing rather than where it sits, e.g. `column-todo`.");

            var description = Text(element, "is");
            if (description is not { Length: > 0 })
                errors.Add($"{at}: `is` is required — what this element is, in a phrase.");

            var visible = Text(element, "visible") ?? "always";
            if (!VisibleValues.Contains(visible, StringComparer.Ordinal))
                errors.Add($"{at}: `visible` is '{visible}'. Use: {string.Join(", ", VisibleValues)}.");

            if (element.TryGetValue("repeats", out var repeats) && repeats is not OpenApiBoolean)
                errors.Add($"{at}: `repeats` must be true or false.");

            elements.Add(new InterfaceElement(
                testId,
                description ?? "",
                OnDemand: visible == "on-demand",
                Repeats: element.TryGetValue("repeats", out var r) && r is OpenApiBoolean { Value: true }));
        }
        return elements;
    }

    /// <summary>A string value of a key, or null when it is absent or not a string.</summary>
    private static string? Text(OpenApiObject holder, string key) =>
        holder.TryGetValue(key, out var value) && value is OpenApiString text
            ? text.Value?.Trim()
            : null;

    private static bool KebabCase(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !value.StartsWith('-') && !value.EndsWith('-');
}
