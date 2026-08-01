# Forge

Forge builds software from a plain-English idea. A team of stateless LLM agents,
each playing a named role, does the work: the **PM** turns your idea into
requirements in a chat, and the **Principal** turns those into a technical design
and task plan. From there the rest of the team builds it, writing code, running
the build and tests, reviewing each other's work, and merging to a git repo,
until you have a working project you can run.

You talk to one role (the PM). Everything else happens on its own.

---

## How it works (30-second tour)

```
  you ──chat──▶ PM ──▶ requirements ──▶ create_feature (PM's handoff, no other
                                                          command needed)
                                                          │
                        ┌─────────────────────────────────▼─────────────────────┐
                        │  forge run --loop   (one autonomous worker)             │
                        │  Principal designs the structure + task DAG →           │
                        │  Engineer writes code → CI builds & tests → Principal   │
                        │  reviews the diff → merge to trunk.  Stuck tasks are     │
                        │  triaged by the Principal automatically.                │
                        └─────────────────────────────────────────────────────────┘
                                                          │
                                        board drains ─────┘
                                                          │
                                                          ▼
                              QA black-box tests the running project, files a bug
                              per unmet requirement, and bugs flow back through the
                              same build loop until QA passes. Then it's done.
```

- **PM**: the only role you talk to. Owns your requirements, and hands the built
  project over to engineering by calling `create_feature` once you've signed off
  in chat.
- **Principal**: designs the structure and contracts, breaks the work into tasks,
  reviews every diff, and rescues any task that gets stuck.
- **Engineer**: implements one task at a time in an isolated workspace.
- **QA**: once the board is fully drained, exercises the project's CLI/HTTP
  contract like a client would and files a bug for anything that doesn't meet a
  requirement; a project isn't "done" until a QA round finds nothing to file.
- The harness runs `dotnet build` / `dotnet test` itself (zero tokens) and reads
  merge/CI state from **git and process output, never from what an agent claims**.

Generated code and its full docs live in a real git repo per project; you clone it
and run it like any other project. Once it's built, you can go back to the PM at
any time with a change request. It's the same PM chat and the same
`create_feature` handoff, but the Principal writes an impact analysis and only
the delta gets built.

---

## Key design choices

The interesting engineering isn't the chat UI, it's what keeps an autonomous,
multi-agent build honest and bounded. A few decisions carry that weight (full
technical detail in [`ARCHITECTURE.md`](ARCHITECTURE.md)):

- **Ground truth over agent claims.** Merge, CI and test state are read from git
  and process output, never believed from what an agent says. An engineer that
  reports "done" is *checked*: no commits ahead of trunk means nothing merges,
  regardless of what it claims.
- **Zero-token CI.** The harness runs `dotnet build`/`dotnet test` itself, as
  trusted mechanical code, not a jailed agent call, and not an LLM call. The
  Principal never reviews a diff that fails to build, so review tokens are
  never spent on code that was never going to pass anyway.
- **One state machine, no "next actor" column.** Every task status change goes
  through a single legal-transition map (`TaskTransitions`); who acts on a
  task is *derived* from its status via a static role map, never stored
  separately, so routing and status can never drift out of sync with each
  other.
- **A harness-run, count-based triage ladder, not model diligence.** A stuck
  task escalates to the Principal, who redirects, splits, or (after a second
  strike) implements it directly; a project isn't "done" until a QA round
  files nothing new, guaranteed by a watermark that counts finished work, not
  by trusting an agent to stop looping.
- **Two budgets, two units, two failure modes.** A dollar project cap
  (exact, priced from real token usage) *pauses* the build with nothing struck
  when it's spent, since that's a client money decision. A token-based
  per-task cap (deliberately approximate) *strikes* the task and hands it to
  the Principal as a runaway-agent guard. Conflating the two was the earlier
  failure mode: one exhausted project cap used to strike every remaining task
  on the board.
- **Three-layer prompt assembly, not per-task prompt blobs.** System prompts
  are built at spin-up from a versioned role file plus a task-type file plus
  the task's own DB columns, never stored as a blob on the task row. Fixing a
  template improves every future task of that type; the tool list an agent is
  told about is generated from its actual allowlist, so the two can't drift
  apart.
