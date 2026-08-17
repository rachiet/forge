# Forge

Forge turns a plain-English idea into a working software project.

You describe what you want. A team of stateless LLM agents takes it from idea →
requirements → architecture → implementation → testing → a runnable git repo.

You only talk to one person on the team: **Iris**, Forge's project manager.

Iris understands your idea, asks questions, and captures the requirements. Once
you approve them, the rest of the team takes over:

- **Principal** designs the architecture and creates the task plan.
- **Engineer** agents write the code, each in an isolated workspace.
- **CI** builds, tests and opens the app in a browser, zero tokens spent.
- **Principal** reviews every diff and recovers tasks that get stuck.
- **QA** writes the acceptance suite and the harness runs it against the
  finished app.

The result is a real project you can clone, run, and keep developing.

---

## How Forge works

```mermaid
flowchart TD
    classDef orch fill:#eef2ff,stroke:#4338ca,stroke-width:1.5px,color:#312e81
    classDef harness fill:#ecfdf5,stroke:#047857,stroke-width:1.5px,color:#064e3b
    classDef store fill:#f8fafc,stroke:#64748b,stroke-width:1px,color:#334155,stroke-dasharray: 2 2

    YOU["You"]
    PM["PM<br/>chat, requirements"]:::orch
    PRIN1["Principal<br/>architecture + task plan"]:::orch
    E1["Engineer"]:::orch
    E2["Engineer"]:::orch
    E3["Engineer"]:::orch
    CI["CI<br/>build, test, render the page"]:::harness
    PRIN2["Principal<br/>review"]:::orch
    MERGE["Merge to trunk"]:::harness
    QA["QA<br/>writes the acceptance suite"]:::orch
    RUN["Harness runs the suite<br/>against the running app"]:::harness
    DONE[("Checked out, with<br/>the command to run it")]:::store

    YOU -->|chat| PM
    PM -->|requirements approved| PRIN1
    PRIN1 --> E1
    PRIN1 --> E2
    PRIN1 --> E3
    E1 --> CI
    E2 --> CI
    E3 --> CI
    CI -->|green| PRIN2
    CI -->|red| E1
    PRIN2 -->|approved| MERGE
    PRIN2 -->|changes requested| E1
    MERGE -->|board drained| QA
    QA --> RUN
    RUN -->|bugs| PRIN1
    RUN -->|passes| DONE
```

Green steps are the harness: ordinary code, no model, no tokens. What
"passed" means is read from git and process exit codes, never from what an
agent reports.

**PM**: the role you meet as Iris, and the only one you talk to. Understands
the idea, asks clarifying questions, writes the requirements, and puts them to
you for approval. Approving the requirements is what hands the
project to engineering. Later change requests go through the same chat.

**Principal**: owns technical execution, designing the structure and
contracts, breaks work into a task graph, reviews every engineer's diff, and
rescues any task that gets stuck.

**Engineer**: implements one task at a time in an isolated, jailed
workspace and submits it for review.

**QA**: once the board is drained, writes a black-box acceptance suite from
the contract and the requirements: HTTP tests for every operation, browser
tests for everything the interface is supposed to show. It never starts the
app itself; the harness does that, runs the suite against it, and files a
bug from a failing run with the real output attached. Bugs flow back through
the same build loop; the project isn't done until a round files nothing new.

---

## Why Forge is different

Most AI coding tools generate code. Forge is built to run an autonomous,
multi-agent build that stays honest and bounded. Detail on each of these
lives in [`ARCHITECTURE.md`](ARCHITECTURE.md).

- **Ground truth over agent claims**: merge, CI, and test state come from
  git and process output, never from what an agent says.
- **Mechanical CI**: `dotnet build`/`dotnet test` run as harness code, not
  an LLM call. The Principal never reviews a diff that fails to build.
- **One task state machine**: status drives who acts next; there's no
  separate "next actor" field that can drift out of sync.
