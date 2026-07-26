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
            // TODO: this client-only filter hides "pm"-addressed escalations — now only
            // the requirements questions the Principal kicks upward (see TaskRunner.Notify).
            // Surface those here or in a dedicated inbox so the client can answer them.
            .Where(m => m.FromAgent == "client" || m.ToAgent == "client")
            .TakeLast(HistoryTurns)
            .ToList();

    public async Task<ChatTurn> SendAsync(string clientMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientMessage))
            throw new ArgumentException("Say something to the PM.", nameof(clientMessage));

        _messages.Insert(Message.Create(MessageType.Question, "client", "pm", clientMessage));
        _log.Message($"client → pm: {Summarise(clientMessage)}");

        var workspace = _workspaces.PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var result = await loop
            .RunChatAsync(PromptAssembler.Conversation(History()), executor, ct)
            .ConfigureAwait(false);

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

    private static string Summarise(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= 60 ? line : line[..60].TrimEnd() + "…";
    }
}
