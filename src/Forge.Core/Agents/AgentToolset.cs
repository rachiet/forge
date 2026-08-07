using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using static Forge.Core.Agents.ToolDoc;
using Forge.Core.Db;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Tools;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Agents;

/// <summary>What executing one tool produced, and whether it ends the loop.</summary>
public sealed record ToolOutcome(string Observation, EndReason? End = null)
{
    /// <summary>
    /// Whether the call was rejected rather than performed: an unavailable tool, a jail or
    /// scope violation, or a malformed argument. A tool that ran and reported failure — a
    /// non-zero `run` exit — is not refused.
    /// </summary>
    public bool Refused =>
        Observation.AsSpan().TrimStart() is var text
        && (text.StartsWith("REFUSED:", StringComparison.Ordinal)
         || text.StartsWith("ERROR:", StringComparison.Ordinal));
}

/// <summary>
/// Implements every tool, bound to one agent's workspace. Which of them the agent may call is
/// the recipe's business, checked on dispatch. Every path goes through the PathJail and the
/// role's PathScope, and every command through the ToolExecutor, so an out-of-bounds call
/// comes back as a refusal rather than an effect.
/// </summary>
public sealed partial class AgentToolset(
    ToolExecutor executor,
    IDbConnection connection,
    AgentRecipe recipe,
    TaskRecord? task = null,
    ForgeLogger? logger = null)
{
    /// <summary>How much of one observation is shown to the agent.</summary>
    private const int MaxObservationChars = 8_000;

    /// <summary>How many lines read_file returns when given no range.</summary>
    private const int DefaultReadLines = 400;

    /// <summary>How much of a tool call or its result appears on a log line.</summary>
    private const int LogSummaryChars = 200;

    private readonly PathJail _jail = executor.Jail;
    private readonly TaskRepository _tasks = new(connection);
    private readonly MessageRepository _messages = new(connection);
    private readonly MilestoneRepository _milestones = new(connection);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    /// <summary>The last note the agent wrote, used as the resume note when the run ends.</summary>
    public string? LastProgressNote { get; private set; }

    /// <summary>What the agent last said to the client, when it is a chat role.</summary>
    public string? LastReply { get; private set; }

    /// <summary>The review verdict; null until the reviewer decides, and on any non-review run.</summary>
    public bool? ReviewApproved { get; private set; }

    /// <summary>The reviewer's reason for changes, or its approval note.</summary>
    public string? ReviewFeedback { get; private set; }

    /// <summary>A rule the reviewer asked to append to CONVENTIONS.md, if it gave one.</summary>
    public string? ReviewConvention { get; private set; }

    /// <summary>Why a bug was rejected, set when one was at triage or in review.</summary>
    public string? RejectedBugReason { get; private set; }

    /// <summary>
    /// Every tool the harness implements, with its summary and arguments. Rendered into the
    /// prompt from a recipe's tool list, and the list every recipe is validated against. The
    /// QA-only tools are documented beside their implementation in QaTools.cs and merged here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ToolDoc> Catalogue = QaCatalogue.Concat(
        new Dictionary<string, ToolDoc>(StringComparer.Ordinal)
        {
            ["read_file"] = new("read a file, optionally a line range.",
                Required("path", "the file to read, relative to your workspace root."),
                Optional("start", "first line to read (1-based). Defaults to the start of the file."),
                Optional("end", "last line to read. Defaults to a few hundred lines from `start`.")),

            ["list_dir"] = new("list a directory.",
                Optional("path", "the directory to list. Defaults to your workspace root.")),

            ["grep"] = new("regex search across files.",
                Required("pattern", "the regular expression to search for."),
                Optional("path", "file or directory to search. Defaults to your workspace root.")),

            ["write_file"] = new("create or overwrite a file.",
                Required("path", "where to write, relative to your workspace root "
                               + "(e.g. `src/App/Program.cs`). Missing directories are created."),
                Required("content", "the ENTIRE new contents. It replaces the file, so a partial "
                                  + "body deletes everything you left out.")),

            ["run"] = new("run one binary and see its exit code and output. No shell.",
                Required("command", "the binary and its arguments, e.g. `dotnet build`."),
                Optional("cwd", "directory to run in, relative to your workspace root. Defaults to the root.")),

            ["add_milestone"] = new("add a milestone to the plan. Returns its id, which create_task "
                                  + "and propose_requirements take.",
                Required("name", "what the client will see, e.g. `Core API`."),
                Optional("description", "one line on what is demonstrable when it is reached."),
                Optional("ordinal", "position in the sequence. Defaults to the end.")),

            ["propose_requirements"] = new(
                "present the finished requirements to the client for approval. They see Approve & "
              + "start building, or keep talking to you. Approving is what opens the Feature and "
              + "starts the build; nothing reaches engineering until then.",
                Required("title", "what is being built, in the client's words."),
                Required("objective", "what must be true when it is done."),
                Optional("acceptance", "how the client would check it from the outside."),
                Optional("requirements_ref", "the requirement file it covers, e.g. `01-todos.md@v1`."),
                Optional("milestone", "a milestone id, for a CHANGE REQUEST only. Pass none for the "
                                    + "initial build: it spans the whole plan, and a Feature-level "
                                    + "milestone would drag every task under one heading.")),

            ["create_task"] = new("put a task on the board. Returns its id.",
                Required("title", "short imperative name, e.g. `implement-create-poll-endpoint`."),
                Required("objective", "what this task must achieve, specific enough to work from."),
                Required("acceptance", "what must be observably true when it is done. The reviewer "
                                     + "judges the diff against this, so a task without it can never "
                                     + "be finished. If you cannot say what done looks like, the task "
                                     + "is not ready to create."),
                Optional("type", "`task` (default), `bug`, or `chore`."),
                Optional("requirements_ref", "the requirement it implements, e.g. `01-todos.md@v1`."),
                Optional("context_paths", "comma-separated files worth reading first."),
                Optional("contract_ops", "comma-separated operationIds from the OpenAPI contract this "
                                       + "task implements. The engineer is handed exactly those "
                                       + "operations. Omit for work with no HTTP surface."),
                Optional("budget", "token cap for one agent on this task. Defaults to 60000."),
                Optional("milestone", "milestone id, so the client can see progress per milestone.")),

            ["add_dependency"] = new("make one task wait for another. Dependencies flow one way: an "
                                   + "edge that would close a cycle is refused.",
                Required("task", "the id of the task that must wait."),
                Required("depends_on", "the id it waits for. That task must be `done` before this one "
                                     + "can be claimed.")),

            ["break_and_relink"] = new(
                "replace this stuck task with the smaller tasks you have just created. The harness "
              + "re-points everything that waited on it at all of them, gives them its dependencies, "
              + "files them under its feature and milestone, and cancels it. Use when the task is too "
              + "big to finish, not when the engineer took a wrong turn.",
                Required("new_tasks", "comma-separated ids from create_task, at least two."),
                Optional("reason", "why it had to be split, for the record.")),

            ["redirect"] = new("hand this stuck task back to the engineer with direction. Resets the "
                             + "attempt so it starts fresh with your guidance. Ends your triage.",
                Required("guidance", "concrete direction: what to do differently, not encouragement."),
                Optional("budget", "a new ABSOLUTE token budget, if it genuinely needed more room.")),

            ["file_bug"] = new(
                "record a failure as a bug for the Principal to triage. The harness attaches whatever "
              + "you last did to the running project — the run(), serve() or http() exchange — and its "
              + "real output as the evidence, so perform the check that shows the failure IMMEDIATELY "
              + "before calling this. Nothing checked = refused.",
                Required("title", "the defect in one line."),
                Required("expected", "what should have happened, quoted from the contract."),
                Optional("requirements_ref", "the requirement it violates, e.g. `01-todos.md@v1`.")),

            ["how_to_run"] = new("record the command that starts the app for the client. Call it once, "
                               + "after you have actually started the app.",
                Required("command", "exactly as you ran it. The harness refuses any command you did not."),
                Optional("url", "the URL it serves on, if it has one.")),

            ["accept_bug"] = new("this filed bug is real; release it to the board for an engineer. "
                               + "Ends your triage.",
                Optional("note", "what the fix needs to address.")),

            ["reject_bug"] = new("this bug is not a real defect; reject it. Kept on record, never "
                               + "re-filed. Use instead of looping a fix for a non-bug.",
                Required("reason", "why it is not a defect. This is the durable verdict."),
                Optional("task", "the bug's id. Only the PM passes this, to close one the client "
                               + "reviewed; at triage or review it acts on the bug in front of you.")),

            ["retriage_bug"] = new("send a bug back to the Principal for another triage with the "
                                 + "client's guidance attached. Use when the client says a flagged bug "
                                 + "needs more investigation rather than rejection.",
                Required("task", "the bug's id."),
                Required("note", "the client's guidance, in their terms.")),

            ["retriage_task"] = new("send a task the client answered back to the Principal, with their "
                                  + "guidance attached and the attempt counter reset. The build resumes "
                                  + "on it by itself.",
                Required("task", "the task's id."),
                Required("note", "what the client said to do.")),

            ["cancel_task"] = new("drop a task the client does not want done. Its branch is deleted and "
                                + "anything depending on it is cancelled too, so tell them what else "
                                + "goes with it BEFORE you call this.",
                Required("task", "the task's id."),
                Required("reason", "why it is being dropped.")),

            ["approve"] = new("the diff is good; approve it for merge and end your review.",
                Optional("note", "what you checked and why it passes.")),

            ["request_changes"] = new("send the work back to the engineer with a reason. Ends your review.",
                Required("reason", "what is wrong and what would fix it. The engineer resumes with this."),
                Optional("convention", "a permanent rule appended to CONVENTIONS.md. Use for a mistake "
                                     + "worth ruling out for every future engineer, not a one-off.")),

            ["reply"] = new("say this to the client and end your turn.",
                Required("message", "plain language, no jargon. This is what they read.")),

            ["progress_note"] = new("save state for your successor. A fresh instance resumes from this "
                                  + "and the workspace, and nothing else.",
                Required("note", "what is done, what is left, what you tried that failed, and the "
                               + "exact next action.")),

            ["done"] = new("you believe the work is complete. The harness verifies it.",
                Required("summary", "what you did, in plain language.")),

            ["escalate"] = new("you are blocked and need a human decision. Ends your run.",
                Required("reason", "what you are blocked on and what decision is needed.")),
        }).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

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
                "propose_requirements" => ProposeRequirements(call),
                "create_task" => CreateTask(call),
                "add_dependency" => AddDependency(call),
                "break_and_relink" => BreakAndRelink(call),
                "redirect" => Redirect(call),
                "file_bug" => FileBug(call),
                "how_to_run" => HowToRun(call),
                "accept_bug" => AcceptBug(call),
                "reject_bug" => RejectBug(call),
                "retriage_bug" => RetriageBug(call),
                "retriage_task" => RetriageTask(call),
                "cancel_task" => CancelTask(call),
                "approve" => Approve(call),
                "request_changes" => RequestChanges(call),
                "reply" => Reply(call),
                "progress_note" => ProgressNote(call),
                "done" => Done(call),
                "escalate" => Escalate(call),
                // QA's own tools (serve/stop_server/http) dispatch from QaTools.cs, which also
                // answers for anything unknown. The recipe gate above already refused every name
                // this role does not have, so nothing reaches here that should not.
                _ => await QaToolAsync(call, ct).ConfigureAwait(false),
            };
        }
        // Refusals come back as observations, so the agent sees the boundary and can correct.
        catch (ToolJailViolationException ex) { return new ToolOutcome($"REFUSED: {ex.Message}"); }
        // Any other failure is also an observation. Cancellation is not: the harness is stopping.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ToolOutcome($"ERROR: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs one line per tool call, under the tool's own event type or tool.refused when the
    /// call was rejected.
    /// </summary>
    private void LogOutcome(ToolCall call, ToolOutcome outcome)
    {
        var summary = FirstLine(outcome.Observation);
        if (!outcome.Refused)
        {
            if (EventTypes.ForTool(call.Name) is { } toolEvent) _log.Event(toolEvent, summary);
            return;
        }

        // A refusal carries a snippet of the call that caused it, so the log shows what was
        // actually emitted and not only what the harness wanted.
        var call_ = Collapse(call.Raw);
        _log.Event(EventType.ToolRefused,
            call_.Length == 0 ? summary : $"{summary} | emitted: {call_}");
    }

    /// <summary>One line, whitespace-collapsed, capped at <see cref="LogSummaryChars"/>.</summary>
    private static string Collapse(string raw)
    {
        var text = WhitespaceRun().Replace(raw, " ").Trim();
        return text.Length <= LogSummaryChars ? text : text[..LogSummaryChars] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
        return line.Length <= LogSummaryChars ? line : line[..LogSummaryChars] + "…";
    }

    /// <summary>Resolves a path inside the jail, then checks it against the role's scope.</summary>
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
        // The tag protocol eats the newline before </arg>; restore the trailing one.
        if (content.Length > 0 && !content.EndsWith('\n')) content += "\n";

        // The contract is refused at the point of writing, so the author can fix it in this run.
        if (ContractRejection(path, content) is { } rejection) return new ToolOutcome(rejection);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existed = File.Exists(path);
        File.WriteAllText(path, content);
        var lineCount = content.Count(ch => ch == '\n');
        return new ToolOutcome($"{(existed ? "Overwrote" : "Wrote")} {_jail.Relative(path)} ({lineCount} lines).");
    }

    /// <summary>
    /// Validates a write to the OpenAPI contract path and returns why it is refused, or null to
    /// let it through. Every other path is written unchecked.
    /// </summary>
    private string? ContractRejection(string absolutePath, string content)
    {
        var relative = _jail.Relative(absolutePath).Replace('\\', '/');
        if (!relative.Equals(Design.ApiContract.Path, StringComparison.OrdinalIgnoreCase)) return null;

        var (_, errors) = Design.ApiContract.Validate(content);
        if (errors.Count == 0) return null;

        return $"REFUSED: {relative} was not written — it is not a usable contract.\n"
             + string.Join("\n", errors.Select(e => $"  - {e}"))
             + "\n\nThe harness reads this document: tasks name its operationIds and QA tests "
             + "against them, so it must parse and every operation must be identifiable. "
             + "Fix these and write it again.";
    }

    private async Task<ToolOutcome> RunAsync(ToolCall call, CancellationToken ct)
    {
        var command = call.Arg("command");
        var result = await executor.RunAsync(command, call.Optional("cwd"), ct: ct).ConfigureAwait(false);

        var sb = new StringBuilder($"$ {command}\nexit code: {result.ExitCode}");
        if (result.TimedOut) sb.Append(" (TIMED OUT — process killed)");
        if (result.Stdout.Length > 0) sb.Append("\n--- stdout ---\n").Append(result.Stdout.TrimEnd());
        if (result.Stderr.Length > 0) sb.Append("\n--- stderr ---\n").Append(result.Stderr.TrimEnd());

        // Records the command and its real output as the evidence file_bug attaches verbatim.
        _lastRunTrace = sb.ToString();
        _ranCommands.Add(command);
        return new ToolOutcome(Truncate(sb.ToString()));
    }

    /// <summary>The most recent observation the harness captured, attached by file_bug.</summary>
    private string? _lastRunTrace;

    /// <summary>Commands this instance really executed; how_to_run accepts only these.</summary>
    private readonly HashSet<string> _ranCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// Tasks create_task made in this instance; break_and_relink accepts only these as
    /// replacements, so a split cannot retire a task in favour of pre-existing ones.
    /// </summary>
    private readonly HashSet<long> _createdTaskIds = [];

    /// <summary>Adds a milestone row to the plan and returns its id.</summary>
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
    /// Puts a task on the board, born `created` and released to `ready` by the caller that
    /// decomposed the Feature. Refuses a malformed packet, or a contract_ops id the contract
    /// does not define, as an ERROR the Principal can correct.
    /// </summary>
    private ToolOutcome CreateTask(ToolCall call)
    {
        var requirement = call.Optional("requirements_ref") is { } reqRef
            ? NormalizeRequirementRef(reqRef)
            : (RequirementsRef?)null;

        var contexts = call.Optional("context_paths")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        var ops = call.Optional("contract_ops")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        // An id nobody defined would sit outside every gate: no packet slice for the
        // engineer, and an operation left uncovered while a task claims to cover it.
        if (ops.Length > 0)
        {
            if (Design.ApiContract.Load(_jail.Root) is not { } contract)
                return new ToolOutcome(
                    $"ERROR: this project has no contract at {Design.ApiContract.Path}, so there are "
                    + "no operationIds to name. Write the contract first, or omit contract_ops.");

            if (contract.Unknown(ops) is { Count: > 0 } unknown)
                return new ToolOutcome(
                    $"ERROR: the contract defines no operation called {string.Join(", ", unknown)}. "
                    + $"Available: {string.Join(", ", contract.OperationIds)}.");
        }

        var created = _tasks.Insert(TaskRecord.Create(
            call.Optional("type") is { } type ? ParseRunnableTaskType(type) : TaskType.Task,
            call.Arg("title"),
            call.Arg("objective"),
            call.OptionalInt("budget") ?? 60_000,
            acceptanceCriteria: call.Arg("acceptance"),
            contextPaths: contexts,
            requirementsRef: requirement,
            milestoneId: call.OptionalInt("milestone") is { } m ? m : null,
            assignedRole: AgentRole.Engineer,
            createdBy: SnakeCaseEnum.ToSnakeCase(recipe.Role),
            contractOps: ops));

        _createdTaskIds.Add(created.Id);
        return new ToolOutcome($"Task {created.Id} created: {created.Title} " +
            $"(created — the client's sign-off makes it ready).");
    }

    /// <summary>
    /// Stages the Feature the client is being asked to approve. The Feature row is
    /// created by <see cref="Board.RequirementsProposal.Approve"/> when they accept.
    /// </summary>
    private ToolOutcome ProposeRequirements(ToolCall call)
    {
        var proposal = new Board.RequirementsProposal(
            call.Arg("title"),
            call.Arg("objective"),
            call.Optional("acceptance"),
            call.Optional("requirements_ref") is { } reqRef ? NormalizeRequirementRef(reqRef).ToString() : null,
            call.OptionalInt("milestone"));
        proposal.Save(connection);

        return new ToolOutcome(
            $"Proposed to the client for approval: {proposal.Title}. They now see " +
            "Approve & start building, or can keep talking to you. Tell them what you " +
            "have written and ask them to approve it.");
    }

    /// <summary>
    /// Engineer-runnable types only. A Feature is the PM's parent unit that the
    /// Principal decomposes — it is never handed to an engineer — and any other value
    /// has no prompts/tasks/ template, so it would crash the runner at spin-up
    /// (PromptLibrary throws on the missing Layer B file). Refuse it here, where the
    /// Principal can read the error and pick a runnable type instead.
    /// </summary>
    private static TaskType ParseRunnableTaskType(string type)
    {
        var parsed = SnakeCaseEnum.Parse<TaskType>(type);
        return parsed is TaskType.Task or TaskType.Bug or TaskType.Chore
            ? parsed
            : throw new ToolCallException(
                $"Task type '{type}' cannot be assigned to an engineer. Use task, bug, or chore.");
    }

    /// <summary>
    /// Parses a requirement ref, accepting the forms a model tends to write: any directory
    /// prefix is stripped, and a missing version is read from the file's "Version: N" line.
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

    /// <summary>
    /// Hands a task being triaged back to the engineer with guidance, optionally with a new
    /// budget, and returns it to `ready`. Ends the triage instance.
    /// </summary>
    private ToolOutcome Redirect(ToolCall call)
    {
        if (task is null) return new ToolOutcome("ERROR: redirect needs a task; this run has none.");
        var guidance = call.Arg("guidance");

        var current = _tasks.Get(task.Id).Status;
        if (current is not (TaskStatus.OutOfBudget or TaskStatus.Blocked or TaskStatus.Triage))
            return new ToolOutcome(
                $"ERROR: redirect only applies to a task being triaged; task {task.Id} is {SnakeCaseEnum.ToSnakeCase(current)}.");

        // No spend to reset: the next engineer is a new instance with its own allowance.
        var budget = call.OptionalInt("budget");
        if (budget is { } b) _tasks.SetBudget(task.Id, b);

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
    /// Records the command that starts the app, for the client to be told at handover. Refused
    /// unless it is a command this instance really ran.
    /// </summary>
    private ToolOutcome HowToRun(ToolCall call)
    {
        var command = call.Arg("command");
        if (!_ranCommands.Contains(command, StringComparer.Ordinal))
            return new ToolOutcome(
                $"ERROR: how_to_run only accepts a command you have run. '{command}' is not one of them. "
                + "Start the app with run() first, then record that exact command.");

        new ProjectMetaRepository(connection).Set("run_command", command);
        if (call.Optional("url") is { } url) new ProjectMetaRepository(connection).Set("run_url", url);
        return new ToolOutcome($"Recorded: the client will be told to start the project with `{command}`.");
    }

    /// <summary>
    /// Records a failure as a bug for the Principal to triage, born in `triage` with the
    /// harness's most recent captured observation attached as the evidence. Refused when this
    /// instance has observed nothing.
    /// </summary>
    private ToolOutcome FileBug(ToolCall call)
    {
        if (_lastRunTrace is null)
            return new ToolOutcome(
                "ERROR: file_bug needs evidence. Perform the check that demonstrates the failure first — run() "
                + "a command, or serve() the app and http() the endpoint — and its exact output is attached "
                + "automatically as the repro. Do not describe a result you did not observe.");

        var requirement = call.Optional("requirements_ref") is { } reqRef
            ? NormalizeRequirementRef(reqRef)
            : (RequirementsRef?)null;

        var objective = $"""
            A defect found in QA. Reproduce it, then make the expected result true and pin it with a test.

            ## Expected
            {call.Arg("expected")}

            ## Observed — captured verbatim from the check QA ran
            ```
            {_lastRunTrace!.Trim()}
            ```
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

    /// <summary>Accepts a bug in triage and releases it to the board for an engineer.</summary>
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

    /// <summary>
    /// Rejects a bug as not a real defect, keeping it on record with the reason so it is never
    /// re-filed. Called at triage, during review of a bug-fix, or by the PM with an explicit id.
    /// </summary>
    private ToolOutcome RejectBug(ToolCall call)
    {
        // The current task at triage or review; an explicit id when the PM calls it from chat.
        if ((call.OptionalInt("task") ?? task?.Id) is not { } bugId || _tasks.Find(bugId) is not { } bug)
            return new ToolOutcome("ERROR: reject_bug needs a bug — pass a task id, or run it on a bug task.");
        if (bug.Type != TaskType.Bug)
            return new ToolOutcome($"ERROR: reject_bug only applies to bug tasks; task {bugId} is a {SnakeCaseEnum.ToSnakeCase(bug.Type)}.");
        if (bug.Status is not (TaskStatus.Triage or TaskStatus.InReview or TaskStatus.Blocked))
            return new ToolOutcome(
                $"ERROR: reject_bug applies to a bug in triage, review, or blocked; task {bugId} is {SnakeCaseEnum.ToSnakeCase(bug.Status)}.");

        var reason = call.Arg("reason");
        _tasks.Transition(bugId, TaskStatus.Rejected);
        _tasks.SetProgressNote(bugId, $"REJECTED (not a bug): {reason}");
        new DiscussionRepository(connection).Open(bugId, SnakeCaseEnum.ToSnakeCase(recipe.Role), $"[bug rejected] {reason}");
        ResolveEscalations(bugId);
        LastProgressNote = $"Bug rejected: {reason}";
        RejectedBugReason = reason;
        // The PM keeps its turn to reply to the client; a triage or review instance ends here.
        return new ToolOutcome($"Bug {bugId} rejected and kept on record; QA will not re-file it.",
            recipe.Role == AgentRole.Pm ? null : EndReason.Done);
    }

    /// <summary>Sends a bug back to the Principal for another triage with the client's guidance.</summary>
    private ToolOutcome RetriageBug(ToolCall call)
    {
        if (call.OptionalInt("task") is not { } bugId || _tasks.Find(bugId) is not { } bug)
            return new ToolOutcome("ERROR: retriage_bug needs a task id of a bug.");
        if (bug.Type != TaskType.Bug)
            return new ToolOutcome($"ERROR: retriage_bug only applies to bug tasks; task {bugId} is a {SnakeCaseEnum.ToSnakeCase(bug.Type)}.");

        var note = call.Arg("note");
        _tasks.SetProgressNote(bugId, $"RE-TRIAGE (from the client, via the PM): {note}");
        if (bug.Status != TaskStatus.Triage && TaskTransitions.IsLegal(bug.Status, TaskStatus.Triage))
            _tasks.Transition(bugId, TaskStatus.Triage);
        ResolveEscalations(bugId);
        new DiscussionRepository(connection).Open(bugId, "pm", $"[re-triage: client guidance] {note}");
        return new ToolOutcome($"Bug {bugId} sent back to the Principal for triage with the client's guidance.");
    }

    /// <summary>Sends a task awaiting the client back to the Principal with their guidance.</summary>
    private ToolOutcome RetriageTask(ToolCall call)
    {
        if (AwaitingClient(call) is not { } task) return NotAwaitingClient("retriage_task");

        var note = call.Arg("note");
        _tasks.SetProgressNote(task.Id, $"FROM THE CLIENT (via the PM): {note}");
        _tasks.ResetOutOfBudgetCount(task.Id);
        _tasks.Transition(task.Id, TaskStatus.Triage);
        ResolveEscalations(task.Id);
        new DiscussionRepository(connection).Open(task.Id, "pm", $"[client guidance] {note}");
        return new ToolOutcome(
            $"Task {task.Id} sent back to the Principal with the client's guidance. " +
            "The build resumes on it by itself; the client does not have to start anything.");
    }

    /// <summary>Cancels a task the client dropped, along with everything depending on it.</summary>
    private ToolOutcome CancelTask(ToolCall call)
    {
        if (AwaitingClient(call) is not { } task) return NotAwaitingClient("cancel_task");

        var reason = call.Arg("reason");
        var cancelled = new List<long>();
        foreach (var affected in _tasks.UnfinishedDependents(task.Id).Append(task))
        {
            if (!TaskTransitions.IsLegal(affected.Status, TaskStatus.Cancelled)) continue;
            _tasks.Transition(affected.Id, TaskStatus.Cancelled);
            _tasks.SetProgressNote(affected.Id, $"CANCELLED by the client: {reason}");
            ResolveEscalations(affected.Id);
            cancelled.Add(affected.Id);
        }

        new DiscussionRepository(connection).Open(task.Id, "pm", $"[cancelled by the client] {reason}");
        return new ToolOutcome(
            $"Cancelled task(s) {string.Join(", ", cancelled)}. Their branches are dropped and the " +
            "build moves on without them.");
    }

    /// <summary>The task named by the call's `task` argument, if it is waiting on the client.</summary>
    private TaskRecord? AwaitingClient(ToolCall call) =>
        call.OptionalInt("task") is { } id
        && _tasks.Find(id) is { Status: TaskStatus.NeedsHuman } task ? task : null;

    private ToolOutcome NotAwaitingClient(string tool) =>
        new($"ERROR: {tool} needs the id of a task waiting on the client. Waiting now: " +
            $"{string.Join(", ", _tasks.AwaitingClient().Select(t => t.Id))}.");

    /// <summary>Marks any escalation pending on the PM for this task resolved.</summary>
    private void ResolveEscalations(long taskId)
    {
        foreach (var m in _messages.Pending("pm").Concat(_messages.Pending("client")))
            if (m.TaskId == taskId && m is EscalationMessage)
                _messages.SetStatus(m.Id, MessageStatus.Received);
    }

    /// <summary>
    /// Records that a task waits on another. The worker will not claim it until the dependency
    /// is done, and an edge that would close a cycle is refused.
    /// </summary>
    private ToolOutcome AddDependency(ToolCall call)
    {
        var taskId = call.OptionalInt("task") ?? throw new ToolCallException("add_dependency needs 'task'.");
        var dependsOn = call.OptionalInt("depends_on")
            ?? throw new ToolCallException("add_dependency needs 'depends_on'.");
        _tasks.AddDependency(taskId, dependsOn);
        return new ToolOutcome($"Task {taskId} now depends on task {dependsOn}.");
    }

    /// <summary>How many splits deep a task may be before break_and_relink refuses to split it.</summary>
    private const int SplitDepthCap = 2;

    /// <summary>
    /// Replaces an oversized task with the tasks the Principal has just created, and cancels it.
    /// The harness does the rewiring: every dependent of the old task waits on all the
    /// replacements, and every replacement inherits all of its dependencies, so the graph is
    /// correct whether the replacements run in sequence or in parallel. The old task is
    /// cancelled rather than deleted, keeping its ledger rows attributable. Every check runs
    /// before the first write, so a refusal leaves the graph untouched.
    /// </summary>
    private ToolOutcome BreakAndRelink(ToolCall call)
    {
        if (task is null) return new ToolOutcome("ERROR: break_and_relink needs a task; this run has none.");

        var current = _tasks.Get(task.Id);
        if (current.Status is not (TaskStatus.OutOfBudget or TaskStatus.Blocked or TaskStatus.Triage))
            return new ToolOutcome($"ERROR: break_and_relink only applies to a task being triaged; "
                + $"task {task.Id} is {SnakeCaseEnum.ToSnakeCase(current.Status)}.");

        if (current.SplitDepth >= SplitDepthCap)
            return new ToolOutcome(
                $"ERROR: task {task.Id} is already {current.SplitDepth} splits deep, the limit. "
                + "Splitting it again would keep deferring the problem. Give the client the "
                + "decision with escalate(reason) instead.");

        var ids = (call.Optional("new_tasks") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) ? id : -1)
            .ToList();
        if (ids.Contains(-1))
            return new ToolOutcome("ERROR: new_tasks must be a comma-separated list of task ids, e.g. \"7,8,9\".");

        // At least two, and created in this instance.
        if (ids.Count < 2)
            return new ToolOutcome(
                $"ERROR: break_and_relink needs at least two replacement tasks; got {ids.Count}. "
                + "Create them with create_task first — if the work is really one task, use "
                + "redirect or escalate instead.");

        var foreign = ids.Where(id => !_createdTaskIds.Contains(id)).ToList();
        if (foreign.Count > 0)
            return new ToolOutcome(
                $"ERROR: task(s) {string.Join(", ", foreign)} were not created in this triage. "
                + "break_and_relink only accepts replacements you have just created with create_task.");

        // Pre-check every edge the rewire will add, so a cycle is refused before any write.
        var dependents = _tasks.DependentsOf(task.Id).Where(d => !ids.Contains(d)).ToList();
        var dependencies = _tasks.DependenciesOf(task.Id).Where(d => !ids.Contains(d)).ToList();
        foreach (var dependent in dependents)
            foreach (var replacement in ids)
                if (_tasks.DependencyChain(from: replacement, to: dependent) is { Count: > 0 } chain)
                    return new ToolOutcome(
                        $"ERROR: task {dependent} cannot wait on replacement {replacement} — "
                        + $"{replacement} already depends on it ({string.Join(" → ", chain)}). "
                        + "Rework the replacements so the work flows one way.");

        foreach (var replacement in ids)
        {
            foreach (var dependent in dependents) _tasks.AddDependency(dependent, replacement);
            foreach (var dependency in dependencies) _tasks.AddDependency(replacement, dependency);

            // Onto the OLD task's parent, not the old task: it is about to be cancelled, and a
            // child of a cancelled task falls out of its Feature on the board.
            _tasks.SetParent(replacement, current.ParentId);
            if (current.MilestoneId is { } milestone && _tasks.Get(replacement).MilestoneId is null)
                _tasks.SetMilestone(replacement, milestone);
            _tasks.SetSplitDepth(replacement, current.SplitDepth + 1);
        }

        _tasks.RemoveDependenciesInvolving(task.Id);
        var reason = call.Optional("reason") ?? "too large to finish as one task";
        _tasks.SetProgressNote(task.Id, $"Split into {string.Join(", ", ids)}: {reason}");
        _tasks.Transition(task.Id, TaskStatus.Cancelled);

        LastProgressNote = $"Split into tasks {string.Join(", ", ids)}.";
        return new ToolOutcome(
            $"Task {task.Id} replaced by {string.Join(", ", ids)} and cancelled. "
            + (dependents.Count > 0 ? $"Now waiting on all of them: {string.Join(", ", dependents)}. " : "")
            + (dependencies.Count > 0 ? $"Each inherits its dependencies: {string.Join(", ", dependencies)}. " : "")
            + "They are on the board and will be released to engineers.",
            EndReason.Done);
    }

    /// <summary>Approves the diff for merge and ends the review.</summary>
    private ToolOutcome Approve(ToolCall call)
    {
        ReviewApproved = true;
        ReviewFeedback = call.Optional("note");
        return new ToolOutcome("Approved for merge.", EndReason.Done);
    }

    /// <summary>
    /// Sends the work back to the engineer with a reason, and ends the review. An optional
    /// convention is appended to CONVENTIONS.md on trunk by the caller.
    /// </summary>
    private ToolOutcome RequestChanges(ToolCall call)
    {
        ReviewApproved = false;
        ReviewFeedback = call.Arg("reason");
        ReviewConvention = call.Optional("convention");
        return new ToolOutcome("Changes requested; sending back to the engineer.", EndReason.Done);
    }

    /// <summary>
    /// Says something to the client and ends the turn. Recorded as a message row, so the
    /// conversation survives the instance that produced it.
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
    /// Saves the agent's progress note, written to the task immediately so it survives a kill.
    /// It is the only state a fresh instance inherits besides the workspace.
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
        // One rung up: engineer/qa/researcher → principal, principal → pm, pm → client.
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

    /// <summary>Caps one observation at <see cref="MaxObservationChars"/>.</summary>
    private static string Truncate(string text) =>
        text.Length <= MaxObservationChars
            ? text
            : text[..MaxObservationChars] + $"\n... [truncated {text.Length - MaxObservationChars} chars]";
}
