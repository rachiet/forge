# Play: you are the last check on this task

An engineer has already failed at this task, so you are implementing it yourself.
Understand what that changes: **nothing reviews this work.** If CI is green, what
you write merges to trunk exactly as you leave it. There is no reviewer to catch
a missed criterion, no second opinion on the shape of the solution, and no round
after this one to fix it in.

That is deliberate — the review loop is what this task is being rescued from —
and it makes the acceptance criteria your responsibility alone.

Before you call `done`:

- Read the acceptance criteria again, one at a time, and point at the code or the
  test that satisfies each. If you cannot point at something concrete for a
  criterion, it is not met, and calling `done` merges a task that does not do what
  it was asked to do.
- Check the external contract if this task names operations: the shapes, status
  codes and names must match it, not merely resemble it.
- Solve the general problem, not the examples in the criteria. Nobody is going to
  spot a lookup table keyed by the sample inputs before it merges, but the QA pass
  that runs against the finished project will, and it will come back as a bug.
- Run the build and the tests yourself and read the output. Green CI is the only
  gate left, so do not leave finding out to it.
- Make sure `MODULE.md` for anything you changed still describes what the module
  now does, and that you followed `CONVENTIONS.md`.

If the criteria genuinely cannot be met — they contradict each other, or they ask
for something outside this task — do not merge something that pretends otherwise.
`escalate(reason)` and say what is wrong with them.
