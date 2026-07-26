# Forge — Orchestrator Implementation

You are implementing Forge: a C#/.NET service that builds software from client
requirements by orchestrating stateless LLM agents (PM, Principal, Engineer, QA,
Researcher). **Read `ORCHESTRATOR-SPEC.md` in full before writing any code.**
It is the authoritative design document; this file adds decisions made after it.

## Terminology
- **Orchestrator** = the whole service (scheduler, pipeline state machine, roles).
- **Harness** = the inner, deterministic layer wrapped around each LLM call:
  context assembly → LLM call → tool-call parsing → jailed tool execution →
  observation loop → budget/iteration enforcement → ledger + progress notes.
  One process, two layers; the orchestrator contains the harness.
- Everything in the harness is trusted mechanical code; everything from the
  model is untrusted output under supervision (spec Principle 6).

## Post-spec decisions (settled in design discussion — treat as [DECIDED])

### Prompt layering (do NOT store prompts in the tasks table)
Agent instructions are assembled at spin-up from three layers:
- **Layer A — role identity:** `prompts/roles/<role>.md` (versioned in git).
- **Layer B — task-type instructions:** `prompts/tasks/<type>.md`
  (e.g. design.md, feature.md, review.md, impact_analysis.md).
- **Layer C — task packet (DB only):** `objective`, `acceptance_criteria`,
  `context_paths`, `requirements_ref`, `progress_note` from the task row.
Extra one-off guidance travels as a task-anchored `messages` row, never as a
per-task prompt blob. Rationale: prevents prompt drift; fixing a template file
improves all future tasks (same self-improving property as CONVENTIONS.md).

### Routing ("who acts next")
Derived, not stored. `assigned_role` says who executes; every other handoff is
a static harness map from status → role (in_review → principal, qa → qa,
blocked → pm). Never add a "next actor" column — two sources of truth drift.

### DB column vs JSON rule
Anything the harness must query or enforce (status, budgets, roles, milestone)
is a real column with CHECK constraints. Anything only the LLM reads may be
TEXT/JSON (`context_paths` is JSON by design).

### Typed layer over SQLite (C#)
- One `sealed record` per table (e.g. `TaskRecord`); enums for `TaskType`,
  `TaskStatus`, `AgentRole` mirroring the CHECK constraints (keep both layers).
- Dapper + small type handlers (enum ⇄ snake_case TEXT, JSON list ⇄ TEXT).
- `RequirementsRef` is a parsed value type ("02-todos-read.md@v3" → File +
  Version); parse-don't-validate at the DB boundary, throw on malformed.
- `Message` is an abstract record with one sealed subtype per message type
  (Question, Answer, Review, Decision, Escalation, Status, ChangeRequest,
  SystemNudge); exhaustive switch for routing.
- Construction via factory methods enforcing invariants (budget > 0,
  non-empty packet), not naked inserts.
- Status changes go through a `TaskTransitions` legal-transition map that
  throws on illegal transitions — never raw `UPDATE tasks SET status=?`.
- Tasks/messages do NOT generate .md files. Repo .md files (MODULE.md, ADRs,
  requirements) are written by agents via write_file. The only markdown the
  harness renders is the task packet into the prompt (never to disk).

## Directory layout [DECIDED]
Two roots. Client project data NEVER lives inside the Forge source repo.
- Forge source repo (this repo): src/, prompts/, docs.
- Runtime data root: single config value `ForgeDataRoot` (env `FORGE_HOME`,
  default `~/forge-data`) — the only path the code hard-knows; derive all else:
  - `forge.db` (global DB), `vault/` (encrypted secrets)
  - `projects/<name>/project.db` — per-project SQLite (queue/board/ledger)
  - `projects/<name>/repo.git` — bare repo, source of truth; harness merges here;
    generated code + full docs tree (PROJECT.md, requirements, MODULE.md) live in it
  - `projects/<name>/workspaces/task-<id>/` — per-task working clone; this exact
    path is the tool executor's jail; created on claim, deleted after merge
