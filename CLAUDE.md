# Forge — contributor guide

Forge is a C#/.NET service that builds software from a client's plain-English
requirements by orchestrating stateless LLM agents (PM, Principal, Engineer, QA).

This file holds the rules a change must respect. It is not a tour of the system —
`README.md` is for users, `ARCHITECTURE.md` describes how the parts fit together.
Read this before editing; everything in it is load-bearing somewhere.

## What Forge is

- **Orchestrator** — the whole service: scheduler, task state machine, roles.
- **Harness** — the deterministic layer wrapped around each LLM call: context
  assembly → call → tool-call parsing → jailed execution → observation loop →
  budget and turn enforcement → ledger and progress note.
- One process, two layers; the orchestrator contains the harness.
- The framing rule everything else follows: **harness code is trusted and
  mechanical; everything a model emits is untrusted output under supervision.**

## Build and run

```sh
dotnet build                  # .NET 8; src/Forge.Core, src/Forge.Cli, tests/Forge.Tests
dotnet test                   # xUnit, ~395 tests, no network or API key needed
```

Commands (`Forge.Cli`, Spectre.Console.Cli):

| Command | Purpose |
|---|---|
| `forge board [--port N]` | The client's web dashboard, all projects, default 5177 |
| `forge project init <project>` | Create the data directory, database and bare repo |
| `forge chat <project> [-m TEXT] [--history]` | Talk to the PM |
| `forge run <project> [--loop] [--task ID] [--project-budget USD]` | Run the build worker |
| `forge task list\|add <project>` | Inspect or hand-add board work |
| `forge log <project> [-e] [-t ID] [-d DOMAIN]` | Replay the event trail and spend |
| `forge prices show\|update` | The model price table |
| `forge secrets set\|list` | The encrypted vault for client project secrets |

Paths, all derived from one root:

