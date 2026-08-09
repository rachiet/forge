using System.Data;
using Forge.Core.Agents;
using Forge.Core.Board;
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
/// The client's conversation with the PM. Each turn spins a fresh instance whose memory is the
/// messages table replayed into a conversation, plus the documents on disk, so the chat can be
/// closed, reopened, or continued from another terminal without losing the thread.
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
    /// <summary>How many past messages are replayed into a turn.</summary>
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

    /// <summary>Escalations still pending on the PM, which its next turn is asked to resolve.</summary>
    public IReadOnlyList<Message> OpenEscalations() =>
        _messages.Pending("pm").Where(m => m is EscalationMessage).ToList();

    /// <summary>
    /// Runs one PM turn telling the client the project is finished and how to run it. The
    /// directory and command come from the harness, so the PM only writes the covering note.
    /// </summary>
    public async Task<ChatTurn> AnnounceReadyAsync(Delivery delivery, CancellationToken ct = default)
    {
        var brief =
            "[Everything the client asked for is built and has passed acceptance testing. Tell "
            + "them it is ready and how to try it, using EXACTLY these details — do not invent "
            + "or alter them:\n"
            + $"  folder:  {delivery.Directory}\n"
            + $"  command: {delivery.Command}\n"
            + (delivery.Url is { Length: > 0 } url ? $"  opens at: {url}\n" : "")
            + "Keep it short and warm. Remind them they can ask you for changes at any time. "
            + "End with `reply`.]";

        var workspace = _workspaces.PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var conversation = PromptAssembler.Conversation(History()).ToList();
        conversation.Add(new Llm.LlmMessage("user", brief));
        var result = await loop.RunChatAsync(conversation, executor, ct).ConfigureAwait(false);

        // If the PM's own turn failed, the harness says it plainly instead.
        var reply = result.Reply ?? $"Your project is ready. Open {delivery.Directory} and run: {delivery.Command}";
        if (result.Reply is null)
            _messages.Insert(Message.Create(MessageType.Status, "pm", "client", reply));
        _log.Message($"pm → client (project ready): {Summarise(reply)}");
        return new ChatTurn(reply, result.End, DocumentsChanged: false, result.Detail);
    }

    /// <summary>
    /// Runs one PM turn asking the client about the tasks waiting on them, and returns what it
    /// said. Started by the harness, so the question is waiting when they next look.
    /// </summary>
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
            + "retriage or cancel_task yet — you have not heard from them. End with `reply`.]\n"
            + items;

        var workspace = _workspaces.PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var conversation = PromptAssembler.Conversation(History()).ToList();
        conversation.Add(new Llm.LlmMessage("user", brief));
        var result = await loop.RunChatAsync(conversation, executor, ct).ConfigureAwait(false);

        // reply writes its own message; only a turn that ended without one needs this.
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

        // The PM's documents go straight to trunk; the client reviews them by approving.
        var changed = _workspaces.CommitAndPushTrunk(
            WorkspacePath, $"docs(pm): {Summarise(clientMessage)}");
        if (changed) _log.Event(EventType.GitCommit, "committed requirements to trunk");

        var reply = result.Reply ?? Fallback(result);
        if (result.Reply is null)
            _messages.Insert(Message.Create(MessageType.Status, "pm", "client", reply));
        _log.Message($"pm → client: {Summarise(reply)}");

        return new ChatTurn(reply, result.End, changed, result.Detail);
    }

    /// <summary>What the client is told when a PM turn ended without saying anything.</summary>
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
    /// Prepends the escalations awaiting a decision to this turn, with how to resolve each.
    /// Attached to the turn rather than the standing prompt, so it reflects what is open now.
    /// </summary>
    private void InjectOpenEscalations(List<Llm.LlmMessage> conversation)
    {
        var open = OpenEscalations();
        if (open.Count == 0 || conversation.Count == 0) return;

        var items = string.Join("\n", open.Select(m => $"  • task {m.TaskId}: {Summarise(m.Payload)}"));
        var note =
            "[Awaiting your decision — the Principal escalated these for the client. Raise each with the "
            + "client, then resolve it: reject_bug(task, reason) if the client agrees it's not a real defect, "
            + "or retriage(task, note) to send it back to the Principal with the client's guidance.\n"
            + items + "]";
        conversation[^1] = conversation[^1] with { Content = $"{note}\n\n{conversation[^1].Content}" };
    }

    private static string Summarise(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 60 ? line : line[..60].TrimEnd() + "…";
    }
}
