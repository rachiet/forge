using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Forge.Core.Db;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Tools;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Agents;

/// <summary>What executing one tool produced, and whether it ends the loop.</summary>
public sealed record ToolOutcome(string Observation, EndReason? End = null);

/// <summary>
/// The v1 tool surface (spec §4.1), bound to one agent's workspace. Which tools
/// exist is the recipe's business, not this class's — so a role gains or loses a
/// capability by editing data. Every path goes through the PathJail and the
/// role's PathScope, and every command through the ToolExecutor, so a model that
/// asks for something out of bounds gets a refusal as an observation rather than
/// an effect.
/// </summary>
public sealed class AgentToolset(
    ToolExecutor executor,
    IDbConnection connection,
    AgentRecipe recipe,
    TaskRecord? task = null,
    ForgeLogger? logger = null)
{
    private const int MaxObservationChars = 8_000;
    private const int DefaultReadLines = 400;
    private const int LogSummaryChars = 200;

    private readonly PathJail _jail = executor.Jail;
    private readonly TaskRepository _tasks = new(connection);
    private readonly MessageRepository _messages = new(connection);
    private readonly MilestoneRepository _milestones = new(connection);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    /// <summary>Last note the agent wrote, for the harness's end-of-run fallback.</summary>
    public string? LastProgressNote { get; private set; }

    /// <summary>What the agent last said to the client, when it is a chat role.</summary>
    public string? LastReply { get; private set; }

    /// <summary>The review verdict, when this run was a Principal review. Null until decided.</summary>
    public bool? ReviewApproved { get; private set; }

    /// <summary>The review's reason (on changes requested) or note (on approval).</summary>
    public string? ReviewFeedback { get; private set; }

    /// <summary>A rule the reviewer wants added to CONVENTIONS.md for a recurring mistake.</summary>
    public string? ReviewConvention { get; private set; }

    /// <summary>One line per tool, rendered into the prompt so docs cannot drift from code.</summary>
    public static readonly IReadOnlyDictionary<string, string> Catalogue =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_file"] = "read_file(path, [start], [end]) — read a file, optionally a line range.",
            ["list_dir"] = "list_dir([path]) — list a directory (defaults to the workspace root).",
            ["grep"] = "grep(pattern, [path]) — regex search across files.",
            ["write_file"] = "write_file(path, content) — create or overwrite a file, whole contents.",
            ["run"] = "run(command, [cwd]) — run one binary.",
            ["add_milestone"] = "add_milestone(name, [description], [ordinal]) — add a milestone to the plan.",
            ["create_task"] = "create_task(title, objective, [type], [acceptance], [requirements_ref], "
                            + "[context_paths], [budget], [milestone]) — put a task on the board. Returns its id. "
                            + "type is feature (default), bug, or chore. requirements_ref names the requirement "
                            + "file, e.g. `01-todos.md@v1` (version optional).",
            ["add_dependency"] = "add_dependency(task, depends_on) — task cannot start until depends_on is done.",
            ["redirect"] = "redirect(guidance, [budget]) — hand this stuck task back to the engineer with "
                         + "concrete direction (and optionally a new absolute token budget). Resets the "
                         + "attempt so the engineer starts fresh with your guidance. Ends your triage.",
            ["file_bug"] = "file_bug(title, repro, expected, actual, [requirements_ref]) — record a failure found "
                         + "in QA as a bug for the Principal to triage. Give exact repro steps, the expected "
                         + "result, and the actual result. Do not file a bug already in the ledger.",
            ["accept_bug"] = "accept_bug([note]) — this filed bug is real; release it to the board for an engineer. Ends your triage.",
            ["reject_bug"] = "reject_bug(reason) — this filed bug is not a real defect; reject it with a reason. "
                           + "It is kept on record and will not be re-filed. Ends your triage.",
            ["approve"] = "approve([note]) — the diff is good; approve it for merge and end your review.",
            ["request_changes"] = "request_changes(reason, [convention]) — send the work back with a reason. "
                                + "Set convention to add a permanent rule to CONVENTIONS.md for a recurring mistake.",
            ["reply"] = "reply(message) — say this to the client and end your turn.",
            ["progress_note"] = "progress_note(note) — save state for your successor.",
            ["done"] = "done(summary) — you believe the work is complete.",
            ["escalate"] = "escalate(reason) — you are blocked and need a human decision.",
        };

    public async Task<ToolOutcome> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        var outcome = await DispatchAsync(call, ct).ConfigureAwait(false);
        LogOutcome(call, outcome);
        return outcome;
    }

    private async Task<ToolOutcome> DispatchAsync(ToolCall call, CancellationToken ct)
    {
        if (!recipe.Tools.Contains(call.Name, StringComparer.Ordinal))
        {
            return new ToolOutcome(
                $"ERROR: no tool '{call.Name}' is available to you. " +
                $"Available: {string.Join(", ", recipe.Tools)}.");
        }

        try
        {
            return call.Name switch
            {
                "read_file" => ReadFile(call),
                "list_dir" => ListDir(call),
                "grep" => Grep(call),
                "write_file" => WriteFile(call),
                "run" => await RunAsync(call, ct).ConfigureAwait(false),
                "add_milestone" => AddMilestone(call),
                "create_task" => CreateTask(call),
                "add_dependency" => AddDependency(call),
                "redirect" => Redirect(call),
                "file_bug" => FileBug(call),
                "accept_bug" => AcceptBug(call),
                "reject_bug" => RejectBug(call),
                "approve" => Approve(call),
                "request_changes" => RequestChanges(call),
                "reply" => Reply(call),
                "progress_note" => ProgressNote(call),
                "done" => Done(call),
                "escalate" => Escalate(call),
                _ => new ToolOutcome($"ERROR: tool '{call.Name}' is not implemented."),
            };
        }
        // Refusals are observations, not crashes: the agent should see the boundary
        // and correct, exactly as it would see a compiler error.
        catch (ToolJailViolationException ex) { return new ToolOutcome($"REFUSED: {ex.Message}"); }
        // Any other tool failure — a malformed argument, a bad requirement ref, an
        // I/O error — is an observation the agent can correct, never a crash of the
        // run. Cancellation is the one exception: it means the harness is stopping.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolOutcome($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// One log line per tool call — the narrative of what the agent actually did.
    /// A refused or errored call is logged as tool.refused so the log shows the
    /// boundary being hit, not just successes.
    /// </summary>
    private void LogOutcome(ToolCall call, ToolOutcome outcome)
    {
        var summary = FirstLine(outcome.Observation);
        var refused = summary.StartsWith("REFUSED:", StringComparison.Ordinal)
                   || summary.StartsWith("ERROR:", StringComparison.Ordinal);
        var type = refused ? EventType.ToolRefused : EventTypes.ForTool(call.Name);
        if (type is { } eventType) _log.Event(eventType, summary);
    }

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
        return line.Length <= LogSummaryChars ? line : line[..LogSummaryChars] + "…";
    }

    /// <summary>Jail first (can it be reached?), then role scope (may this role reach it?).</summary>
    private string Resolve(string relativePath)
    {
        var full = _jail.Resolve(relativePath);
        var relative = _jail.Relative(full);
        if (!recipe.Scope.Allows(relative))
            throw new ToolJailViolationException(
                $"'{relative}' is outside your role's scope ({recipe.Scope.Describe()}).");
        return full;
    }

    private ToolOutcome ReadFile(ToolCall call)
    {
        var path = Resolve(call.Arg("path"));
        if (!File.Exists(path)) return new ToolOutcome($"ERROR: no such file '{call.Arg("path")}'.");

        var lines = File.ReadAllLines(path);
        var start = Math.Max(1, call.OptionalInt("start") ?? 1);
        var end = Math.Min(lines.Length, call.OptionalInt("end") ?? start + DefaultReadLines - 1);

        var sb = new StringBuilder($"{_jail.Relative(path)} (lines {start}-{end} of {lines.Length}):\n");
        for (var i = start; i <= end; i++) sb.Append(i).Append('\t').AppendLine(lines[i - 1]);
        if (end < lines.Length) sb.Append($"... {lines.Length - end} more lines; read again with start={end + 1}.");
        return new ToolOutcome(Truncate(sb.ToString()));
    }

    private ToolOutcome ListDir(ToolCall call)
    {
        var dir = Resolve(call.Optional("path") ?? ".");
        if (!Directory.Exists(dir)) return new ToolOutcome($"ERROR: no such directory '{call.Optional("path") ?? "."}'.");

        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Where(e => Path.GetFileName(e) != ".git")
            .Where(e => recipe.Scope.Allows(_jail.Relative(e) + (Directory.Exists(e) ? "/" : "")))
            .OrderBy(e => e, StringComparer.Ordinal)
            .Select(e => Directory.Exists(e) ? $"{_jail.Relative(e)}/" : _jail.Relative(e));
        return new ToolOutcome(Truncate(string.Join('\n', entries) is { Length: > 0 } s ? s : "(nothing in scope here)"));
    }

    private ToolOutcome Grep(ToolCall call)
    {
        var pattern = call.Arg("pattern");
        var root = Resolve(call.Optional("path") ?? ".");
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return new ToolOutcome($"ERROR: bad regex '{pattern}': {ex.Message}"); }

        var files = File.Exists(root)
            ? [root]
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .Where(f => recipe.Scope.Allows(_jail.Relative(f)));

        var hits = new StringBuilder();
        var count = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length && count < 200; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                hits.AppendLine($"{_jail.Relative(file)}:{i + 1}: {lines[i].Trim()}");
                count++;
            }
        }
        return new ToolOutcome(count == 0 ? $"No matches for '{pattern}'." : Truncate(hits.ToString()));
    }

    private ToolOutcome WriteFile(ToolCall call)
    {
        var path = Resolve(call.Arg("path"));
        var content = call.Args.TryGetValue("content", out var c) ? c : "";
        // The tag protocol eats the layout newline before </arg>; restore the
        // conventional trailing newline so files are not written without one.
        if (content.Length > 0 && !content.EndsWith('\n')) content += "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        File.WriteAllText(path, content);
        var lineCount = content.Count(ch => ch == '\n');
        return new ToolOutcome($"{(existed ? "Overwrote" : "Wrote")} {_jail.Relative(path)} ({lineCount} lines).");
    }

    private async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
    {
        var command = call.Arg("command");
        var result = await executor.RunAsync(command, call.Optional("cwd"), ct: ct).ConfigureAwait(false);

        var sb = new StringBuilder($"$ {command}\nexit code: {result.ExitCode}");
        if (result.TimedOut) sb.Append(" (TIMED OUT — process killed)");
        if (result.Stdout.Length > 0) sb.Append("\n--- stdout ---\n").Append(result.Stdout.TrimEnd());
        if (result.Stderr.Length > 0) sb.Append("\n--- stderr ---\n").Append(result.Stderr.TrimEnd());
        return new ToolOutcome(Truncate(sb.ToString()));
    }

    /// <summary>The milestone plan is a real table, not prose in a markdown file the harness can't query.</summary>
    private ToolOutcome AddMilestone(ToolCall call)
    {
        var name = call.Arg("name");
        var ordinal = call.OptionalInt("ordinal") ?? _milestones.NextOrdinal();
        var milestone = _milestones.Insert(new MilestoneRecord
        {
            Name = name,
            Description = call.Optional("description"),
            Ordinal = ordinal,
        });
        return new ToolOutcome($"Milestone {milestone.Id} recorded: #{ordinal} {name}.");
    }

    /// <summary>
    /// The Principal breaks the design into board tasks (spec §7). Tasks are born
    /// `created`, not `ready`: nothing is claimable until the client signs off on
    /// the design, which is the gate that flips them to ready. A malformed packet
    /// (empty title/objective, budget ≤ 0, bad requirement ref) is refused by the
    /// factory and comes back as an ERROR the Principal can correct.
    /// </summary>
    private ToolOutcome CreateTask(ToolCall call)
    {
        var requirement = call.Optional("requirements_ref") is { } reqRef
            ? NormalizeRequirementRef(reqRef)
            : (RequirementsRef?)null;

        var contexts = call.Optional("context_paths")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        var created = _tasks.Insert(TaskRecord.Create(
            call.Optional("type") is { } type ? ParseRunnableTaskType(type) : TaskType.Feature,
            call.Arg("title"),
            call.Arg("objective"),
            call.OptionalInt("budget") ?? 60_000,
            acceptanceCriteria: call.Optional("acceptance"),
            contextPaths: contexts,
            requirementsRef: requirement,
            milestoneId: call.OptionalInt("milestone") is { } m ? m : null,
            assignedRole: AgentRole.Engineer,
            createdBy: SnakeCaseEnum.ToSnakeCase(recipe.Role)));

        return new ToolOutcome($"Task {created.Id} created: {created.Title} " +
            $"(created — the client's sign-off makes it ready).");
    }

    /// <summary>
    /// Only types with a prompts/tasks/ template can be claimed and run; any other
    /// TaskType value would put a task on the board that crashes the runner at
    /// spin-up (PromptLibrary throws on the missing Layer B file). Refuse it here,
    /// where the Principal can read the error and pick a runnable type instead.
    /// </summary>
    private static TaskType ParseRunnableTaskType(string type)
    {
        var parsed = SnakeCaseEnum.Parse<TaskType>(type);
        return parsed is TaskType.Feature or TaskType.Bug or TaskType.Chore
            ? parsed
            : throw new ToolCallException(
                $"Task type '{type}' cannot be assigned to an engineer. Use feature, bug, or chore.");
    }

    /// <summary>
    /// The canonical requirement ref is <c>file.md@vN</c>, but a model will often
    /// reach for the natural path (<c>docs/requirements/01-todos.md</c>) or omit
    /// the version. Meet it there: strip any directory, and when the version is
    /// missing, read it from the requirement file's "Version: N" line rather than
    /// rejecting the whole task over a formatting nicety.
    /// </summary>
    private RequirementsRef NormalizeRequirementRef(string reqRef)
    {
        var text = reqRef.Trim();
        var at = text.IndexOf('@');
        var file = System.IO.Path.GetFileName(at >= 0 ? text[..at] : text);
        if (file.Length == 0) throw new ToolCallException($"Empty requirement ref '{reqRef}'.");

        if (at >= 0) return RequirementsRef.Parse($"{file}{text[at..]}");

        var version = ReadRequirementVersion(file) ?? 1;
        return new RequirementsRef(file, version);
    }

    private int? ReadRequirementVersion(string file)
    {
        try
        {
            var path = _jail.Resolve(System.IO.Path.Combine("docs", "requirements", file));
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var match = Regex.Match(line, @"[Vv]ersion:\s*(\d+)");
                if (match.Success) return int.Parse(match.Groups[1].Value);
            }
        }
        catch { /* best effort — fall back to the default version */ }
        return null;
    }

    /// <summary>A DAG edge: the task waits on its dependency. The serial worker respects it (spec §7).</summary>
    /// <summary>
    /// The Principal's triage verdict: hand a blocked/out-of-budget task back to the
    /// engineer with direction. Resets the spend so the redirected attempt starts
    /// fresh (a new directed try, not a continuation), optionally with a new budget,
    /// and re-readies the task. Ends the triage instance.
    /// </summary>
    private ToolOutcome Redirect(ToolCall call)
    {
        if (task is null) return new ToolOutcome("ERROR: redirect needs a task; this run has none.");
        var guidance = call.Arg("guidance");

        var current = _tasks.Get(task.Id).Status;
        if (current is not (TaskStatus.OutOfBudget or TaskStatus.Blocked))
            return new ToolOutcome(
                $"ERROR: redirect only applies to a blocked/out-of-budget task; task {task.Id} is {SnakeCaseEnum.ToSnakeCase(current)}.");

        var budget = call.OptionalInt("budget");
        if (budget is { } b) _tasks.SetBudget(task.Id, b);
        _tasks.ResetTokensSpent(task.Id);

        var note = $"PRINCIPAL GUIDANCE (triage): {guidance}";
        _tasks.SetProgressNote(task.Id, note);
        LastProgressNote = note;
        _tasks.Transition(task.Id, TaskStatus.Ready);

        return new ToolOutcome(
            $"Task {task.Id} redirected to the engineer with new guidance" +
            (budget is { } nb ? $" and budget {nb}" : "") + "; ready again.",
            EndReason.Done);
    }

    /// <summary>
    /// QA records a failure as a bug for the Principal to triage. The bug is born in
    /// `triage` (not on the engineer's board yet) and carries structured repro /
    /// expected / actual so a triager and, later, a fixer both know exactly what broke.
    /// </summary>
    private ToolOutcome FileBug(ToolCall call)
    {
        var requirement = call.Optional("requirements_ref") is { } reqRef
            ? NormalizeRequirementRef(reqRef)
            : (RequirementsRef?)null;

        var objective = $"""
            A defect found in QA. Reproduce it, then make the expected result true and pin it with a test.

            ## Repro
            {call.Arg("repro")}

            ## Expected
            {call.Arg("expected")}

            ## Actual
            {call.Arg("actual")}
            """;

        var bug = _tasks.Insert(TaskRecord.Create(
            TaskType.Bug,
            call.Arg("title"),
            objective,
            call.OptionalInt("budget") ?? 60_000,
            acceptanceCriteria: "Reproducing the steps no longer yields the actual behaviour; the expected result holds.",
            requirementsRef: requirement,
            assignedRole: AgentRole.Engineer,
            createdBy: SnakeCaseEnum.ToSnakeCase(recipe.Role)) with { Status = TaskStatus.Triage });

        return new ToolOutcome($"Bug {bug.Id} filed: {bug.Title} (triage — the Principal decides).");
    }

    /// <summary>The Principal's verdict: this filed bug is real. Release it to the board for an engineer.</summary>
    private ToolOutcome AcceptBug(ToolCall call)
    {
        if (task is null) return new ToolOutcome("ERROR: accept_bug needs a bug to act on; this run has none.");
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Triage)
            return new ToolOutcome(
                $"ERROR: accept_bug only applies to a bug in triage; task {task.Id} is {SnakeCaseEnum.ToSnakeCase(current)}.");

        _tasks.Transition(task.Id, TaskStatus.Ready);
        new DiscussionRepository(connection).Open(task.Id, SnakeCaseEnum.ToSnakeCase(recipe.Role),
            $"[bug accepted] {call.Optional("note") ?? "Accepted for fixing."}");
        LastProgressNote = "Bug accepted; ready for an engineer to fix.";
        return new ToolOutcome($"Bug {task.Id} accepted; released to the board.", EndReason.Done);
    }

    /// <summary>The Principal's verdict: not a real defect. Kept on record with the reason; never re-filed.</summary>
    private ToolOutcome RejectBug(ToolCall call)
    {
        if (task is null) return new ToolOutcome("ERROR: reject_bug needs a bug to act on; this run has none.");
        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Triage)
            return new ToolOutcome(
                $"ERROR: reject_bug only applies to a bug in triage; task {task.Id} is {SnakeCaseEnum.ToSnakeCase(current)}.");

        var reason = call.Arg("reason");
        _tasks.Transition(task.Id, TaskStatus.Rejected);
        _tasks.SetProgressNote(task.Id, $"REJECTED (not a bug): {reason}");
        new DiscussionRepository(connection).Open(task.Id, SnakeCaseEnum.ToSnakeCase(recipe.Role), $"[bug rejected] {reason}");
        LastProgressNote = $"Bug rejected: {reason}";
        return new ToolOutcome($"Bug {task.Id} rejected and kept on record; QA will not re-file it.", EndReason.Done);
    }

    private ToolOutcome AddDependency(ToolCall call)
    {
        var taskId = call.OptionalInt("task") ?? throw new ToolCallException("add_dependency needs 'task'.");
        var dependsOn = call.OptionalInt("depends_on")
            ?? throw new ToolCallException("add_dependency needs 'depends_on'.");
        _tasks.AddDependency(taskId, dependsOn);
        return new ToolOutcome($"Task {taskId} now depends on task {dependsOn}.");
    }

    /// <summary>Review verdict: the diff is good. The harness reads the verdict and merges.</summary>
    private ToolOutcome Approve(ToolCall call)
    {
        ReviewApproved = true;
        ReviewFeedback = call.Optional("note");
        return new ToolOutcome("Approved for merge.", EndReason.Done);
    }

    /// <summary>
    /// Review verdict: send it back. The reason reaches the engineer; an optional
    /// convention is the self-improving loop (spec §7) — the harness appends it to
    /// CONVENTIONS.md so the same mistake is ruled out for every future task.
    /// </summary>
    private ToolOutcome RequestChanges(ToolCall call)
    {
        ReviewApproved = false;
        ReviewFeedback = call.Arg("reason");
        ReviewConvention = call.Optional("convention");
        return new ToolOutcome("Changes requested; sending back to the engineer.", EndReason.Done);
    }

    /// <summary>
    /// The client-facing turn. Recorded as a message so the conversation survives
    /// the instance that produced it — chat history lives in the DB, never in a
    /// transcript held in memory (Principle 2).
    /// </summary>
    private ToolOutcome Reply(ToolCall call)
    {
        var text = call.Arg("message");
        _messages.Insert(Message.Create(
            MessageType.Answer, SnakeCaseEnum.ToSnakeCase(recipe.Role), "client", text, task?.Id));
        LastReply = text;
        return new ToolOutcome("Delivered to the client.", EndReason.Done);
    }

    /// <summary>
    /// Persisted immediately, not at end of run: the note is the only thing a
    /// fresh instance inherits after a kill, so it must survive one.
    /// </summary>
    private ToolOutcome ProgressNote(ToolCall call)
    {
        var note = call.Arg("note");
        if (task is null) return new ToolOutcome("ERROR: progress_note needs a task; this run has none.");
        _tasks.SetProgressNote(task.Id, note);
        LastProgressNote = note;
        return new ToolOutcome("Progress note saved.");
    }

    private ToolOutcome Done(ToolCall call)
    {
        var summary = call.Arg("summary");
        if (task is not null) _tasks.SetProgressNote(task.Id, summary);
        LastProgressNote = summary;
        return new ToolOutcome("Work reported complete; the harness will verify and merge.", EndReason.Done);
    }

    private ToolOutcome Escalate(ToolCall call)
    {
        var reason = call.Arg("reason");
        var from = SnakeCaseEnum.ToSnakeCase(recipe.Role);
        // Escalation climbs one rung: engineer/qa/researcher → principal (the author
        // of the DAG and contracts, who can re-scope or re-budget), principal → pm
        // (only a requirements/scope question that needs the client), pm → client.
        var to = recipe.Role switch
        {
            AgentRole.Pm => "client",
            AgentRole.Principal => "pm",
            _ => "principal",
        };
        _messages.Insert(Message.Create(MessageType.Escalation, from, to, reason, task?.Id));
        if (task is not null) _tasks.SetProgressNote(task.Id, $"Escalated: {reason}");
        LastProgressNote = $"Escalated: {reason}";
        return new ToolOutcome($"Escalation sent to the {to}; stopping here.", EndReason.Escalated);
    }

    /// <summary>Tool output is untrusted and unbounded; the context window is not.</summary>
    private static string Truncate(string text) =>
        text.Length <= MaxObservationChars
            ? text
            : text[..MaxObservationChars] + $"\n... [truncated {text.Length - MaxObservationChars} chars]";
}
