using System.Data;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;

namespace Forge.Core.Chat;

public sealed record ChatTurn(string Reply, EndReason End, bool DocumentsChanged, string? Detail = null);

/// <summary>
/// The client's side of the pipeline (spec §7): one CLI conversation with the PM.
///
/// The PM is as stateless as any other agent — each turn spins a fresh instance
/// whose memory is the messages table replayed into a conversation, plus the docs
/// on disk. Nothing is held between invocations, which is why `forge chat` can be
/// closed and reopened, or answered from a different terminal, without losing the
/// thread.
/// </summary>
public sealed class PmChat(
    ForgePaths paths,
    string project,
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null)
{
    /// <summary>How much history to replay. Older turns live in the log and in the docs the PM wrote.</summary>
    private const int HistoryTurns = 40;

    private readonly AgentRecipe _recipe = AgentRecipe.Pm;
    private readonly MessageRepository _messages = new(conn);
    private readonly WorkspaceManager _workspaces = new(paths, project);
    // Intake has no task yet, so PM chat logs at project scope (task column blank).
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    public string WorkspacePath => paths.RoleWorkspace(project, "pm");

    /// <summary>The conversation so far, oldest first — what `forge chat` prints on open.</summary>
    public IReadOnlyList<Message> History() =>
        _messages.Log()
            .Where(m => m.FromAgent == "client" || m.ToAgent == "client")
            .TakeLast(HistoryTurns)
            .ToList();

    /// <summary>
    /// Items the Principal (or the harness) escalated for a human decision — pending
    /// escalations addressed to the PM. These are injected into the PM's turn so it
    /// raises them with the client and resolves them (reject_bug / retriage_bug),
    /// rather than a task silently stranding on the board.
    /// </summary>
    public IReadOnlyList<Message> OpenEscalations() =>
        _messages.Pending("pm").Where(m => m is EscalationMessage).ToList();

    /// <summary>
    /// Runs one PM turn that asks the client about the tasks waiting on them, and
    /// returns what it said.
    /// </summary>
    /// <remarks>
    /// Started by the harness rather than by the client typing, so the question is
    /// already in the thread when they next look at the board.
    /// </remarks>
    public async Task<ChatTurn> AskAboutStuckWorkAsync(
        IReadOnlyList<TaskRecord> waiting, CancellationToken ct = default)
    {
        var items = string.Join("\n", waiting.Select(t =>
            $"  • task {t.Id} — {t.Title}: {t.ProgressNote ?? "(no note)"}"));
        var brief =
            "[The build has stopped on work the engineering team could not resolve. The "
            + "engineering notes below are for you, not for the client — do not repeat them. "
            + "Write a SHORT message (a few sentences) saying what is stuck, and be specific "
            + "about the decision you need from them: guidance so it can be tried again, or "
            + "drop it. No task ids, token budgets, file names or tooling. Do not call "
            + "resolve_task or cancel_task yet — you have not heard from them. End with `reply`.]\n"
            + items;

        var workspace = _workspaces.PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var conversation = PromptAssembler.Conversation(History()).ToList();
        conversation.Add(new Llm.LlmMessage("user", brief));
        var result = await loop.RunChatAsync(conversation, executor, ct).ConfigureAwait(false);

        // The `reply` tool writes its own message; only a turn that ended without one
        // needs the harness to say something, or the client is left with silence.
        var reply = result.Reply ?? Fallback(result);
        if (result.Reply is null)
            _messages.Insert(Message.Create(MessageType.Status, "pm", "client", reply, waiting[0].Id));
        _log.Message($"pm → client (unprompted): {Summarise(reply)}");
        return new ChatTurn(reply, result.End, DocumentsChanged: false, result.Detail);
    }

    public async Task<ChatTurn> SendAsync(string clientMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientMessage))
            throw new ArgumentException("Say something to the PM.", nameof(clientMessage));

        _messages.Insert(Message.Create(MessageType.Question, "client", "pm", clientMessage));
        _log.Message($"client → pm: {Summarise(clientMessage)}");

        var workspace = _workspaces.PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var conversation = PromptAssembler.Conversation(History()).ToList();
        InjectOpenEscalations(conversation);
        var result = await loop.RunChatAsync(conversation, executor, ct).ConfigureAwait(false);

        // Requirements live in git with the code (spec §5) — so a chat turn that
        // authored documents is a commit, not a file sitting in a scratch directory.
        // No review gate: these are the PM's own artifacts, and the client is the
        // reviewer via sign-off.
        var changed = _workspaces.CommitAndPushTrunk(
            WorkspacePath, $"docs(pm): {Summarise(clientMessage)}");
        if (changed) _log.Event(EventType.GitCommit, "committed requirements to trunk");

        var reply = result.Reply ?? Fallback(result);
        if (result.Reply is null)
            _messages.Insert(Message.Create(MessageType.Status, "pm", "client", reply));
        _log.Message($"pm → client: {Summarise(reply)}");

        return new ChatTurn(reply, result.End, changed, result.Detail);
    }

    /// <summary>
    /// The PM ended its turn without saying anything — budget, cap, crash. The
    /// client is a person waiting at a prompt, so they get told what happened
    /// rather than silence.
    /// </summary>
    private static string Fallback(AgentRunResult result) => result.End switch
    {
        EndReason.Budget =>
            "I've used up the token budget for this conversation and stopped before spending more. "
            + "Raise the budget to continue.",
        EndReason.Iterations =>
            "I worked through my turn limit without getting back to you. Ask again, or narrow the question.",
        EndReason.Escalated =>
            "I've escalated this — it needs a decision I can't make on my own.",
        _ => $"I couldn't complete that turn. {result.Detail}".Trim(),
    };

    /// <summary>
    /// Prepend any items awaiting a human decision to the current turn, with instructions
    /// on how to resolve them. Attached to the turn (not the standing prompt) so the PM
    /// sees exactly what's open right now and can act on it with the client.
    /// </summary>
    private void InjectOpenEscalations(List<Llm.LlmMessage> conversation)
    {
        var open = OpenEscalations();
        if (open.Count == 0 || conversation.Count == 0) return;

        var items = string.Join("\n", open.Select(m => $"  • task {m.TaskId}: {Summarise(m.Payload)}"));
        var note =
            "[Awaiting your decision — the Principal escalated these for the client. Raise each with the "
            + "client, then resolve it: reject_bug(task, reason) if the client agrees it's not a real defect, "
            + "or retriage_bug(task, note) to send it back to the Principal with the client's guidance.\n"
            + items + "]";
        conversation[^1] = conversation[^1] with { Content = $"{note}\n\n{conversation[^1].Content}" };
    }

    private static string Summarise(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 60 ? line : line[..60].TrimEnd() + "…";
    }
}
