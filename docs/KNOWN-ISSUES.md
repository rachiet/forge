# Known issues

Defects found by running BillSplitter and HabitTracker end to end (2026-08-04/05)
and deliberately deferred. Each was re-verified against the code on 2026-08-05.

## 1 — QA's evidence chain

These three are the difference between QA that verifies and QA that only appears to.

**1.1 A failed `serve` still counts as evidence.**
`QaTools.cs:149` calls `RecordEvidence` on the startup-failure path, so a round that
never reached a running server can still back a bug — one live round filed four bugs
carrying an MSBuild error as their "Observed" proof.
*Fix:* leave `_lastRunTrace` unset when the process exits during startup; the trace is
still returned to the model as text, it just cannot be quoted as proof.

**1.2 QA passes on zero bugs regardless of whether it observed anything.**
`TaskRunner.cs:555` reads `filed == 0` as success, so silence and verification are
indistinguishable — a QA round that never started the app reports the project verified.
*Fix:* require at least one recorded observation before a pass; no execution, no verdict.
Same rule `file_bug` already enforces.

**1.3 `escalate` is impossible for project-scoped roles.**
QA runs with no task and `Message.Create` (Message.cs:35) rejects an unanchored
`qa → principal` message, so QA's only escape hatch throws.
*Fix:* allow a project-anchored escalation, or give the QA round a synthetic task id.

## 2 — The client gets stuck

**2.1 The PM's stuck-work message tells the client nothing.**
`PmChat.cs:110` orders the PM not to repeat the engineering notes, so the client is told
only that there is "a technical blocker" and cannot answer usefully. Every reply this
session had to be written from the database instead.
*Fix:* keep the ban on task ids, token counts and tooling, but require plain-language
substance ("the tests can't start the database, so we can't verify the endpoint") and
what has already been tried.

**2.2 Answering the PM does not restart the build.**
`resolve_task`, `retriage_bug` and `cancel_task` make work claimable but start no worker,
so the client must also press Start. Approving a requirements proposal is not affected —
the board's approve handler already calls `StartWorker`, which is the pattern to copy:
try the lease; if another worker holds it, do nothing, since that one will pick the work up.

## 3 — Budgets

**3.1 A reviewer inherits the task's whole token budget.**
`AgentRecipe.PrincipalReview` declares `DefaultBudget = 300_000`, but enforcement is per
instance against `tasks.token_budget` — so a review of a 900k task got 900k, and one ran
to 436k tokens over 9 turns before reaching a verdict.
*Fix:* enforce the recipe's budget when it is lower than the task's.

**3.2 `file_bug` defaults to a 60k token budget.**
`AgentToolset.cs:373,546` — far too small now that enforcement counts every bucket
including cache reads. A bug-fix task dies of budget before it starts.

**3.3 The task budget measures conversation length, not effort.**
Counting cache reads at full weight means a ~70k prompt costs 70k every turn, so any task
needing more than ~12 turns dies regardless of difficulty; one endpoint outspent the rest
of HabitTracker. This is a policy question, not a bug — decide what the budget is for.

## 4 — Smaller

**4.1 `how_to_run` records a command with no working directory** (`AgentToolset.cs:501`),
so a command QA really ran can still be unrunnable for the client from the delivered folder.

**4.2 A successful `how_to_run` writes no log line** — `EventTypes.ForTool` has no entry.

**4.3 The PM created its milestone plan twice.** HabitTracker has milestones 1 and 2 holding
every task plus 3 and 4 with the same names and none. Harmless to the board since empty
milestones are now skipped, but the PM should not be doing it.

## 5 — Unexplained

**5.1 `forge.log` went stale while a build ran inside `forge board`.**
The log showed nothing for over an hour while the database advanced normally. The original
diagnosis — that the board's worker does not flush — is wrong: `BoardCommand` passes a real
`FileLogSink` with `AutoFlush = true` to both the PM turn and the build loop. The symptom
was real, the cause is unknown. Reproduce before acting.

## Fixed

- **`add_dependency` accepted cycles** (2026-08-05). The Principal wrote `3 → 4` and
  `4 → 3` on HabitTracker and deadlocked the board: a dep is satisfied only by a `done`
  task, so neither became claimable, the loop drained instantly, and Resume looked broken.
  `TaskRepository.AddDependency` now refuses the closing edge, and the tool error names the
  existing path (`4 → 3`) so the Principal can revise the plan it is authoring — it has no
  tool to delete an edge. `DependencyChain` walks breadth-first over a visited set, so a
  database that already contains a cycle terminates too.

- **A finished project read "paused" and still offered Resume** (2026-08-05).
  `BoardQuery.ProjectState` required every milestone to be `done`, and `State(0,0,0)`
  returns `"pending"` — so the PM's two empty duplicate milestones (4.3) held HabitTracker
  short of complete after all 10 tasks shipped and it was delivered. Now skips milestones
  with no tasks, falling back to features when every milestone is empty.