- **One interface (`ILlmClient`), three normalised adapters.** Claude, OpenAI
  and Gemini report token usage in three incompatible shapes (what counts as
  "cached," whether reasoning tokens are separate from output); each adapter
  normalises its provider's quirks so the rest of Forge (the ledger, the
  pricer, the budget nudge) sees one consistent meaning for every number.
  Recipes name a model *tier*, never a model id.

## Prerequisites

- **.NET 8 SDK** or newer (`dotnet --version`).
- An API key for **Claude (default), OpenAI, or Gemini**.

---

## Setup

### 1. An API key (required)

Forge runs on **Claude, OpenAI, or Gemini**; you need a key for one of them, in a
credentials file at **`~/forge_env`**:

```sh
# ~/forge_env  - Forge's own credentials, never shown to the agents it runs
ANTHROPIC_API_KEY=sk-ant-api-your-key-here   # default provider
# OPENAI_API_KEY=sk-...
# GEMINI_API_KEY=...
```

Then lock it down: `chmod 600 ~/forge_env`.

These stay Forge's own: the tool executor builds a scrubbed environment for any
command an agent runs, so a generated project never sees them. Point Forge at a
different file with the `FORGE_ENV` environment variable.

Claude is the default. To use another provider, create `<data root>/llm.json`:

```json
{ "provider": "openai" }
```

You can also pin a specific model per tier (`fast`, `coding`, `reasoning`) instead
of taking the provider's defaults:

```json
{ "provider": "gemini",
  "models": { "reasoning": "gemini/gemini-2.5-pro", "coding": "gemini/gemini-2.5-flash" } }
```

`FORGE_LLM_PROVIDER=openai forge run …` overrides the file for one command. Run
**`forge prices show`** to confirm your provider and see the live rate for every
model it will use; Forge refuses to run a model it cannot price.

### 2. Where your projects live (optional)

All runtime data (databases, git repos, workspaces) lives under one root:

- Default: `~/forge-data`
- Override with the `FORGE_HOME` environment variable (e.g.
  `export FORGE_HOME=~/Projects/ForgeHome`).

Client project data never lives inside the Forge source tree.

### 3. Build the CLI

```sh
dotnet build -c Release
```

The examples below use `forge` as the command. Define it once for your shell:

```sh
alias forge='dotnet /path/to/Forge/src/Forge.Cli/bin/Release/net8.0/Forge.Cli.dll'
```

(or run `dotnet run --project src/Forge.Cli -- <args>` directly.)

---

## Quickstart: build your first project

```sh
# 1. Create the project
forge project init mynotes

# 2. Describe your idea to the PM. It will ask clarifying questions; answer them.
#    Open an interactive session:
forge chat mynotes
#    …or send single messages:
forge chat mynotes -m "Build me a sticky-note web app: one board, add/edit/delete
notes, pick a color, drag to reposition, everything saved between restarts. Make
the UI elegant and handwritten-feeling."

# 3. When the PM says the requirements are captured, it hands the project to
#    engineering itself (no command from you needed) and everything from here
#    is autonomous: the Principal designs the structure and task plan, an
#    engineer builds each task, and QA checks the finished project against
#    every requirement, filing and routing its own fixes until nothing's left.
#    --project-budget is a safety cap on total token spend.
forge run mynotes --loop --project-budget 1000000
```