Jail + DB locations are M0 concerns — build the path logic in M0.

### Credentials file [DECIDED] — the one deliberate exception
`~/forge_env` (override: env `FORGE_ENV`) holds **Forge's own** credentials,
loaded into the process environment at CLI startup. Forge authenticates its calls
to the Anthropic Messages API with a Claude API key in `ANTHROPIC_API_KEY`
(`sk-ant-api…`, sent as the `x-api-key` header). One credential path means one
thing to configure and one failure mode to recognise. This is a second hard-known
path, knowingly: the data root holds client repos and databases and is meant to be
movable and shareable, so keys must not ride along in that payload.
- Two kinds of secret, never mixed: harness keys → `forge_env`; client project
  secrets → the encrypted `vault/`, seen by agents only as `{{secret:NAME}}`.
- The tool executor builds child-process environments from an **allowlist**
  (PATH, TMPDIR, LANG, DOTNET_*/NUGET_*), never by inheritance, and points HOME
  at the jail. An agent's `dotnet run` is arbitrary code execution, so inheriting
  Forge's environment would leak every key. A key added to `forge_env` tomorrow
  is therefore invisible to agents by default, with nothing to remember.

### QA [DECIDED] (M5a — project-level acceptance gate)
- **QA is project-level, not per-task.** A scaffold or a half-built feature has no
  observable behaviour to black-box; acceptance is a feature/requirement concern. So
  the per-task `Qa` hop is gone (a task goes `merging → done`), and QA runs only when
  the **whole board is complete** — `RunNextByPriorityAsync` calls `MaybeRunQaAsync`
  once no task/triage work remains. Same trigger for the first build and for a later
  change request.
- **QA tests the observable side-channel, never the source** (`AgentRecipe.Qa`,
  `prompts/roles/qa.md`): it exercises the HTTP/CLI contract the Principal designed
  and files a bug per unmet requirement. It ignores the engineer's white-box unit
  tests and does not judge aesthetics — visual "feel" stays the client's call. (A
  persistent, committed acceptance-test suite is a deferred refinement; M5a verifies
  in an ephemeral trunk clone.)
- **Bugs are first-class tasks with a triage lifecycle.** `file_bug(title, repro,
  expected, actual, [requirements_ref])` creates a `bug` task born **`triage`**
  (Principal-owned). The Principal `accept_bug` (→ `ready`, an engineer fixes it
  through the normal CI+review+merge) or `reject_bug(reason)` (→ **`rejected`**, a
  durable "not a bug" verdict — kept, never deleted). Two new statuses:
  `Triage`, `Rejected`.
- **No QA↔fix loop, guaranteed by counts.** QA is seeded with the **bug ledger**
  (rejected + open) so it does not re-file — but the hard guarantee is the
  termination rule, not the model's diligence: a `qa_fix_watermark` in `project_meta`
  tracks how many bug-fixes QA has verified, and QA re-runs only when
  `CountBugs(Done)` exceeds it. A cycle that accepts nothing new (all bugs rejected,
  or none filed) never moves the watermark, so QA is not called again → **project
  complete**. A non-converging project escalates to the client after `QaRoundCap` (5).

### Review + CI [DECIDED] (settled while building M4)
- **CI is harness-run, zero tokens** (`Ci/CiRunner.cs`): the harness runs
  `dotnet build` then `dotnet test` in the task workspace itself — trusted code
  like Git.cs, not the agent's jailed executor. No project (no .sln/.csproj) is a
  skip, not a failure (docs-only or not-yet-scaffolded). Injectable into
  `TaskRunner` (`Func<string,CiResult>`) so orchestration tests don't need a
  toolchain; production uses `CiRunner.Run`.
