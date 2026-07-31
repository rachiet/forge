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

### Change requests [DECIDED] (M6 — a change to an already-built project)
- **Same spine, two deltas.** A CR reuses everything: the client talks to the PM
  (`forge chat`) who updates the affected requirement(s), then `forge design run`,
  `forge design approve`, `forge run --loop`, CI + review + merge, and QA. Only two
  things differ.
- **Design becomes impact analysis, not greenfield** (`DesignPhase.ChangeRequestBrief`).
  A design run is a CR iff the project already has a `done` task. In that mode the
  Principal reads the existing structure/contracts/MODULE.md, writes an impact note to
  `docs/design/impact/`, and creates **only the delta** tasks (never recreating done
  work) — or `escalate`s if the change is ill-advised (the pushback path). The cost of
  the change is the sum of the delta tasks' budgets, which the client sees at sign-off.
- **QA re-arms via the generalized watermark.** The QA gate's "new work to verify"
  signal is now the count of **all done tasks** (`CountDone`), not done bugs — so a
  CR's completed tasks re-trigger QA exactly as a bug-fix does, and the full acceptance
  suite re-runs to catch regressions the change caused. `design approve` clears any
  `qa_escalated` flag so a CR is a fresh QA cycle. Specs live in the **client** repo
  (requirements/contracts updated before the change; MODULE.md by the engineer during it).

### QA/triage hardening [DECIDED] (settled after the first live QA run)
- **A bug carries machine-captured evidence, not model prose.** `file_bug(title,
  expected, [requirements_ref])` no longer takes a free-form `actual`/`repro`; the
  toolset records QA's most recent `run` and attaches that command + its real output
  verbatim. `file_bug` **refuses if QA hasn't run anything** — no execution, no bug.
  This killed a live false-positive where QA reported an error the server never emits.
- **Triage/QA phases get crash-retry** (`RunWithCrashRetryAsync`): a provider blip in
  `TriageBugAsync`/`TriageAsync`/`RunQaAsync` retries in place (crash cap) instead of
  escalating to a human on the first failure — the resilience task runs already had via
  Park. A QA round that still crashes does NOT advance the watermark (would falsely mark
  the project verified); it sets `qa_escalated` and surfaces to the human.
- **A reviewer can reject a bug** (`reject_bug` added to `PrincipalReview`, reachable
  from `in_review`): if a bug-fix review shows the reported defect isn't real, it closes
  the bug instead of looping `request_changes` forever — the failure mode that burned a
  whole strike ladder on a non-bug.
- **Human review flows through the PM chat.** Pending escalations to `pm` are injected
  into the PM's turn (`PmChat.OpenEscalations`); the PM resolves each with `reject_bug`
  (close it) or `retriage_bug(note)` (send it back to the Principal with the client's
  guidance) — no more tasks stranded behind a message nobody reads, no DB surgery to
  resume. The autonomous loop never blocks on a human: an escalated task is skipped.

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

### Models, providers and cost [DECIDED] (settled while adding multi-provider support)

- **No code names a model — recipes name a *tier*.** `ModelTier` is `Fast | Coding |
  Reasoning` (the vocabulary spec §3 already used), and `AgentRecipe.Tier` replaced the
  old `Model` string. The configured `ILlmClient` resolves tier → model id via
  `ModelFor(tier)`, so orchestration policy ("an engineer needs the coding tier") stays
  separate from provider knowledge ("what that tier is called at Anthropic today").
  Adding a role remains a record + a prompt file; adding a provider is one adapter class
  with a three-entry tier map plus one case in `LlmClientFactory`. `AgentLoop` resolves
  the model **once per instance**, never per turn — a conversation must not change model
  underneath itself. Three adapters exist: `AnthropicLlmClient`, `OpenAiLlmClient`,
  `GeminiLlmClient` (hand-rolled over HttpClient — Forge uses no SDK surface, since tool
  calls are parsed out of plain text). Each adapter's default tier ids are **price-table
  keys**, so every default is priceable out of the box.
- **The adapter normalises usage; the rest of Forge sees one meaning.** `LlmUsage.TokensIn`
  is always the *uncached prompt remainder*, which each provider reaches differently:
  Anthropic's `input_tokens` already excludes cached tokens, while OpenAI's `prompt_tokens`
  and Gemini's `promptTokenCount` include them and must have the cached count subtracted.
  Two further traps, both settled from the published schemas: OpenAI reports
  `cache_write_tokens` but prices no cache-write rate (it bills them as ordinary input),
  so they stay inside `TokensIn` and `CacheWriteTokens` is left at zero — mapping them
  across would ask the pricer for a nonexistent rate and throw. Gemini reports
  `thoughtsTokenCount` separately from `candidatesTokenCount` but bills it as output, so
  output is their **sum**. Gemini ids also carry the price table's `gemini/` prefix and the
  adapter strips it for the URL, so one canonical id travels through recipe, ledger and pricer.
