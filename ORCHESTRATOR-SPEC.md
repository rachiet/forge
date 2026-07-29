# Forge — Layer 1 Orchestrator: Design Specification (v1)

> **Purpose of this document.** This is the seed specification for "Forge" (working name): a service that builds software from client requirements by orchestrating a team of AI agents. It captures all architectural decisions made during the design phase so that implementation can begin in a fresh context without re-deriving them. Decisions marked **[DECIDED]** are settled; items marked **[OPEN]** still need resolution during implementation.

---

## 1. Vision

Forge is an orchestration layer ("Layer 1") that takes a client's software idea, refines it into requirements through conversation, and autonomously designs, implements, reviews, and tests the software — producing a working, verifiable product with the client involved only at defined decision points.

The client talks to exactly one agent (the Project Manager). Behind the PM, a team of specialized agents (Principal Engineer, Software Engineers, QA, Researchers) executes the project through a structured pipeline with real quality gates.

**v1 scope:** single machine, single project at a time, one serial worker agent, CLI chat interface, local git repositories. The orchestrator itself is written in **C# (.NET)**. **[DECIDED]**

---

## 2. Core Principles (non-negotiable; every design decision below derives from these)

1. **Grounding over trust.** Agents must verify claims against reality, never assert from memory. "Tests pass" means the harness ran the tests and parsed the output. "PR merged" comes from querying git, not from an agent saying so. Every feature must be verifiable via CLI (headless) — no feature is *done* until a CLI-invokable verification exists for it.
2. **Stateless agents, externalized memory.** Agents are short-lived workers: spun up per task, dissolved after. Anything worth remembering is written to files (docs, MODULE.md, ADRs, task comments) or the database — never kept in a transcript. An "agent" is a *recipe*: model choice + system prompt + context assembly rules. Stateful **within** a task (the act→observe loop keeps its conversation), stateless **across** tasks.
3. **Progressive disclosure via the filesystem tree.** Context is navigated, not loaded. Every directory has a summary file (INDEX.md / MODULE.md) sufficient to decide whether to descend. Agents traverse DFS with pruning. No agent ever receives "the whole codebase."
4. **Artifact-centered communication.** No freeform agent-to-agent chat. Every message references a task, PR, bug, or decision, and has a type (question, answer, review, decision, escalation, status). Bounded task-anchored Q&A is allowed; open-ended discussion channels are not.
5. **Reviewer ≠ author.** No agent grades its own work. Engineers write code + unit tests; the Principal reviews diffs and authors acceptance criteria; QA independently derives tests from requirements (held-out test set). Rejection reasons are written back into CONVENTIONS.md / MODULE.md so the same mistake never survives twice (self-improving loop).
6. **Enforcement lives in the harness, not the model.** Token budgets, iteration caps, timeouts, and file-access scopes are enforced mechanically by the runtime. Models cannot self-limit reliably and are never trusted to.
7. **Cheap generation, expensive verification.** Low-cost models do high-volume code generation; high-reasoning models do design, review, and judgment. Verification (reading a diff) costs a fraction of generation (the write-run-fix loop), so Principal review adds ~10–20% overhead while buying the largest quality gain in the system.
8. **Milestones over waterfall.** Structure is designed fully up front (folder tree, module boundaries, external contracts — expensive to change later); details are elaborated milestone-by-milestone with a client-visible demo at each. Clients don't know what they want until they see something.
9. **Everything is logged by construction.** SQLite is simultaneously the message queue, the task board, the token ledger, and the audit log. The human can replay the entire project from one database file.
10. **Secrets never enter model context.** Agents see references like `{{secret:STRIPE_API_KEY}}`; the runtime substitutes real values at process-execution time, outside the LLM.

---

## 3. Roles

Roles are recipes, not processes. "Spinning up an engineer" = inserting an agent-instance row and assembling its context; it is task-driven (work pulls workers), never headcount-driven.