- **Automatic recovery**: a stuck task escalates to the Principal, who
  redirects, splits, or takes it over directly.
- **Two budgets, two jobs**: a dollar project cap pauses the build for a
  money decision; a token task cap strikes a runaway agent. They're never
  conflated.
- **One LLM interface, three providers**: Claude, OpenAI, and Gemini are
  normalized behind one adapter. Recipes name a model tier, never a
  model id.
- **Appearance isn't generated**: Forge ships a component kit and a set of
  themes. You pick one from the board and it's installed immediately: free,
  instant, and reversible. No agent writes CSS.
- **Interfaces are verified by rendering them**: pages are opened in a real
  browser and measured, because markup can't tell you whether an element is
  visible, where it sits, or what colour it came out.
- **It asks you when it's genuinely stuck**: work that can't be resolved
  technically is put to you in the chat in plain language; your answer sends
  it back to the team and the build carries on by itself.

  <img src="docs/images/forge-06-needs-input.png" alt="Iris stopping the build to ask for input" width="30%">

  Nothing is lost when it stops: the task is left exactly as it stands, the
  provider's own words are folded away behind the details, and replying in the
  chat picks the build up where it left off.

---

## Installation

**Requirements:** .NET 8 SDK or newer (`dotnet --version`), and an API key
for Claude (default), OpenAI, or Gemini.

Projects with a user interface are checked in a real browser. The first run
that needs one downloads Chromium (~100MB) into `$FORGE_HOME/browsers`, once
per machine. A machine where it can't be installed skips those checks rather
than failing them.

**API key**: create `~/forge_env`:

```sh
ANTHROPIC_API_KEY=sk-ant-api-your-key-here   # default provider
# OPENAI_API_KEY=sk-...
# GEMINI_API_KEY=...
```

```sh
chmod 600 ~/forge_env
```

This is Forge's own credential file, separate from the encrypted secrets
vault used by generated projects (below). Agents never see it. Override the
path with `FORGE_ENV`.

**Data location**: all runtime data (databases, git repos, workspaces)
lives under one root, default `~/forge-data`, override with `FORGE_HOME`.
Client project data never lives inside the Forge source tree.

**Build:**

```sh
dotnet build -c Release
alias forge='dotnet /path/to/Forge/src/Forge.Cli/bin/Release/net8.0/Forge.Cli.dll'
```

---

## Getting started

### The dashboard

```sh
forge board                # http://localhost:5177, every project
forge board --port 8080
```

**1. Create the project.** A name, a hard cap in dollars, and a provider. The
cap is real: the build pauses when it's reached and asks you, rather than
spending on.

<img src="docs/images/forge-01-new-project.png" alt="Start a new project" width="50%">

**2. Describe what you want.** Iris opens the conversation. Plain
language is enough; she asks about anything ambiguous.

<img src="docs/images/forge-02-chat.png" alt="Chatting with Iris" width="50%">

**3. Approve the specification.** Iris turns your conversation into a full
requirements and specification document, then waits for you to review it.
Once you approve, you sit back and watch: the agents start building.

![Approving the specification](docs/images/forge-03-spec.png)

**4. Watch the agents work.** Approving starts the build, and from here it
runs itself. Milestones, features and spend per agent update as work lands,
every figure read from the token ledger rather than estimated. Check in
whenever you like, and pause or resume from the same page. When it's done,
the board tells you where the project is and how to run it.

![The project board](docs/images/forge-04-board.png)

**5. Run what it built.**

![The finished app](docs/images/forge-05-app.png)

### The CLI

The same pipeline, without the browser:

```sh
forge project init habittracker           # create it
forge chat habittracker                   # same PM conversation, in the terminal
forge run habittracker --loop             # build until the board drains, then QA
forge task list habittracker              # where everything stands
forge log habittracker --events           # every tool call, git op, transition
```