- **The gate order is CI, then review** (spec §7): the Principal never reviews code
  that fails CI. `TaskRunner.IntegrateAsync`: commit → commits-ahead check → push →
  CI → review → merge. QA still auto-passes (M5).
- **Review is a fresh Principal instance** (`Review/ReviewPhase.cs`,
  `AgentRecipe.PrincipalReview`) — reviewer ≠ author. Seeded with the branch diff
  (`WorkspaceManager.DiffAgainstTrunk`), ends with `approve` or `request_changes`
  (verdict rides `AgentRunResult`). No run() (CI already built); may write
  CONVENTIONS.md.
- **Revision loop back to the engineer.** CI failure or a rejected review sets the
  progress note to the feedback and leaves the task claimable (`in_progress`), so
  the next `forge run` resumes the engineer with the feedback in its packet — same
  resume mechanism as a kill. Bounded: `RevisionCap` (5 engineer attempts) →
  block + escalate to PM, counted from `agent_instances`.
- **Self-improving write-back** (spec §7): `request_changes(reason, convention?)`
  — the reason goes to the engineer; an optional `convention` is appended to
  CONVENTIONS.md on trunk (`WorkspaceManager.AppendToTrunkFile`), so a recurring
  mistake is ruled out once for every future engineer.
- **Discussions table now used** (`Db/DiscussionRepository.cs`) — review verdicts
  and CI-fail feedback are recorded as discussion rows, the task's rejection history.