| Role | Model tier | Always-in-context | Drills into | Never sees |
|---|---|---|---|---|
| **Project Manager (PM)** | High reasoning | PROJECT.md, STATUS.md, task board summary, budget dashboard, requirements INDEX | Requirement sections, change requests | Code |
| **Principal Engineer** | Highest reasoning | PROJECT.md, CONVENTIONS.md, design INDEX, module summaries | Design sections, PR diffs, code files on demand | Full codebase dumps (forbidden by recipe) |
| **Software Engineer** | Cheap/fast coding model | CONVENTIONS.md + task packet only | Files listed in packet, then tool-navigated | Requirements doc, other tasks |
| **QA** | High reasoning | Requirement sections for feature under test, external contract (OpenAPI/CLI spec), run instructions | CLI/API of the running system | Source code (black-box by construction) |
| **Researcher** | High reasoning + web tools | Research task packet | Web, docs | Codebase (delivers findings as markdown artifacts) |

Role authority rules **[DECIDED]**:
- PM owns requirement fidelity; Principal owns technical decisions; Principal wins technical deadlocks with engineers; PM↔Principal deadlocks escalate to the client via the chat.
- Spawning: Principal *requests* an agent instance; PM *approves* (cost chokepoint); hard cap on concurrent instances per project (v1: **1 worker**; the cap is config, not architecture).
- Client talks only to the PM. All client-only logistics (repo access, API keys, accounts) flow: team → PM → client → PM → secrets vault.

---

## 4. Agent Runtime

### 4.1 The agent loop
Each agent instance is a loop owned by the C# harness:

```
assemble context (recipe) → call LLM → parse response for tool calls →
execute tools (shell, file read/write, git, db) → append observations →
repeat until: task done | budget exhausted | iteration cap | agent escalates
```

Tools exposed to agents (v1 minimal set): `read_file(path, range)`, `list_dir(path)`, `grep(pattern, path)`, `write_file(path)`, `run(command)` (sandboxed to project dir, secrets substituted at exec time), `git(...)`, `task_update(...)`, `send_message(...)`, `escalate(...)`.

### 4.2 Statelessness mechanics
- On task start: fresh instance reads its recipe's always-in-context files + the task packet. Spin-up cost target: 5–8K tokens.
- On task end (or budget death): instance writes a **progress note** to the task record. Crash recovery = a fresh instance resumes from the note. Same mechanism handles context-bloat mid-task: write note, die, resume fresh.
- Definition of done always includes: update MODULE.md of touched modules; update docs if decisions changed.

### 4.3 Supervisor (build this FIRST — it is the safety net for everything else)
A harness-level watchdog, not an agent:
- **Token ledger:** every LLM call logs (agent_instance_id, task_id, role, model, tokens_in, tokens_out, cost, timestamp). The API returns exact usage per call.
- **Budgets:** per task (set by Principal at task creation), per agent-instance, per project. At **70%**: inject system nudge ("wrap up or write progress note and escalate"). At **100%**: harness refuses the next LLM call, task → `blocked`, message queued to PM. Budgets are enforced by *not making the call* — never by instructing the model to stop.
- **Iteration cap:** max loop turns per instance (v1 default: 40).
- **Staleness:** task `in_progress` with no tool activity for N minutes → flag to PM.
- Attribution is per **task** (finds tarpits), not just per agent.

---

## 5. The Filesystem Tree (shared long-term memory)

The generated project repo IS the context system. The Principal authors this structure at design time — it is the highest-leverage artifact in the system, which is why the strongest model creates it.

