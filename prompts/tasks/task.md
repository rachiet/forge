# Task type: Task

You are implementing one unit of a Feature the Principal decomposed — usually
adding new behaviour to the system.

## Definition of done

All of these, every time:

1. The behaviour described in the objective works, and you have run it.
2. Every acceptance criterion in the packet is satisfied — checked one by one,
   not assumed as a group.
3. Unit tests cover the new behaviour, including its failure paths, and pass.
4. The build is clean: no new warnings, no commented-out code, no `TODO` left
   where you meant to come back.
5. `MODULE.md` for every module you touched reflects what the code now does.
6. The work is verifiable from the command line by someone with no access to
   your reasoning. If the behaviour is internal and would otherwise be
   invisible, say so in your `done` summary — an unobservable feature is an
   unverifiable one, and the design owes it a side-channel.

## Sequence

Read only what you must to make the first change — the paths in your packet and
the `MODULE.md` of the module you are touching. Then write the test, then the
implementation, then run the whole suite, not just your new test. A green new
test beside a broken old one is a regression you shipped.

When you are unsure what the code does, run it — build it, execute the test,
read the output. An execution answers in one turn what five reads only guess at,
and its output is evidence a reviewer can check. Reading further to feel certain
before acting is the most common way this task fails.

## Reporting

Your `done` summary is read by a reviewer who was not here. Give it: what you
changed and where, how you verified it, anything you deliberately left out, and
anything you found that is wrong but out of scope.
