# Proposal: the Principal splits a task it cannot land

Status: proposed, not implemented. Written 2026-08-05.

## The problem

A task sized too large for one agent burns the whole strike ladder and ends up
parked on the client, who can only answer the question "what do you want done
about it?" — a question they are the worst-placed person to answer.

The ladder today (`TaskRunner.TriageOrImplementAsync`, `DirectImplementStrike = 2`):

1. The engineer exhausts its budget. `ParkOutOfBudget` (line 1049) records strike 1
   and sets `out_of_budget`.
2. The loop hands it to `TriageAsync`. The Principal redirects with guidance; the
   task returns to `ready`.
3. The engineer dies again — strike 2.
4. `TaskRunner.cs:152` fires `ImplementDirectlyAsync`: the Principal implements it
   itself, on the implementer recipe with a raised budget.
5. That exhausts too — strike 3. `task.OutOfBudgetCount > DirectImplementStrike`
   sends it to `GiveUp` → `needs_human`, parked on the client.

Every rung retries *the same task at the same size*. Nothing on the ladder makes
the work smaller, so a task that is simply too big is guaranteed to reach the
client, having spent three full agent instances proving it.

## What already works

`AgentRecipe.PrincipalTriage` carries `create_task` and `add_dependency`, and
`TaskRunner.ReleaseTriageSubtasks` (line 328) adopts anything created during a
triage under the stuck task, gives it the parent's milestone, and releases it to
`ready`. The Principal can already break the work up. What it cannot do is retire
the parent afterwards.

## Why the parent is the hard part

`PrincipalTriage`'s only exits are `redirect`, `escalate`, `accept_bug`,
`reject_bug`. So after decomposing, the Principal has two choices and both are wrong:

- **Redirect** — the parent returns to `ready` and an engineer picks up the same
  oversized task again, now duplicating the children that were just created.
- **Do nothing** — the status stays `out_of_budget`, and `TriageAsync`'s line 312
  sends it to `GiveUp`. The decomposition happened, but the client is still asked
  about a task that no longer needs them.

**Cancelling the parent is not the answer.** `OutOfBudget → Cancelled` is legal in
the transition map, but `cancel_task` cancels every transitive dependent, and an
oversized task is exactly the kind other tasks depend on. It would take the
downstream work with it, and the new children would not inherit those edges.
Cancelling without the cascade is worse: a dependency edge is satisfied only by a
`done` task, so the dependents would wait on a cancelled task forever.

That constraint decides the design: **the parent must eventually reach `done`.**

## The design

Add a `split` verdict to triage. The parent stops being a unit of work and becomes
a container: it goes to `active`, its children do the work, and the existing sweep
closes it to `done` when they all finish.

This is the shape decomposition already uses. A Feature sits `active` while its
children build, and `CloseFinishedFeatures` (line 267) closes it once every child
is terminal. Dependents unblock naturally, because the container reaching `done` is
exactly what a dependency edge waits for. No edges are rewritten and nothing cascades.

### Changes

1. **`split(reason)` on `PrincipalTriage`** — mirrors `Redirect` (AgentToolset.cs:466):
   validates the task is under triage, writes the reason as the progress note,
   transitions the parent to `active`, ends the instance with `EndReason.Done`.

2. **Guard: refuse a split that creates nothing.** The toolset counts `create_task`
   calls for the instance and `split` refuses below two — the same
   "no execution, no verdict" rule `file_bug` enforces. This is load-bearing, not
   tidiness: the sweep requires `EXISTS (a child)`, so a childless parent left in
   `active` would sit in no queue and never close. Splitting into one task is a
   redirect with extra steps and is refused for the same reason.

3. **Legal transition `OutOfBudget → Active`.** Currently absent
   (`TaskRecord.cs:117`). `Blocked → Active` and `Triage → Active` too, since
   triage is reachable from both.

4. **Generalise the sweep.** `ActiveFeaturesReadyToClose`
   (`TaskRepository.cs:272`) filters `type = 'feature'`; drop that filter so it
   closes any `active` task whose children are all terminal. Feature rows are
   `active` too, so existing behaviour is unchanged. Rename to
   `ActiveParentsReadyToClose`.

5. **Release the parent's workspace.** The parent will never be implemented, so its
   task workspace should be deleted on split. Leave the branch: it is unmerged and
   harmless, and it may hold partial work worth reading. Note that the children
   start from trunk, so that partial work is otherwise lost — if it is worth
   keeping, the Principal should say so in the children's objectives.

6. **Prompt** — `prompts/tasks/` triage guidance gains the rule: if the task failed
   because it is too large rather than because the engineer went astray, split it
   instead of redirecting. Redirect is for a wrong approach; split is for too much work.

### Where it sits on the ladder

Split is a verdict the Principal chooses, not a rung the harness forces, so it is
available from strike 1. That is deliberate — the earliest useful moment to split
is the first triage, before two more instances have been spent proving the size.

## Consequences

**The board.** The parent stays `type = 'task'`, so `BoardQuery.Features()` — which
selects every `type = 'feature'` row regardless of parent — does not pick it up.
This matters: retyping the parent to a Feature was the obvious alternative, and it
would have listed the container as its own top-level feature card *while its cost
was also counted in its parent Feature's subtree*, double-counting it and breaking
the reconciliation property the page must never lose.

The accepted cost of not retyping: `SubtreeCost` is one level deep
(`t2.id = t.id OR t2.parent_id = t.id`), so the children of a split parent are
grandchildren of the original Feature and fall into the "work outside features"
bucket. Money still reconciles — nothing is lost or counted twice — but attribution
is coarser than it could be. Making `SubtreeCost` recursive would fix it and is a
separate change.

**QA.** `BoardQuiescent` counts anything not `done`/`rejected`/`cancelled`, so an
`active` container holds QA back until its children finish. Correct as-is.

**Milestones.** The container counts toward its milestone's total until it closes,
then toward done. Correct as-is.

**A child that also fails** runs its own strike ladder and can itself be split. The
parent stays `active` until every child is terminal, so a child parked on the client
holds the container open — which is right: the work is genuinely unfinished.

## Tests

- Split moves the parent to `active`, leaves the children `ready`, and the parent
  is offered to no queue.
- The sweep closes the parent once every child is `done`; a dependent of the parent
  becomes claimable at that point.
- Split is refused with fewer than two tasks created in the instance, and the task
  is left exactly as it was.
- A child that is cancelled counts as terminal and does not hold the parent open.
- Existing Feature decomposition and close still behave identically after the
  sweep filter is dropped.