```
project/
├── PROJECT.md            # one-pager: what, why, current milestone. Always in context for all roles.
├── CONVENTIONS.md        # the rules. Always in context. <1 page. Grows via review write-backs.
├── STATUS.md             # PM-maintained; refreshed on milestone events; answers client status queries at ~zero cost.
├── docs/
│   ├── requirements/
│   │   ├── INDEX.md      # ToC, 1-line summary per section, VERSION stamp
│   │   ├── 01-<feature>.md
│   │   └── changes/      # change request docs (see §9)
│   └── design/
│       ├── INDEX.md
│       ├── 01-architecture.md      # birds-eye view (client-visible)
│       ├── 02-data-model.md
│       ├── 03-contracts/           # external contracts: OpenAPI spec, CLI grammar. QA tests against ONLY this.
│       └── decisions/              # ADRs, one file per decision (NNN-title.md)
├── src/<module>/MODULE.md          # per-module summary: purpose, public interface, key decisions, gotchas
└── tests/
    ├── unit/            # engineer-owned, white-box, churns freely with refactors
    ├── acceptance/      # Principal-owned, behavioral, visible to engineers (the contract)
    └── qa/              # QA-owned, requirements-derived, HELD OUT from engineers
```

Tree rules **[DECIDED]**:
- Every directory node has a summary file; reading the summary must suffice to decide whether to descend (enables DFS with pruning).
- Docs live in git with the code — design and code cannot silently diverge; a change request is literally a diff the client can see.
- Freshness: "update MODULE.md for touched modules" is in every task's definition of done; Principal review checklist includes "does the summary still match the code?"; CI nudges (not blocks) when code changes lack a MODULE.md change.
- CONVENTIONS.md content: language/framework, formatting (delegated to tooling: linters enforce style so tokens are never spent debating it), naming, error-handling policy, test layout, commit format, definition-of-done checklist. Keep under one page — agents follow short rules and ignore long ones.

---

## 6. Database Schema (SQLite — queue + board + ledger + audit log in one file)

```sql
-- Message queue AND communication log (same table, by design)
CREATE TABLE messages (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER,              -- groups an exchange
  from_agent TEXT NOT NULL,       -- role or 'client' or 'system'
  to_agent   TEXT NOT NULL,
  task_id INTEGER REFERENCES tasks(id),   -- artifact anchor (nullable only for client chat)
  type TEXT CHECK(type IN ('question','answer','review','decision',
                           'escalation','status','change_request','system_nudge')),
  payload TEXT NOT NULL,          -- markdown body
  status TEXT DEFAULT 'pending' CHECK(status IN ('pending','in_progress','done')),
  created_at TEXT DEFAULT (datetime('now'))
);

-- The unit of work. Bugs are tasks with type='bug'.
CREATE TABLE tasks (
  id INTEGER PRIMARY KEY,
  milestone_id INTEGER REFERENCES milestones(id),
  type TEXT CHECK(type IN ('feature','bug','design','impact_analysis','research','chore')),
  title TEXT NOT NULL,
  objective TEXT NOT NULL,                -- what & why
  acceptance_criteria TEXT,               -- behavioral statements at the external boundary (Principal-authored)
  context_paths TEXT,                     -- JSON: relevant files + design section refs (the packet pointers)
  requirements_ref TEXT,                  -- e.g. "01-users-auth.md@v3" (version-stamped!)
  assigned_role TEXT,                     -- role recipe, not a named individual
  status TEXT DEFAULT 'created' CHECK(status IN
    ('created','ready','claimed','in_progress','in_review','merging',
     'qa','done','blocked','cancelled')),
  token_budget INTEGER NOT NULL,
  tokens_spent INTEGER DEFAULT 0,
  progress_note TEXT,                     -- resume point for fresh instances
  branch_name TEXT,
  created_by TEXT, created_at TEXT, updated_at TEXT
);

CREATE TABLE task_deps (                  -- the DAG; v1 executes its topological sort serially
  task_id INTEGER REFERENCES tasks(id),
  depends_on INTEGER REFERENCES tasks(id),
  PRIMARY KEY (task_id, depends_on)
);

-- Reused for PR review comments AND general task discussion (one object, as designed)
CREATE TABLE discussions (
  id INTEGER PRIMARY KEY,
  task_id INTEGER NOT NULL REFERENCES tasks(id),
  parent_id INTEGER REFERENCES discussions(id),
  author TEXT NOT NULL,
  body TEXT NOT NULL,
  file_path TEXT,                         -- NULL = general comment; set = code review comment
  line_number INTEGER,
  status TEXT DEFAULT 'open' CHECK(status IN ('open','resolved')),
  created_at TEXT
);

CREATE TABLE token_ledger (
  id INTEGER PRIMARY KEY,
  agent_instance_id TEXT, role TEXT, task_id INTEGER,
  model TEXT, tokens_in INTEGER, tokens_out INTEGER,
  created_at TEXT
);

CREATE TABLE milestones (
  id INTEGER PRIMARY KEY, name TEXT, description TEXT,
  status TEXT CHECK(status IN ('planned','active','demo_ready','accepted')),
  ordinal INTEGER
);

CREATE TABLE agent_instances (
  id TEXT PRIMARY KEY,                    -- e.g. 'eng-20260718-093012'
  role TEXT, model TEXT, task_id INTEGER,
  started_at TEXT, ended_at TEXT,
  end_reason TEXT                         -- done|budget|iterations|crash|escalated
);

CREATE TABLE secrets_registry (           -- names & metadata ONLY. Values live in the vault file, never in DB or context.
  name TEXT PRIMARY KEY, description TEXT, provided_at TEXT
);
```