Prefer a browser? `forge board` serves a page at `localhost:5177` where you can
create a project, chat with the PM, and start/stop the build, no terminal needed
after that. See [Watching and inspecting a run](#watching-and-inspecting-a-run).

When `forge run --loop` reports the project accepted, it's done. It lives in the
bare git repo at `$FORGE_HOME/projects/mynotes/repo.git`. Clone it and run it:

```sh
git clone "$FORGE_HOME/projects/mynotes/repo.git" mynotes-app
cd mynotes-app
dotnet run --project src/<ProjectName>      # for a web app, open the URL it prints
```

---

## Coming up with ideas

Forge builds **.NET projects**. Console tools and ASP.NET web apps work best,
because everything a project does must be verifiable from the CLI or over HTTP
(that is how the harness proves the work is real). A few that map well:

- A **CLI tool**: a bill splitter, a habit tracker, a unit converter, a markdown
  table formatter.
- A **web app with a UI**: a sticky-note board, a URL shortener, a poll/voting app,
  a guest book, a kanban board.

Tips for the intake chat, where the quality of your idea turns into quality of
output:

- **Describe the outcome, not the tech.** "I want to split a group's expenses and
  see who owes whom" beats "use a greedy settlement algorithm." The Principal makes
  the technical calls.
- **Say what you care about.** Look and feel, edge cases, what must never break:
  the PM will fold these into the requirements. (You can absolutely ask for an
  "elegant, handwritten, premium" UI and mean it.)
- **Answer the PM's questions.** It asks about the awkward cases up front; that is
  where correctness is won.
- **You are the reviewer of the requirements.** Confirm them carefully with the
  PM in chat — once you do, it hands the project straight to engineering.

---

## Watching and inspecting a run

```sh
forge task list mynotes                 # the board: status, budget, progress notes
forge log  mynotes                      # the conversation and decision trail
forge log  mynotes --events             # every tool call, git op, and transition
forge log  mynotes --events --task 3    # just one task
forge log  mynotes --events --domain error
```

A run is autonomous, but not opaque: every action is one logged line.

### Or watch it from the browser

```sh
forge board                     # serves every project at http://localhost:5177
forge board --port 8080         # pick a different port
```

One page for all your projects: create a new one, chat with the PM, watch
milestones/features and spend per agent, and start or stop the build, all
without a terminal. Only one build runs machine-wide at a time, so the board's
Start button and a terminal `forge run` can't collide on the same database.

## When a task gets stuck (it handles itself)

If a task exhausts its budget or turns, or an engineer gets on the wrong track, it
is **handed to the Principal**, who diagnoses the real cause and either redirects the
engineer with concrete guidance, breaks the task into smaller ones, or, as a last
resort, implements it directly. A transient provider error (a rate limit or
timeout) is parked and auto-resumed. You do not need to babysit the loop; run it
again if the process ever exits and it resumes exactly where it left off.

---

## Client secrets

If a generated project needs a secret (an API key, a connection string), store it in
the encrypted vault. Agents only ever see the placeholder `{{secret:NAME}}`, and the
real value is substituted at execution time, never written to the database or logs:

```sh
forge secrets set STRIPE_KEY        # prompts for the value, hidden
forge secrets list                  # names only, never values
```

These are **the generated project's** secrets, separate from Forge's own credential
in `~/forge_env`, which agents never see at all.

---

## Command reference

| Command | What it does |
|---|---|
| `forge project init <name>` | Create a project's data directory, database, and git repo. |
| `forge chat <name> [-m MSG]` | Talk to the PM: intake, requirements, status. Your primary interface. |
| `forge run <name> [--loop] [--project-budget N] [--task ID]` | Claim and run tasks; `--loop` drains the whole board autonomously, including design, then runs QA. |
| `forge task list <name>` | Show the task board. |
| `forge task add <name> <title> [options]` | Put a task on the board by hand (the Principal normally does this for you). |
| `forge board [--port N]` | Serve the browser progress page for every project (default port 5177). |
| `forge log <name> [--events] [--task N] [--domain D]` | Replay the trail and token spend. |
| `forge prices show [--project NAME]` | Show the price table's age and the live rate for every model a provider will use. |
| `forge prices update` | Force a price refresh, ignoring the cache TTL. |
| `forge secrets set\|list` | Manage the encrypted secrets vault for a project. |

---

## Repository layout

- `src/Forge.Core`: the orchestrator and harness (scheduler, agent loop, tools, DB).
- `src/Forge.Cli`: the `forge` command-line interface.
- `prompts/roles`, `prompts/tasks`: the versioned agent prompts (edit a template
  here and every future task improves).
- `tests/Forge.Tests`: the test suite (`dotnet test`).
- `ORCHESTRATOR-SPEC.md`: the authoritative design document.
- `CLAUDE.md`: decisions settled after the spec.
- `ARCHITECTURE.md`: how the agent loop, task triage, provider adapters, and
  budget guardrails actually work in code.