A run is autonomous but not opaque: every action is one logged line, and
`--task 3` narrows the trail to a single task.

The result is an ordinary git repo:

```sh
git clone "$FORGE_HOME/projects/habittracker/repo.git" habittracker
cd habittracker && dotnet run --project src/<ProjectName>
```

Only one build runs machine-wide at a time, so the dashboard and a terminal
`forge run` can't collide on the same database.

---

## What to build

Forge builds **.NET projects**: console tools and ASP.NET web apps, because
everything a project does must be verifiable from the CLI or over HTTP.

In the intake chat: describe the outcome, not the tech; say what you care
about (behaviour, edge cases); answer Iris's questions up front, because that
is where correctness is won. You're the reviewer of the requirements; once you
approve them, she hands off to engineering.

**How it looks is a setting, not a requirement.** Ask about appearance and
Iris opens a theme picker: every theme drawn in its own colours, with light
or dark, an accent, spacing and corner rounding beside it. Pick one and it's
applied straight away, before or after the build, as often as you like. So
spend the conversation on what the product must *do*: say "the three
columns sit side by side and each is a different colour", not "make it
elegant". A requirement nobody can check from the outside can't be built to
or tested against.

---

## Limitations

- .NET only: console and ASP.NET projects.
- One build runs machine-wide at a time.
- Change requests extend a project Forge already built; there's no import of
  an existing external codebase.

---

## Commands

| Command | What it does |
|---|---|
| `forge project init <name>` | Create a project's data directory, database, and git repo. |
| `forge chat <name> [-m MSG] [--history]` | Talk to the PM: intake, requirements, status. |
| `forge run <name> [--loop] [--project-budget N] [--task ID]` | Claim and run tasks; `--loop` drains the board autonomously, then runs QA. |
| `forge task list <name>` | Show the task board. |
| `forge task add <name> <title> [options]` | Add a task by hand. |
| `forge board [--port N]` | Serve the browser dashboard (default port 5177). |
| `forge log <name> [--events] [--task N] [--domain D]` | Replay the trail and token spend. |
| `forge prices show [--project NAME]` | Show provider pricing. |
| `forge prices update` | Force a price refresh. |
| `forge secrets set <name> [-d TEXT]` | Store a secret (prompted, hidden) in the encrypted vault. |
| `forge secrets list` | List stored secret names, never their values. |

---

## Repository layout

```
src/
 ├── Forge.Core        # orchestrator + harness: scheduler, agent loop, tools, DB
 └── Forge.Cli         # the forge command-line interface and the board's web page
prompts/
 ├── roles             # versioned per-role prompts
 ├── tasks             # versioned per-task-type prompts
 ├── plays             # what to attach when the harness detects a stuck situation
 └── templates
     ├── CONVENTIONS.md  # the base every generated project starts from
     └── ui              # the UI kit: components, themes, and the class catalogue
tests/
 └── Forge.Tests
```

- [`ARCHITECTURE.md`](ARCHITECTURE.md) describes how the agent loop, task state
  machine, provider adapters, and budget guardrails work in code.
- [`CLAUDE.md`](CLAUDE.md) states the rules a change to Forge must respect.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) states the contributor licence terms.

---

## License

Forge is licensed under the **GNU Affero General Public License v3.0**. The full
text is in [`LICENSE`](LICENSE). Read it, run it, fork it, learn from it, change
it: free. If you run a modified Forge as a network service, the AGPL requires you
to offer your users its source.

**Commercial licensing.** If you want to build on Forge commercially without
those obligations, a separate licence is available. Email
rachiet.sharma@gmail.com.

Contributions are accepted under the terms in
[`CONTRIBUTING.md`](CONTRIBUTING.md), which keep the project relicensable by a
single owner.

"Forge" and "Iris" are names of this project and are not part of the licence
grant. Please use your own name for anything you distribute or host.

© 2026 Rachiet Sharma