Queue semantics: receivers poll `messages WHERE to_agent=? AND status='pending'` ordered by created_at; mark `in_progress` → `done`. An agent finishes responding to pending messages before claiming the next task.

---

## 7. The Pipeline (end-to-end flow)

```
[Client] ⇄ PM chat (CLI)
   │  intake: thin requirements + milestone plan (NOT full waterfall doc)
   ▼
Requirements v1 (versioned files) ── client sign-off gate ✅
   │
   ▼
Design phase (Principal): folder tree, CONVENTIONS.md, module boundaries,
external contracts (OpenAPI/CLI grammar), birds-eye architecture,
acceptance criteria per feature, task DAG + per-task budgets
   │  gates: PM checks requirements coverage (every req section maps to modules/tasks);
   │         client signs off on birds-eye view ✅
   ▼
Task board (topologically sorted; v1: one serial worker)
   │
   ▼  per task:
Engineer instance: implement + unit tests + update MODULE.md → branch → "PR"
   │
   ▼
CI (mechanical, zero tokens): lint, build, unit tests, acceptance tests
   │  fail → back to engineer. Principal NEVER reviews code that fails CI.
   ▼
Principal review (diff + module summaries, 1–2 turns):
  generality check (anti-Goodhart: "does this solve the problem or just the examples?"),
  convention conformance, design conformance, MODULE.md freshness
   │  reject → discussion thread → engineer revises (Principal wins deadlocks)
   │  repeated same-class rejection → write correction into CONVENTIONS.md / MODULE.md  ← self-improving loop
   ▼
Merge to master (harness performs merge; state from git, not agent claims)
   │
   ▼
QA (black-box): runs qa/ suite against the RUNNING system via external contract only;
reads the requirement section independently; checks acceptance-criteria coverage
("spec misses req 02.3" is a valid bug AGAINST THE PRINCIPAL);
adversarial exploratory testing via CLI (malformed input, sequence-breaking, boundaries)
   │  bug found → task(type=bug) → PM approves → assigned back to authoring flow
   ▼
Task done → board updates → milestone demo when milestone tasks complete
   │
   ▼
Milestone acceptance: PM + QA agree it matches requirements → client demo → accept/iterate
Project success = client accepts final milestone.
```

### 7.1 Testing: the three-layer split [DECIDED]
| Layer | Author | Sees code? | Visible to engineer? | Purpose |
|---|---|---|---|---|
| Unit tests | Engineer | Yes (white-box) | n/a (their own) | Implementation correctness; free to churn with refactors |
| Acceptance tests | Principal | No — written at design time from requirements | **Yes** (they ARE the spec) | The contract; behavioral statements at the external boundary |
| QA tests | QA | **Never** | **No** (held-out set) | Independent interpretation of requirements; catches overfitting/gaming |