- **The provider is configuration, not code.** `<data root>/llm.json` names the provider
  and may pin any tier's model id; `FORGE_LLM_PROVIDER` overrides it (same
  environment-beats-file rule as `EnvFile`). No file at all is valid — the default
  provider's built-in map applies. Malformed JSON throws rather than silently defaulting,
  because the only other symptom would be a surprising bill.
- **Prices are fetched, never hardcoded** (`Llm/Pricing/`). `PriceCatalog` reads
  LiteLLM's table (`model_prices_and_context_window.json`), chosen because its keys are
  provider-native model ids — the exact strings recipes resolve to — so no name mapping
  can silently mis-price. Cached in memory for the process and on disk at
  `<data root>/prices/` with a **1-day TTL** and a conditional GET; machine-wide, not per
  project, since prices are not project-scoped. A failed refresh falls back to the stale
  snapshot; a **model miss forces one refresh before it is allowed to fail**, because the
  everyday cost of a TTL is a model newer than the table.
- **An unpriced model refuses to run.** No zero-cost fallback, no guessed cache
  multiplier — with a dollar budget, costing $0 is a cap that never trips. Same rule for
  a cache bucket with no rate: free while the bucket is empty, `ModelNotPricedException`
  the moment it isn't.
- **The ledger stores all four token buckets plus cost.** `tokens_in` (the *uncached*
  prompt remainder), `tokens_out`, `cache_read_tokens`, `cache_write_tokens`, plus
  `cost_nanos` (USD × 1e-9, an integer so `SUM()` stays exact) and `priced_with` (the
  snapshot that priced it). Keeping the buckets is what makes a row's cost recomputable
  when a rate is corrected — the property that makes depending on an external feed safe.
- **Two budget units, two jobs — and two different failure modes.** The **project**
  budget is USD (stored per project; `--project-budget` overrides per invocation),
  enforced by summing `cost_nanos` and re-read **per call** when unset on the CLI, so
  raising it from the board takes effect mid-build. A **task** budget stays tokens and
  deliberately still counts only `tokens_in + tokens_out`: an approximate guard on one
  runaway agent, undercounting real traffic ~25x because cache reads dominate —
  knowingly tolerated; money safety is the dollar cap's job.
- **A spent project cap PAUSES; a spent task budget STRIKES.** They arrive as the same
  `EndReason.Budget`, distinguished by `BudgetExhaustedException.ProjectCap` →
  `AgentRunResult/TaskRunOutcome.ProjectBudgetExhausted`. Task-budget exhaustion is that
  task's failure: OutOfBudget, a strike, the Principal's queue. Project-cap exhaustion is
  a client money decision: the task is left exactly as it stands (claimable, no strike,
  no transition), the loop stops pulling work, ONE escalation reaches the PM (deduped
  against pending), and QA/triage phases short-circuit without moving the QA watermark —
  a budget-refused QA round must never mark the project verified. The old behaviour
  marched every remaining task through the strike ladder to blocked; do not reintroduce
  it. Covered by `BudgetPauseTests`.
- **Per-role spend needs no new attribution.** `token_ledger` has carried
  `agent_instance_id`, `role` and `task_id` since M0, so "what did the PM cost" is
  `GROUP BY role` (`LedgerRepository.SpendByRole`, shown by `forge log`). Role granularity
  only: design/review/triage/implementation all report as `principal`, which is accepted.

### Client progress board [DECIDED] (`forge board <project>`)

- **The client's whole window on the project**: milestones, features, spend per agent, and
  the PM chat — which stays their only input. Served by `forge board` on localhost via a
  minimal API hosted inside the CLI (a `FrameworkReference`, not a second executable);
  the page polls `/api/board` every 3s.
- **A read model, never a second source of truth** (`Board/BoardQuery.cs`). Every figure is
  queried from tasks/milestones/token_ledger/messages at request time, so the page cannot
  drift from the ledger. Nothing is cached or denormalised.
