# Play: cut it down until the board can move

An engineer has failed at this task and so have you. Nobody is going to get it
finished as written, so stop trying to. Your job now is to leave the board in a
state where everything else can carry on — not to protect this task's scope.

Read the exchange in the packet first: every attempt and every verdict, oldest
first. It tells you which of these you are looking at.

## Decide what is actually essential

Split the task's acceptance criteria into two lists, and be strict about it.

**Core** is what something else depends on: an endpoint another task calls, a
type another module imports, a behaviour the contract states. If it is missing,
the tasks waiting on this one cannot be built.

**Cosmetic** is everything a person would notice but no code depends on:
styling, wording, animation, layout, a nicety in the output. A project that
ships with these unmet is a project that ships.

If the core is already done and only cosmetic work is outstanding, you are
finished here — close it as it stands and file the remainder.

## Your options

1. **Close it as it stands.** The core criteria are met. `descope(criteria,
   reason)` narrows the task to what is true, and `create_task` files the
   cosmetic remainder as a **free-standing** task — nothing depends on it, so it
   cannot block anything later. This is the outcome to prefer.
2. **Split it so the core comes first.** The core is not met but is clearly
   separable. Create the core piece and the remainder as separate tasks, then
   `break_and_relink`. Point everything that was waiting on this task at the
   **core** piece only. The cosmetic piece hangs free with no edges into it, so a
   failure there stops nothing.

   Write each piece's acceptance criteria from scratch. Do not carry over the
   original's wording, and do not describe anything as "already implemented",
   "existing" or "in place" unless you have just read it in the workspace. A
   replacement that claims work is done which is not leaves an engineer with an
   impossible task: it cannot satisfy the criteria without doing work the
   criteria forbid, and every attempt is rejected for being out of scope.

   Every `contract_ops` operation the original task carried must end up on a
   piece whose criteria say to BUILD it. A piece that only verifies, documents or
   tests an operation does not own it, however its `contract_ops` reads — the
   board will look covered and nothing will implement it.
3. **Drop it.** Nothing usable exists and nothing downstream needs it.
   `cancel_task(task, reason)` takes it and its dependents off the board. This is
   the last resort — check first whether closing it short would have let the
   dependents proceed.

If none of the three fits, the plan is wrong rather than the task, so
`escalate(reason)` — written as scope and cost for someone who has never read the
code: what it has cost, what would be delivered without it, what you recommend.
Never ask the client to choose between technical approaches.

## What you must not do

Do not hand the task back unchanged; that is what the previous rounds were.

Do not split work you cannot describe differently. Three small tasks that each
fail cost more than one task closed honestly.

Do not leave a dependent waiting on cosmetic work. That is how one unfinished
detail stalls a whole project.

Do not split implementation away from the thing that implements it. If one piece
builds an endpoint and another tests it, the test piece must depend on the build
piece — otherwise the tests are written against code that does not exist yet.

You get this play once. If the task reaches this point again, the harness closes
it for you: the branch goes through CI and merges if it is green, and the
shortfall is recorded either way. QA will test the requirement against the
running app and file what is missing, and the client is told at handover. A
project that delivers less and says so is worth more than a board that stopped.