Anti-Goodhart stack: visible acceptance criteria define the contract → Principal review checks solution *shape* (hardcoded special-casing is glaring in a diff) → held-out QA tests are the train/test split → optional property-based checks (inherently hard to hardcode against). Code passing visible criteria but failing QA = overfitting alarm → bug to engineer.

### 7.2 External contracts [DECIDED]
Fixed at design time by the Principal: HTTP endpoints + schemas (OpenAPI), CLI commands + flags, file formats. Engineers are free *below* the contract; changing the contract is a design change requiring Principal sign-off (ADR). QA tests against the contract only — internal renames/refactors can never break QA tests. This is also why CLI-verifiability is a hard constraint: it forces every feature to *have* an external contract.

---

## 8. Git & Review (v1: local, in-house) [DECIDED]

- Local bare repo per project on the orchestrator machine (`repos/<project>.git`); working clones per task. Client uploads to a remote (GitHub etc.) themselves after v1 testing.
- Branch per task: `task/<id>-<slug>`. Merge performed by the harness after review approval; merge state is read from git.
- "PR" = the diff between task branch and master + a `discussions` thread (file_path/line_number for inline comments). Same discussion object serves general task Q&A — one object, reused, as designed.
- CI = harness-run scripts (dotnet build / linter / test runners of the *generated* project's stack), zero LLM tokens.
- Post-v1 option: swap to self-hosted Gitea via API without changing the pipeline (the review object maps 1:1 to forge PR comments).

---

## 9. Change Requests (requirements churn) [DECIDED]

1. Client tells PM in chat.
2. PM writes a **change request doc** (`docs/requirements/changes/CR-NNN.md`) in requirements-doc terms and bumps the requirements version.
3. PM creates `task(type=impact_analysis)` for the Principal.
4. Principal returns: affected modules, tasks to amend/cancel/create, estimated token-cost delta. (Bounded typed Q&A on the task thread is allowed; open-ended discussion is not.)
5. PM presents impact + cost to client → approval gate ✅.
6. On approval: task DAG updated; amended tasks re-point their `requirements_ref` to the new version. In-flight tasks referencing a superseded version are flagged — an engineer must never build against a requirement that no longer exists.

Deadlock rule (unchanged from role design): PM (requirements fidelity) vs Principal (technical) disagreements — e.g. cost vs scope collisions in a CR — escalate to the client with both positions summarized.

---

## 10. Client Interface (v1: CLI chat) [DECIDED]

- Console app: `forge chat` opens the client⇄PM conversation (persisted to `messages` with task_id NULL, thread per topic).
- `forge status` — renders STATUS.md + board summary + budget spend (pure DB/file read; zero tokens).
- `forge log [--task N]` — replay any conversation/decision trail from SQLite.
- `forge secrets set NAME` — writes value to the local vault file (e.g., DPAPI-protected or age-encrypted); registers name in `secrets_registry`. Agents only ever see `{{secret:NAME}}`.
- PM behavior rules: answers status from the board/STATUS.md, never by waking agents ("status is a query, not a conversation"); wakes another agent only when genuinely necessary; silent-by-default monitors — the PM messages the client on state changes and anomalies, not on schedule.
- Web UI: later; the chat is already just rows in `messages`, so a web front-end is additive.

---

## 11. C# Implementation Notes

- **Runtime:** .NET 8+ console host; `System.CommandLine` (or Spectre.Console) for CLI; single-process, single worker loop for v1.
- **DB:** `Microsoft.Data.Sqlite` + Dapper (schema is small; avoid EF ceremony). One `.db` file per project + one global.
- **LLM gateway:** one interface `ILlmClient { Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct) }` with per-provider adapters (Anthropic first). ALL calls go through one `MeteredLlmClient` decorator that writes the token ledger and enforces budgets by refusing calls — the supervisor is a decorator, not a convention.
- **Tool execution:** `System.Diagnostics.Process` with working-directory jail to the project folder, allowlisted binaries (git, dotnet, curl), output capture streamed into the agent loop as observations, timeout per command. Secret substitution happens here, after the LLM produced the command and before exec.
- **Agent recipes:** config records: `{ role, model, systemPromptPath, alwaysInContext[], toolAllowlist[], defaultBudget }`. Personas are data, not classes.
- **Generated-project stacks:** **[DECIDED]** C#/.NET is the only supported target stack for v1 — same as the orchestrator. Generated projects are .NET 8+ (ASP.NET Core minimal API for services, console for CLIs, xUnit for tests). Other stacks stay possible behind the CI-adapter seam, but none are built until a real project needs one. Rationale: one toolchain (`dotnet build` / `dotnet test`) for both Forge and its output means one CI adapter, one allowlist, one set of conventions — and Forge building Forge-shaped code is the dogfood test.

---

## 12. Build Order (milestones for building Forge itself — eat the dogfood)

1. **M0 — Skeleton + Supervisor:** SQLite schemas, MeteredLlmClient (ledger + budget refusal), tool executor with jail + secret substitution, `forge log`. *No agents yet.* The safety net exists before anything can misbehave.
2. **M1 — Single agent, single task:** one Engineer recipe, agent loop, task claiming, progress notes, git branch + harness merge (no review yet). Prove: fresh-instance resume after kill.
3. **M2 — PM chat + intake:** `forge chat`, PM recipe, requirements tree authoring, milestone plan, versioned requirement files, STATUS.md.
4. **M3 — Design phase:** Principal recipe; tree/CONVENTIONS/contracts/acceptance-criteria/task-DAG generation; PM coverage gate; client sign-off gate.
5. **M4 — Review + CI:** CI scripts, discussion objects, Principal review flow, rejection write-back loop into CONVENTIONS.md.
6. **M5 — QA:** held-out test authoring from requirements, black-box runner, bug pipeline via PM approval, coverage-gap bugs against the Principal.
7. **M6 — Change requests + escalation:** CR docs, impact-analysis tasks, version re-pointing, deadlock escalation to client.
8. **First real project:** something small with a clean external contract (e.g., a REST todo API + CLI client) end-to-end overnight.

Anti-pattern to avoid (learned from MetaGPT/ChatDev): standing up all personas at once. A team you can't feed is theater — one pillar at a time.

---

## 13. Open Items

- ~~**[OPEN]** First supported target stack for generated projects.~~ **[DECIDED]** C#/.NET 8+ — see §11. One toolchain for orchestrator and output; other stacks deferred behind the CI-adapter seam.
- **[OPEN]** Exact model assignments per role tier (pin at implementation time; they change monthly). Principle is fixed: highest reasoning = Principal/QA/PM/Researcher; cheap coding tier = Engineer.
- **[OPEN]** Property-based testing: include in v1 acceptance layer or defer.
- **[OPEN]** Researcher role triggering: on-demand by Principal during design (likely), or also available to PM during intake.
- **[OPEN]** Concurrency later: task-claiming semantics are already N-worker-safe (claim→work→release); turning N up is config. Decide the merge-conflict policy only when N>1 becomes real.
- **[OPEN]** RAG over requirements/design prose for PM's "what did the client say about X" queries — optional optimization, not v1-blocking (tree navigation may suffice).

## 14. Known Risks (accepted, with mitigations)

| Risk | Mitigation |
|---|---|
| Agent confabulates completion | Grounding: harness verifies via CI/git/CLI, never trusts claims |
| Token runaway | Supervisor budget refusal + iteration caps + staleness flags (M0, built first) |
| Stale summaries poison pruning | MODULE.md updates in definition-of-done + review checklist + CI nudge |
| Engineer games visible tests | Held-out QA suite + Principal generality review |
| Requirements drift mid-flight | Versioned requirement refs on tasks + CR re-pointing |
| Context bloat mid-task | Progress note + fresh-instance resume (same mechanism as crash recovery) |
| Secrets leak into logs/context | Reference substitution at exec time; values never in DB or transcripts |

— End of specification —
