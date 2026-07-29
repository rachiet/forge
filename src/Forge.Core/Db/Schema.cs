namespace Forge.Core.Db;

/// <summary>
/// Schema DDL. CHECK constraints mirror the enums in Model/Enums.cs — keep both
/// layers in sync. Anything the harness must query or enforce is a real column;
/// LLM-only payloads (context_paths) may be JSON TEXT.
/// </summary>
public static class Schema
{
    /// <summary>Global forge.db: project registry + secret names. Values live in the vault, never here.</summary>
    public const string GlobalDdl = """
        CREATE TABLE IF NOT EXISTS projects (
          name TEXT PRIMARY KEY,
          token_budget INTEGER CHECK(token_budget IS NULL OR token_budget > 0),
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS secrets_registry (
          name TEXT PRIMARY KEY,
          description TEXT,
          provided_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    /// <summary>Per-project project.db: queue + board + ledger + audit log in one file.</summary>
    public const string ProjectDdl = """
        CREATE TABLE IF NOT EXISTS messages (
          id INTEGER PRIMARY KEY,
          thread_id INTEGER,
          from_agent TEXT NOT NULL,
          to_agent   TEXT NOT NULL,
          task_id INTEGER REFERENCES tasks(id),
          type TEXT NOT NULL CHECK(type IN ('question','answer','review','decision',
                                   'escalation','status','change_request','system_nudge')),
          payload TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','in_progress','done')),
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS ix_messages_queue ON messages(to_agent, status, created_at);

        CREATE TABLE IF NOT EXISTS tasks (
          id INTEGER PRIMARY KEY,
          milestone_id INTEGER REFERENCES milestones(id),
          -- The parent Feature this task decomposes (self-reference). NULL for a
          -- top-level task (a Feature itself, or a standalone task). Children the
          -- Principal creates from a Feature carry that Feature's id here.
          parent_id INTEGER REFERENCES tasks(id),
          type TEXT NOT NULL CHECK(type IN ('feature','task','bug','chore')),
          title TEXT NOT NULL CHECK(length(title) > 0),
          objective TEXT NOT NULL CHECK(length(objective) > 0),
          acceptance_criteria TEXT,
          context_paths TEXT,
          requirements_ref TEXT,
          assigned_role TEXT CHECK(assigned_role IS NULL OR assigned_role IN
            ('pm','principal','engineer','qa','researcher')),
          status TEXT NOT NULL DEFAULT 'created' CHECK(status IN
            ('created','ready','claimed','in_progress','in_review','merging',
             'qa','done','blocked','out_of_budget','triage','rejected','active','cancelled')),
          token_budget INTEGER NOT NULL CHECK(token_budget > 0),
          tokens_spent INTEGER NOT NULL DEFAULT 0 CHECK(tokens_spent >= 0),
          -- How many times this task ran out of resources (budget/iteration) after
          -- the forced last-turn message. At the second strike the harness stops
          -- handing it back to an engineer and the Principal implements it directly.
          out_of_budget_count INTEGER NOT NULL DEFAULT 0 CHECK(out_of_budget_count >= 0),
          progress_note TEXT,
          branch_name TEXT,
          created_by TEXT,
          created_at TEXT NOT NULL DEFAULT (datetime('now')),
          updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS task_deps (
          task_id INTEGER NOT NULL REFERENCES tasks(id),
          depends_on INTEGER NOT NULL REFERENCES tasks(id),
          PRIMARY KEY (task_id, depends_on)
        );

        -- Small key/value store for project-level orchestration state — currently the
        -- QA gate's watermark (how many bug-fixes QA has already verified), which is
        -- what stops the QA↔fix loop once a cycle accepts nothing new.
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
          tokens_in INTEGER NOT NULL CHECK(tokens_in >= 0),
          tokens_out INTEGER NOT NULL CHECK(tokens_out >= 0),
          created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS ix_ledger_task ON token_ledger(task_id);

        CREATE TABLE IF NOT EXISTS milestones (
          id INTEGER PRIMARY KEY,
          name TEXT NOT NULL,
          description TEXT,
          status TEXT NOT NULL DEFAULT 'planned'
            CHECK(status IN ('planned','active','demo_ready','accepted')),
          ordinal INTEGER NOT NULL
        );

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