### Design phase [DECIDED] (settled while building M3)
- **The Principal is the same loop, seeded with a design brief** — not a chat, not
  a board task. `Design/DesignPhase.cs` runs it on a long-lived trunk clone (like
  the PM's doc work), commits structure/CONVENTIONS/contracts to trunk, then runs
  the coverage gate. Highest-reasoning recipe (`AgentRecipe.Principal`, opus),
  unrestricted workspace scope (a technical role, unlike the PM), no run().
- **The task DAG is real rows, not prose.** `create_task` inserts board tasks
  (born `created`), `add_dependency` writes `task_deps` edges. Both are toolset
  tools gated to the Principal's recipe.
- **PM coverage gate is mechanical** (`Design/CoverageGate.cs`): every
  `docs/requirements/NN-*.md` must be named by some task's `requirements_ref`, or
  it's reported as uncovered. Ground truth, not an LLM claim.
- **Client sign-off gate uses status, not a new field.** Design tasks are born
  `created` (unclaimable); `DesignPhase.Approve` flips them to `ready`. That
  transition IS "the client accepted the design" — `forge design approve`. Until
  then `forge run` finds no work.
- CLI: `forge design run <project>` then `forge design approve <project>`.

### Agent runtime [DECIDED] (settled while building M1/M2)
- **Recipes declare their tools and file scope.** `AgentRecipe.Tools` is the
  allowlist the toolset enforces and the prompt renders — one list, so a role
  cannot be told about a tool it does not have. `AgentRecipe.Scope` (a
  `PathScope`) is how "the PM never sees code" becomes mechanical: the PM is
  scoped to PROJECT.md, STATUS.md and docs/, and `read_file src/…` is refused by
  the harness, not by the model's manners (Principle 6 lists file-access scopes).
- **Chat is the same loop as task work**, seeded with the conversation instead of
  a task packet, and ended by a `reply` tool rather than `done`. Metering, budget
  refusal, the jail and the iteration cap therefore apply to the PM unchanged.
- **Chat history lives in the `messages` table**, replayed into an alternating
  conversation on every turn. The PM is as stateless as an engineer: `forge chat`
  can be closed, reopened, or resumed from another terminal.
- **The PM commits docs straight to trunk** from a long-lived `workspaces/pm/`
  clone. Requirements are the PM's own artifacts and the client is their
  reviewer via sign-off, so they do not go through the task branch/review path.
- **Provider errors park work, never crash the process.** The provider is a
  network boundary; a 429 or auth failure ends the instance as `crash` with the
  workspace and progress note intact, so the resume path handles it.

### Logging / observability [DECIDED] (settled while building the event log)
- **Six columns, fixed:** `timestamp | project | task | domain | action | message`.
  `project` is on every line (the story); `task` is the unit within it and is
  null for project-level events (intake chat, milestone planning). A task line
  still names its project, so filtering by project is a superset of every task —
  "all logs for the project" and "logs for one task" are the same rows, one
  filter apart. There is NO single "scope" column: project and task are two
  levels of identity, not two values of one field.
- **`domain` + `action` are rendered from one closed `EventType` enum**
  (`Logging/EventType.cs`), split at write time and reassembled on read with
  `EventTypes.FromColumns`. The enum is the single source of truth, so the two
  columns can never disagree, and filtering is an equality check (`domain='tool'`
  to skip or find a domain; `action='write_file'` across the whole project).
  - Typed mechanical events split as `domain`/`action`: `tool`/`write_file`,
    `git`/`merge`, `lifecycle`/`instance_start`, `llm`/`call`, `error`/`provider`.
  - The `message` domain has an empty `action` — free-form, human-readable,
    covering agent↔client communication AND ordinary service/debug logging from
    harness code ("creating util file X"). The line you actually read.
- **The logger API is two methods** (`Logging/ForgeLogger.cs`): `Event(EventType,
  msg)` for typed events (the enum is the only category argument, so a git merge
  can't be mis-tagged as lifecycle — the old per-domain methods were a footgun and
  are gone), and `Message(msg)` for the free-form channel. Tool events derive their
  type from the tool name (`EventTypes.ForTool`) and are never hand-written.
- Read back with `forge log <project> --events [--task N] [--domain D]`.
- **Swappable sink behind `ILogSink`** (`Write(LogEntry)`). Default is
  `FileLogSink` → `projects/<name>/forge.log` (per-project, so isolation is
  structural). `ConsoleLogSink`, `CompositeLogSink` (fan-out, "push anywhere"),
  and `NullLogSink` exist; a remote sink is a drop-in. Changing destination is
  one line at the CLI, no call-site changes.
- **`ForgeLogger` is the facade** every emit point calls; `.For(taskId)` binds
  the correlation once so call sites emit a one-line message. Optional everywhere
  (defaults to `ForgeLogger.Null`), so logging is never required to run and did
  not disturb existing constructors/tests.
- **Emit points:** toolset (one line per tool call, `tool.refused` on refusal),
  loop (instance start/end, llm.call, llm.nudge, llm.refused, error.provider),
  runner (task transitions, git branch/push/merge), PM chat (message.sent,
  git.commit). Read back with `forge log <project> --events [--task N]`.

## Build order (spec §12 — follow strictly, do not skip ahead)
M0 first: SQLite schemas, MeteredLlmClient (ledger + budget refusal as a
decorator), tool executor with working-dir jail + secret substitution,
`forge log`. No agents until M0 is done. Then M1 (single agent, single task,
kill-and-resume proof) → M2 (PM chat) → M3 (design) → M4 (review+CI) →
M5 (QA) → M6 (CRs). Anti-pattern: standing up all personas at once.

## Non-negotiables to preserve in code
- Budgets enforced by refusing the next LLM call, never by asking the model.
- All LLM calls flow through one `MeteredLlmClient` decorator (ledger + caps).
- Merge/CI/test state read from git and process output, never from agent claims.
- Secrets: agents see `{{secret:NAME}}`; substitution in the tool executor at
  exec time; values never in DB, context, or logs.
- Every feature of generated projects must be CLI-verifiable; Principal must
  design an observable side-channel for otherwise-internal behavior
  (e.g. an X-Cache header for a cache).
- .NET 8+ console host; Microsoft.Data.Sqlite + Dapper (no EF);
  System.CommandLine or Spectre.Console for the CLI.