- **Milestone state is derived from its tasks — there is NO stored status column.**
  `milestones.status` was dropped (with `MilestoneStatus`): nothing ever advanced it past
  `planned` through a whole finished project, and a stored status beside a derived one is
  two sources of truth. done = all tasks done; active = any in flight or any done; else
  pending. The same cleanup dropped write-only `messages.thread_id` and the dead
  `projects.token_budget` on the global registry; `project_meta` accessors live on
  `Db/ProjectMetaRepository`, not TaskRepository.
- **Milestone linkage is the harness's job, not the model's.** `create_task`/`create_feature`
  accept `milestone`, the design brief now lists the PM's milestones with their ids
  (`DesignPhase.MilestoneSection`) so the Principal has something to pass, and
  `TaskRunner` makes a child **inherit its Feature's milestone** when it adopts it —
  including subtasks created during a stuck-task triage, which are also adopted, given
  the parent's milestone, and **released to `ready`** (before that fix they were born
  `created` with no release path and deadlocked the board). PM guidance: a change-request
  Feature names its one milestone; the initial build passes NONE (it spans the whole
  plan — the Principal assigns per task).
- **"Work outside features" is a required section, not a nicety.** ~15% of calls carry no
  `task_id` at all (PM chat, design, QA) and bugs/chores sit under no Feature — on
  weatherboard, features were $15.58 of a $23.45 total. Without that row the client sees
  $7.87 they cannot account for. `BoardQueryTests` asserts the reconciliation.
- **Chat is a real PM turn.** `POST /api/chat` runs `PmChat.SendAsync` in the background
  (a turn takes far longer than a browser will wait) behind a semaphore, and the reply
  arrives via the same polling that drives the rest.
- **`forge board` takes no project** — it serves them all, with the project as a query
  parameter, so the dropdown switches without restarting the host. `POST /api/projects`
  creates one (name, budget, provider) via `ProjectBootstrap.Init`; the client never
  needs a terminal.
- **Per-project settings live in `project_meta`** (`ProjectSettings`: `llm_provider`,
  `budget_usd`), not in the global registry and not in `llm.json`. Two reasons, both bugs
  that existed before: `llm.json` is machine-wide, so two projects could not choose
  different providers — the second silently ran on the first one's models; and
  `--project-budget` was a per-invocation flag, so the next run that omitted it had **no
  cap at all**. Provider precedence is `FORGE_LLM_PROVIDER` → project → llm.json → default.
  The budget is raisable from the page, because hitting the cap otherwise dead-ends a
  client whose only interface is the board.
- **The spec appears when the PM hands work over**, not while it is being drafted:
  `SpecReady` is "a Feature exists", since `create_feature` is the PM's handoff to the
  Principal. Read from **trunk via `git show`** (`Board/SpecReader.cs`), never from the
  PM's working clone, which can sit mid-edit. Rendered inline and collapsed.
- **One build at a time, machine-wide** (`Scheduling/WorkerLease.cs`) — a decision, not a
  limitation. A JSON lease at `<data root>/worker.json` carrying a heartbeat; both the
  board's Start button and a terminal `forge run` take it, so they cannot collide on one
  database. Staleness is judged by the heartbeat rather than file mtime, so a crashed
  worker frees the lease by falling silent. This finally closes the "forge run has no
  concurrency guard" hazard. Two properties are load-bearing: the lease **beats itself**
  on an internal timer (a single task run routinely outlasts the 90s timeout — beating
  only between tasks let the lease read stale mid-task, re-opening the exact collision it
  prevents), and acquisition is **atomic** (`FileMode.CreateNew`, not check-then-write,
  which let two simultaneous acquirers both win). Stopping mid-task is safe by design:
  the cancelled instance parks and the task resumes via the normal kill-and-resume path.
- **"Building" and "working" are different facts and stay separate.** `state` is how far
  the plan has got (from the data); `building` is whether a worker is alive this second
  (from the lease). A project can sit at `building` with nothing running, and the client
  must be able to see that — the pill reads "planning · idle" or "working".

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
- Never hardcode a model id or a model price: recipes name a `ModelTier`, the configured
  `ILlmClient` resolves it, and rates come from `PriceCatalog`. An unpriced model refuses
  to run. See "Models, providers and cost" above.
- Merge/CI/test state read from git and process output, never from agent claims.
- Secrets: agents see `{{secret:NAME}}`; substitution in the tool executor at
  exec time; values never in DB, context, or logs.
- Every feature of generated projects must be CLI-verifiable; Principal must
  design an observable side-channel for otherwise-internal behavior
  (e.g. an X-Cache header for a cache).
- .NET 8+ console host; Microsoft.Data.Sqlite + Dapper (no EF);
  System.CommandLine or Spectre.Console for the CLI.
