# Forge

Forge builds software from a plain-English idea. You describe what you want to a
Project Manager in a chat; a Principal engineer turns that into a technical design
and a task plan; and a team of stateless LLM agents implements it — writing code,
running the build and tests, reviewing each other's work, and merging to a git repo
— until you have a working project you can run.

You talk to one role (the PM). Everything else happens on its own.

---

## How it works (30-second tour)

```
  you ──chat──▶ PM ──▶ requirements ──▶ Principal ──▶ design + task DAG
                                                          │
                                          approve ◀───────┘   (your sign-off)
                                                          │
                        ┌─────────────────────────────────▼─────────────────────┐
                        │  forge run --loop   (one autonomous worker)             │
                        │  Engineer writes code → CI builds & tests → Principal   │
                        │  reviews the diff → merge to trunk.  Stuck tasks are    │
                        │  triaged by the Principal automatically.                │
                        └─────────────────────────────────────────────────────────┘
```

- **PM** — the only role you talk to. Owns your requirements.
- **Principal** — designs the structure and contracts, breaks the work into tasks,
  reviews every diff, and rescues any task that gets stuck.
- **Engineer** — implements one task at a time in an isolated workspace.
- The harness runs `dotnet build` / `dotnet test` itself (zero tokens) and reads
  merge/CI state from **git and process output — never from what an agent claims**.

Generated code and its full docs live in a real git repo per project; you clone it
and run it like any other project.

---

## Prerequisites

- **.NET 8 SDK** or newer (`dotnet --version`).
- An **Anthropic account** with access to Claude models.

---

## Setup

### 1. An API key (required)

Forge runs on **Claude, OpenAI, or Gemini** — you need a key for one of them, in a
credentials file at **`~/forge_env`**:

```sh
# ~/forge_env  — Forge's own credentials, never shown to the agents it runs
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

You can also pin a specific model per tier — `fast`, `coding`, `reasoning` — instead
of taking the provider's defaults:

```json
{ "provider": "gemini",
  "models": { "reasoning": "gemini/gemini-2.5-pro", "coding": "gemini/gemini-2.5-flash" } }
```

`FORGE_LLM_PROVIDER=openai forge run …` overrides the file for one command. Run
**`forge prices show`** to confirm your provider and see the live rate for every
model it will use — Forge refuses to run a model it cannot price.

### 2. Where your projects live (optional)

All runtime data — databases, git repos, workspaces — lives under one root:

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

## Quickstart — build your first project

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

# 3. When the PM says the requirements are captured, run the design phase.
#    The Principal writes the structure, contracts, and a task plan.
forge design run mynotes

# 4. Sign off on the design — this releases the tasks to the board.
forge design approve mynotes

# 5. Build it. One autonomous worker drains the board; --project-budget is a
#    safety cap on total token spend.
forge run mynotes --loop --project-budget 1000000
```

When the board drains, your project is done. It lives in the bare git repo at
`$FORGE_HOME/projects/mynotes/repo.git`. Clone it and run it:

```sh
git clone "$FORGE_HOME/projects/mynotes/repo.git" mynotes-app
cd mynotes-app
dotnet run --project src/<ProjectName>      # for a web app, open the URL it prints
```

---

## Coming up with ideas

Forge builds **.NET projects** — console tools and ASP.NET web apps work best,
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
- **Say what you care about.** Look and feel, edge cases, what must never break —
  the PM will fold these into the requirements. (You can absolutely ask for an
  "elegant, handwritten, premium" UI and mean it.)
- **Answer the PM's questions.** It asks about the awkward cases up front — that is
  where correctness is won.
- **You are the reviewer.** `design approve` is your sign-off that the plan matches
  your intent; nothing gets built until you give it.

---

## Watching and inspecting a run

```sh
forge task list mynotes                 # the board: status, budget, progress notes
forge log  mynotes                      # the conversation and decision trail
forge log  mynotes --events             # every tool call, git op, and transition
forge log  mynotes --events --task 3    # just one task
forge log  mynotes --events --domain error
```

A run is autonomous, but not opaque — every action is one logged line.

## When a task gets stuck (it handles itself)

If a task exhausts its budget or turns, or an engineer gets on the wrong track, it
is **handed to the Principal**, who diagnoses the real cause and either redirects the
engineer with concrete guidance, breaks the task into smaller ones, or — as a last
resort — implements it directly. A transient provider error (a rate limit or
timeout) is parked and auto-resumed. You do not need to babysit the loop; run it
again if the process ever exits and it resumes exactly where it left off.

---

## Client secrets

If a generated project needs a secret (an API key, a connection string), store it in
the encrypted vault — agents only ever see the placeholder `{{secret:NAME}}`, and the
real value is substituted at execution time, never written to the database or logs:

```sh
forge secrets set STRIPE_KEY        # prompts for the value, hidden
forge secrets list                  # names only, never values
```

These are **the generated project's** secrets — separate from Forge's own credential
in `~/forge_env`, which agents never see at all.

---

## Command reference

| Command | What it does |
|---|---|
| `forge project init <name>` | Create a project's data directory, database, and git repo. |
| `forge chat <name> [-m MSG]` | Talk to the PM — intake, requirements, status. Your only interface. |
| `forge design run <name>` | Run the Principal to author the structure, contracts, and task plan. |
| `forge design approve <name>` | Your sign-off — releases the design's tasks to the board. |
| `forge run <name> [--loop] [--project-budget N] [--task ID]` | Claim and run tasks; `--loop` drains the board. |
| `forge task list <name>` | Show the task board. |
| `forge log <name> [--events] [--task N] [--domain D]` | Replay the trail and token spend. |
| `forge secrets set\|list` | Manage the encrypted secrets vault for a project. |

---

## Repository layout

- `src/Forge.Core` — the orchestrator and harness (scheduler, agent loop, tools, DB).
- `src/Forge.Cli` — the `forge` command-line interface.
- `prompts/roles`, `prompts/tasks` — the versioned agent prompts (edit a template
  here and every future task improves).
- `tests/Forge.Tests` — the test suite (`dotnet test`).
- `ORCHESTRATOR-SPEC.md` — the authoritative design document.
- `CLAUDE.md` — decisions settled after the spec.