- `FORGE_HOME` (default `~/forge-data`) is the **only** path the code hard-knows.
  Under it: `forge.db`, `vault/`, `prices/`, `browsers/`, `harness-home/` (HOME
  for a harness process with no project in scope, git's above all), and
  `projects/<name>/` holding `project.db`, `repo.git` (bare, the source of
  truth), `workspaces/` (harness scratch, deleted after merge), `build/` (the
  client's checkout), `agent-home/`, `forge.log`.
- Client project data never lives inside this repo.
- `~/forge_env` (override `FORGE_ENV`) is the one deliberate second path: Forge's
  **own** provider keys, loaded into the process environment at startup. Keys must
  not ride along in a data root that is meant to be movable.
- `<data root>/llm.json` names the provider and may pin a model per tier.
  Precedence: `FORGE_LLM_PROVIDER` → the project's `project_meta` → `llm.json` →
  the adapter's default.

## Non-negotiables

Break one of these and the system stops being trustworthy, not just incorrect.

- **Budgets are enforced by refusing the next LLM call**, never by asking a model
  to stop. Every call goes through the `MeteredLlmClient` decorator.
- **No model id and no price is ever hardcoded.** A recipe names a `ModelTier`
  (`Coding | Reasoning`); the configured `ILlmClient` resolves it; rates
  come from `PriceCatalog`. An unpriced model refuses to run — costing $0 is a cap
  that never trips.
- **Merge, CI and test state are read from git and process exit codes**, never
  from an agent's account of them.
- **Evidence, not prose.** `file_bug` attaches the harness's own capture of the
  last `run`/`serve`/`http` and refuses when nothing was executed; `how_to_run`
  accepts only a command the instance really ran.
- **Secrets**: agents see `{{secret:NAME}}` only. Substitution happens in the tool
  executor at exec time, values are redacted from captured output, and they never
  reach the database, a prompt or a log. Child processes get an **allowlisted**
  environment (PATH, TMPDIR, LANG, `DOTNET_*`, `NUGET_*`) with `HOME` pointed at
  the agent home — never an inherited one. An agent's `dotnet run` is arbitrary
  code execution, and so is the harness's: **every** process Forge starts is built
  by `Tools/ChildProcess`, the one place a child's environment is decided, because
  CI, QA, the started app and git all re-run the same agent-authored code. A test
  asserts no other file in `Forge.Core` constructs a `ProcessStartInfo`.
- **Status changes go through `TaskTransitions`**, which throws on an illegal
  transition. Never `UPDATE tasks SET status`.
- **Schema changes go through `Db/Migrations.cs`.** `CREATE TABLE IF NOT EXISTS`
  leaves an existing table alone and SQLite cannot ALTER a CHECK, so changing a
  constraint means rebuilding the table.
- **Generated projects have exactly one runnable project**, serving its pages from
  its own `wwwroot/` and its API on the same port. Every feature must be verifiable
  from the CLI or over HTTP; where behaviour would otherwise be invisible, the
  Principal owes it an observable side-channel.

## Invariants

- **Derive, never store twice.** Who acts next is a static map from status
  (`TaskTransitions.RoleFor`); a milestone's state comes from its tasks; a task's
  cost comes from the ledger. There is no "next actor" column and no milestone
  status column — two sources of truth drift.
- **`assigned_role` says what kind of work a task is, not who acts next.** A task
  parked on the client is still an engineering task.
- **A queue filters on status, never on the existence of a message row.** Messages
  notify; statuses route. If a task should leave a queue, give it a status that
  says so.
- **DB column vs JSON**: anything the harness queries or enforces (status, budgets,
  roles, milestone) is a real column with a CHECK; payloads only a model reads may
  be TEXT/JSON (`context_paths`, `contract_ops`). The enums in `Model/Enums.cs`
  mirror those CHECKs and are kept in step by hand.
- **The same rule in memory**: a type when the harness enforces it, a string when
  only a model reads it. Parse-don't-validate at the boundary — `RequirementsRef`,
  `ApiContract`, `InterfaceContract`, `ThemeChoice` — with factory methods that
  enforce invariants and exhaustive switches over closed enums.
- **Prompts are files, never rows.** Three layers: role identity
  (`prompts/roles/<role>.md`), task type (`prompts/tasks/<type>.md`), and the task
  packet rendered from the tasks row. One-off guidance travels as a task-anchored
  message. Fixing a template file improves every future run.
- **One place per concern.** `AgentToolset.Catalogue` is the only description of
  the tool surface — recipes are validated against it, the prompt section and the
  provider's tool schemas are both rendered from it. `EventType` is the only list
  of log categories. Adding a second place to update is the defect.
- **Every handler does its work first and transitions last**, so a crash either
  re-runs an idempotent step or leaves the task claimable. Re-running a merge that
  already landed is a no-op by construction.
- **If it can be decided deterministically, the harness decides it.** Before adding
  an LLM call, ask what the answer depends on: if it can be read from the repo, the
  database, git, a browser or process output, write the code. A model call is for
  judgement only.
- **Refusals are written for the model that will read them**: what was wrong *and*
  the correct form or the available values. A refusal an agent cannot act on wastes
  a whole instance.

## The pipeline

What a change must not break, in the order the worker does it
(`TaskRunner.RunNextByPriorityAsync`):

1. `merging` — harness merge, no agent.
2. `in_review` — a **fresh** Principal instance; the reviewer is never the author.
3. Principal-owned work (`triage`, `stalled`) — decompose a Feature, triage a bug,
   or climb the recovery ladder.
4. One PM turn asking the client about anything parked on them.
5. The next claimable engineer task.
6. Close finished Features, then QA once the board is quiescent.

Rules inside that:

- **CI runs before review**, so the Principal never reads a diff that does not
  build. CI is harness code with zero tokens (`Ci/CiRunner`): static-file parse →
  UI gate → layout → `dotnet build` → `dotnet test` → the rendered-page check.
  Nothing to build is a **skip**, not a failure.
- **Review and merge are queue steps, not inline calls.** A worker killed
  mid-pipeline must leave the task in a status some queue selects.
- **Every recovery loop ends on a counter, and no counter is reset by anything an
  agent does**: `RevisionCap` 5 engineer attempts → the Principal implements the
  task directly; `PrincipalAttemptCap` 3 → the final triage, which has no
  `redirect`; `CrashRetryCap` 2; `SplitDepthCap` 2; `QaRoundCap` 5. Inside one
  instance: 3 empty turns, 5 fully-refused turns, 3 output-truncated turns.
- **A spent cap hands the task to a rung of the ladder, never to the client.** Why
  a task cannot pass CI is a technical question the client cannot answer.
- **A Principal implementation is not reviewed** — green CI goes straight to
  merging. It exists to escape the review loop, so re-entering it would defeat it.
- **`stalled` is the Principal's queue** and is reachable from every live status —
  a task that cannot get there is one the loop claims again with nothing changed.
  **`needs_human` is the client's** and appears in no queue, so the loop drains the
  rest of the board and stops. Blocking that matters is structural: `task_deps`
  keeps dependents unclaimable and a non-quiescent board keeps QA from running.
- **QA is project-level.** It runs only when no task work remains, is seeded with
  the bug ledger, and re-arms on `CountDone()` exceeding `qa_verified_count` — a
  round that accepts nothing new never moves the watermark, which is what makes the
  project terminate. Where the project has an OpenAPI contract, QA **writes the
  acceptance suite and never starts the app**; the harness builds it, starts the
  application, runs it and files a bug from a red run with the output attached. A
  round whose contract and coverage are unchanged re-runs the suite with no model
  at all.
- **Handover happens once per completion** (`project_delivered`), re-armed when a
  new Feature is decomposed, and records `spec_baseline_sha` — everything after it
  is the pending change. A change request is planned and reviewed as a **delta**;
  nobody re-reads the whole specification.

## Budgets

Two units, two jobs, two different failure modes. Do not conflate them.

- **Project budget — USD.** Stored per project in `project_meta`, re-read per call
  so raising it from the board takes effect mid-build. Exhausted: the build
  **pauses**, the task is left exactly as it stands with no strike, one escalation
  reaches the PM, and a QA round that was refused must not move the watermark.
- **Task budget — tokens.** Per *agent instance*, counting `tokens_in + tokens_out`
  only: a cache read is a prompt the provider already holds, so counting it made
  the same budget mean an order of magnitude less on a caching provider. Exhausted:
  that task strikes and climbs the ladder.
- The ledger stores all four token buckets plus `cost_nanos` (USD × 1e-9, integer
  so `SUM()` stays exact) and `priced_with`, which is what makes a row's cost
  recomputable when a rate is corrected.
- Prices are fetched from LiteLLM's table, keyed by provider-native model id, cached
  machine-wide with a 1-day TTL; a model miss forces one refresh before it may fail.

## Boundaries enforced mechanically, not by manners

- `PathScope` per recipe: the PM is scoped to `PROJECT.md`, `STATUS.md` and
  `docs/`, so `read_file src/…` is refused by the harness. `PathJail` resolves
  every path inside the workspace; `ToolAllowlist` and `RefusedCommands` bound what
  `run`/`serve` may start; there is no shell.
- The OpenAPI contract is validated at `write_file`, so a document the harness
  cannot use is refused in the turn that wrote it.
- `UiGate` refuses a page using `fg-` classes the kit does not define, in the same
  turn, and again in CI. Appearance is not generated work: Forge ships the UI kit
  and installs it into every workspace, themes are a closed set chosen with
  `choose_theme` or by the client from the board, and kit files are overwritten
  rather than policed.
- A page is verified by **rendering** it in headless Chromium (`Ui/PageProbe`),
  never by reading markup — markup cannot say whether an element is visible, where
  it sits, or what colour it resolved to. Generic health rules live in
  `Ci/PageHealth`; requirement-specific assertions live in QA's suite. No browser,
  or no interface change in the diff, is a skip.
- One build at a time machine-wide, via a heartbeat lease at
  `<data root>/worker.json`. Acquisition is atomic (`FileMode.CreateNew`) and the
  lease beats itself on a timer, because one task run routinely outlasts the
  timeout.

## House style

Rules that change what gets written. Anything a competent C# author does by
default is left out on purpose.

- **Comments say what the code does, not the history of why.** Every class and
  function carries a short comment: what it is and what it does, enough to review
  the file without reading the body. Leave out the incident that motivated it and
  the option that was rejected — a decision worth keeping goes in this file. When a
  rule is removed the comment loses the sentence describing it; it never gains one
  explaining the removal.
- **Per-field and per-entry comments are individual.** Each dictionary entry,
  record field or config key gets its own line saying what the key is and what the
  value means — never one shared blurb above the block.
- **A file is written to be reviewed by a human**: public surface first, helpers
  after, one concern per file, no function whose comment cannot describe it in a
  sentence.
- **Tests are named as the behaviour they protect**, in full sentences:
  `A_completed_task_lands_in_the_bare_repo_and_the_workspace_is_cleaned_up`. Assert
  observable outcomes — rows, files on trunk, exit codes — not internal calls. CI is
  injectable into `TaskRunner` so orchestration tests need no toolchain.
- **New dependencies are argued for, not added.** Current set: Microsoft.Data.Sqlite
  + Dapper (no EF), Spectre.Console, Microsoft.OpenApi.Readers, Microsoft.Playwright,
  and an ASP.NET `FrameworkReference` for the board. Provider adapters are
  hand-rolled over `HttpClient` — there is no SDK surface to keep in step.

## Where the history lives

This file states the rules as they are now. The record of how they got there —
superseded designs, the M0–M6 build order, incidents that motivated a gate — belongs
in `ARCHITECTURE.md` and the design notes under `docs/`, not here. Some source
comments still cite a v1 specification by section (`spec §7`); read those as
historical markers of a document that has since been folded into `ARCHITECTURE.md`.
