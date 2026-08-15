namespace Forge.Core.Db;

/// <summary>
/// The DDL for both databases. CHECK constraints mirror the enums in Model/Enums.cs, and both
/// layers are kept in sync by hand. Anything the harness queries or enforces is a real column;
/// payloads only an agent reads may be JSON TEXT.
/// </summary>
public static class Schema
{
    /// <summary>The global forge.db: the project registry and secret names, never secret values.</summary>
    public const string GlobalDdl = """
        -- A bare registry; per-project settings live in that project's own project_meta.
        CREATE TABLE IF NOT EXISTS projects (
          name TEXT PRIMARY KEY,
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS secrets_registry (
          name TEXT PRIMARY KEY,
          description TEXT,
          provided_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    /// <summary>The tasks table alone, so <see cref="Migrations"/> can rebuild it.</summary>
    public const string TasksDdl = """
        CREATE TABLE IF NOT EXISTS tasks (
          id INTEGER PRIMARY KEY,
          -- The Feature this task belongs to. NULL on a Feature itself or a standalone task.
          parent_id INTEGER REFERENCES tasks(id),
          -- The phase of the plan this task belongs to, named by the Principal at create_task.
          -- NULL on a Feature, and on work created before any milestone was named.
          milestone_id INTEGER REFERENCES milestones(id),
          type TEXT NOT NULL CHECK(type IN ('feature','task','bug','chore')),
          title TEXT NOT NULL CHECK(length(title) > 0),
          -- The same task in the client's words, shown on the progress board. The title is
          -- an identifier for engineers; this is the sentence a non-technical reader sees.
          display_name TEXT,
          objective TEXT NOT NULL CHECK(length(objective) > 0),
          acceptance_criteria TEXT,
          context_paths TEXT,
          requirements_ref TEXT,
          -- JSON array of operationIds from the project's OpenAPI contract that this task
          -- implements. Null for work with no HTTP surface. Every operation in the contract
          -- must appear in some task's list; an id here that the contract does not define
          -- is refused at create_task.
          contract_ops TEXT,
          assigned_role TEXT CHECK(assigned_role IS NULL OR assigned_role IN
            ('pm','principal','engineer','qa','researcher')),
          status TEXT NOT NULL DEFAULT 'created' CHECK(status IN
            ('created','ready','claimed','in_progress','in_review','merging',
             'qa','done','stalled','triage','rejected','active',
             'needs_human','cancelled')),
          token_budget INTEGER NOT NULL CHECK(token_budget > 0),
          tokens_spent INTEGER NOT NULL DEFAULT 0 CHECK(tokens_spent >= 0),
          -- How many times this task has stalled: a spent budget or turn cap, an agent that
          -- gave up, or an attempt allowance used up. At the second the Principal implements
          -- it instead of the engineer; past that it is cut down.
          stall_count INTEGER NOT NULL DEFAULT 0 CHECK(stall_count >= 0),
          -- How many splits deep this task is: 0 if the Principal planned it, 1 if it
          -- replaced a task that was too big. break_and_relink refuses above its cap.
          split_depth INTEGER NOT NULL DEFAULT 0 CHECK(split_depth >= 0),
          progress_note TEXT,
          branch_name TEXT,
          created_by TEXT,
          created_at TEXT NOT NULL DEFAULT (datetime('now')),
          updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    /// <summary>A project's project.db: its queue, board, ledger and audit log in one file.</summary>
    public const string ProjectDdl = $"""
        CREATE TABLE IF NOT EXISTS messages (
          id INTEGER PRIMARY KEY,
          from_agent TEXT NOT NULL,
          to_agent   TEXT NOT NULL,
          task_id INTEGER REFERENCES tasks(id),
          type TEXT NOT NULL CHECK(type IN ('question','answer','review','decision',
                                   'escalation','status','change_request','system_nudge')),
          payload TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','received')),
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS ix_messages_queue ON messages(to_agent, status, created_at);

        -- The phases the Principal breaks a Feature into, in the order it first named them.
        -- What the client reads as the plan: prose names, one row per phase, no status column
        -- (a milestone's state is derived from the tasks pointing at it).
        CREATE TABLE IF NOT EXISTS milestones (
          id INTEGER PRIMARY KEY,
          -- Shown to the client verbatim, so it is written in plain words.
          name TEXT NOT NULL CHECK(length(name) > 0),
          -- Where this phase sits in the plan; assigned from the order names first appear.
          position INTEGER NOT NULL,
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_milestones_name ON milestones(name);

        {TasksDdl}

        CREATE TABLE IF NOT EXISTS task_deps (
          task_id INTEGER NOT NULL REFERENCES tasks(id),
          depends_on INTEGER NOT NULL REFERENCES tasks(id),
          PRIMARY KEY (task_id, depends_on)
        );

        -- Key/value store for project-level orchestration state: the QA watermark and round
        -- count, the project's settings, and the handover flag.
        CREATE TABLE IF NOT EXISTS project_meta (
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS discussions (
          id INTEGER PRIMARY KEY,
          task_id INTEGER NOT NULL REFERENCES tasks(id),
          parent_id INTEGER REFERENCES discussions(id),
          author TEXT NOT NULL,
          body TEXT NOT NULL,
          file_path TEXT,
          line_number INTEGER,
          status TEXT NOT NULL DEFAULT 'open' CHECK(status IN ('open','resolved')),
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS token_ledger (
          id INTEGER PRIMARY KEY,
          agent_instance_id TEXT NOT NULL,
          role TEXT NOT NULL CHECK(role IN ('pm','principal','engineer','qa','researcher')),
          task_id INTEGER REFERENCES tasks(id),
          model TEXT NOT NULL,
          -- The four buckets a provider reports separately. tokens_in is the UNCACHED prompt
          -- remainder, so the whole prompt is tokens_in + cache_read + cache_write. All four
          -- are stored: each is priced differently, and keeping them makes cost recomputable.
          tokens_in INTEGER NOT NULL CHECK(tokens_in >= 0),
          tokens_out INTEGER NOT NULL CHECK(tokens_out >= 0),
          cache_read_tokens INTEGER NOT NULL DEFAULT 0 CHECK(cache_read_tokens >= 0),
          cache_write_tokens INTEGER NOT NULL DEFAULT 0 CHECK(cache_write_tokens >= 0),
          -- USD × 1e-9, as an integer so SUM() stays exact across thousands of rows.
          cost_nanos INTEGER NOT NULL DEFAULT 0 CHECK(cost_nanos >= 0),
          -- Which price snapshot produced cost_nanos.
          priced_with TEXT,
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS ix_ledger_task ON token_ledger(task_id);
        CREATE INDEX IF NOT EXISTS ix_ledger_role ON token_ledger(role);

        CREATE TABLE IF NOT EXISTS agent_instances (
          id TEXT PRIMARY KEY,
          role TEXT NOT NULL CHECK(role IN ('pm','principal','engineer','qa','researcher')),
          model TEXT NOT NULL,
          task_id INTEGER REFERENCES tasks(id),
          started_at TEXT NOT NULL DEFAULT (datetime('now')),
          ended_at TEXT,
          end_reason TEXT CHECK(end_reason IS NULL OR end_reason IN
            ('done','budget','iterations','crash','escalated'))
        );
        """;
}
