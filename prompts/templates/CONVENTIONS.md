# Development Conventions

These are Forge's house rules and are the same on every project. They are here
before any code is written; nobody needs to re-derive them. The Principal appends
a "Project-specific" section below for what this build genuinely requires — the
module names, the storage choice, domain rules — and reviewers append rules they
find are worth making permanent. Nothing above that section is theirs to rewrite.

## Stack — fixed

- C# / .NET 8. xUnit for tests.
- `src/<Project>.<Module>/` for code, `tests/<Project>.Tests/` for tests.
- The build is `dotnet build`; the tests are `dotnet test`. Both run from the repo
  root and must work with no arguments, no environment variables and no setup step.

## Naming and style

- `PascalCase` for types, methods, properties and any public member; `camelCase` for
  parameters and locals; `_camelCase` for private fields.
- Asynchronous methods end in `Async` and take a `CancellationToken` where the caller
  could reasonably cancel them.
- Request and response payloads are `record` types or plain DTO classes, never
  anonymous objects or dictionaries — the contract has to be visible in the type.

## HTTP responses

- Errors return `ProblemDetails` (RFC 7807), which ASP.NET Core already produces for
  model-validation failures: `status`, `title`, and `detail` saying what was wrong in
  a sentence, plus `errors` per field where the framework supplies it. It is the
  standard shape, so QA can assert on named fields rather than parse prose.
- `detail` describes the client's mistake, never the server's internals. No exception
  messages, no stack traces, no type names — it is read by whoever called the API.
- `400` for bad input, `404` for something that does not exist, `500` for a fault.
  Missing, empty and whitespace-only values are all bad input: `400`, not `500`.
- Success-response field names match `docs/design/03-contracts/` exactly. When the C#
  member name differs, use `[JsonPropertyName]` — renaming the contract is not the fix.

## Tests

- Named `UnitOfWork_StateUnderTest_ExpectedBehavior`, so a failure in CI output names
  the defect without anyone opening the file — `GetLink_WithUnknownCode_Returns404`.
- A production project never references a test-only package. If a test needs a
  different backing store, select it at runtime (`Database.IsRelational()` and
  similar), do not add the dependency to the shipping project.
- `tests/acceptance/` belongs to QA. It is not in the solution file and must not be
  added to it — it runs only against a started application, and the build would fail
  without one. Engineers do not edit it; a failing acceptance test is fixed in the
  code it tests.

## Stay inside the task

- Add code where it already lives. Do not move files between folders, rename
  namespaces, or introduce a new layout as part of a task that asks for a feature.
  Tasks have been lost this way: half the codebase references the old names and half
  the new, and the resulting build errors read as missing types and cost a whole
  budget to chase. A restructure is its own task.
- Files under `docs/design/03-contracts/` are read-only to engineers. The
  implementation conforms to the contract; the contract does not move to fit it.

## Definition of done

1. `dotnet build` succeeds with no warnings and no errors.
2. `dotnet test` passes.
3. The `MODULE.md` for what you touched describes the code as it now is, in fact
   rather than intent.
4. The observable behaviour matches `docs/design/03-contracts/`.
