using System.Text;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>
/// Assembles an agent's instructions from three layers: the role prompt, the task-type prompt,
/// and the task packet rendered from the tasks row. The packet is rendered into the prompt and
/// never written to disk.
/// </summary>
public sealed class PromptAssembler(PromptLibrary prompts)
{
    /// <summary>The system prompt for an agent working a task: role, task type, tools, context.</summary>
    public string SystemPrompt(AgentRecipe recipe, TaskRecord task, PathJail workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prompts.Role(recipe.RolePrompt).TrimEnd()).AppendLine();
        sb.AppendLine(prompts.TaskType(task.Type).TrimEnd()).AppendLine();
        AppendCommon(sb, recipe, workspace);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The system prompt for a role in conversation rather than on a task. No task-type layer:
    /// a chat has no task type, and the conversation itself is the packet.
    /// </summary>
    public string ChatSystemPrompt(AgentRecipe recipe, PathJail workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prompts.Role(recipe.RolePrompt).TrimEnd()).AppendLine();
        AppendCommon(sb, recipe, workspace);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Appends the tool protocol and the recipe's standing-context files, if present.</summary>
    private static void AppendCommon(StringBuilder sb, AgentRecipe recipe, PathJail workspace)
    {
        sb.AppendLine(ToolProtocol(recipe)).AppendLine();

        foreach (var relative in recipe.AlwaysInContext)
        {
            var path = Path.Combine(workspace.Root, relative);
            if (!File.Exists(path)) continue;
            sb.AppendLine($"# {relative}").AppendLine();
            sb.AppendLine(File.ReadAllText(path).TrimEnd()).AppendLine();
        }
    }

    /// <summary>
    /// The tool protocol section: how to emit a call, the tools this recipe has with their
    /// arguments, and the limits the harness enforces. Generated from the recipe, so a role is
    /// never told about a tool it does not have.
    /// </summary>
    public static string ToolProtocol(AgentRecipe recipe)
    {
        var sb = new StringBuilder($$$"""
            # Tools

            Your entire reply is tool calls and nothing else. Anything outside a call is
            discarded, so a turn without one accomplishes nothing.

            The tools are live and already authorised, and your workspace — including
            every file named in your packet — is on disk at your root. There is nothing
            to check and nobody to ask: if you need to read something, call read_file
            now rather than saying you are about to.

            You may make several calls in one turn; they run in order, and you see all
            the observations before your next turn.

            Rules the harness enforces mechanically — they are not advice:
            - Every path is relative to your workspace root. Paths outside it are refused.
            - You may read and write {{{recipe.Scope.Describe()}}}. Anything else is refused.
            - Secrets appear only as {{secret:NAME}}; the value is substituted outside
              your context at exec time. Never ask for a secret's value.
            - You have a token budget. At 70% you get a warning; at 100% the harness
              stops calling the model mid-turn, whether or not you are finished.
            - You also have a turn budget: a fixed number of tool-using turns. You get
              a warning as it runs low and a hard warning on your final turn; when it
              runs out the harness stops the run. Being cut off cold at the cap is a
              failure — before then you must end in a clean state: the tool that ends
              your turn (done/escalate/reply), or at minimum a progress note so a fresh
              instance can resume. Never spend your last turns re-reading or exploring.
            """);

        if (recipe.Tools.Contains("run") || recipe.Tools.Contains("serve"))
        {
            var starters = recipe.Tools.Contains("serve") ? "run() and serve() have" : "run() has";
            sb.AppendLine();
            sb.Append($"- {starters} no shell: no pipes, redirects, &&, or $(). One binary per call. ")
              .Append($"Allowed: {string.Join(", ", recipe.ToolAllowlist)}.");
        }

        if (recipe.Tools.Contains("progress_note"))
        {
            sb.AppendLine().AppendLine().Append("""
                # Statelessness

                You are short-lived. If you die — budget, crash, iteration cap — a fresh
                instance restarts with NO memory of this conversation: it gets the task
                packet, the workspace on disk, and your progress note. Nothing else.
                Write a progress_note after any meaningful step: what is done, what is
                left, what you tried that failed, and the exact next action.

                Claims are verified against reality, never believed. `done` triggers the
                harness to build, test, and merge; saying the tests pass does not make it so.
                """);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The task packet: the agent's first user turn, rendered from the tasks row.</summary>
    /// <param name="task">The task being worked.</param>
    /// <param name="standingGuidance">
    /// Instructions that outlive one attempt, such as the client's answer when the task was put
    /// to them. Passed separately because the progress note is overwritten by every revision.
    /// </param>
    /// <param name="contractSlice">
    /// The task's contract operations as an OpenAPI fragment, so the engineer is handed the
    /// exact paths, status codes and response schemas it must produce.
    /// </param>
    public static string TaskPacket(
        TaskRecord task, IReadOnlyList<string>? standingGuidance = null, string? contractSlice = null,
        string? history = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Task {task.Id}: {task.Title}").AppendLine();
        sb.AppendLine("## Objective").AppendLine().AppendLine(task.Objective).AppendLine();

        if (standingGuidance is { Count: > 0 })
        {
            sb.AppendLine("## Standing guidance from the client").AppendLine();
            sb.AppendLine("This came from the person paying for the work. It applies to every " +
                          "attempt at this task, including yours. Follow it exactly.").AppendLine();
            foreach (var item in standingGuidance) sb.AppendLine($"- {item}");
            sb.AppendLine();
        }

        if (task.AcceptanceCriteria is { Length: > 0 } criteria)
            sb.AppendLine("## Acceptance criteria").AppendLine().AppendLine(criteria).AppendLine();

        if (task.RequirementsRef is { } req)
            sb.AppendLine($"## Requirement\n\nImplements `{req}`. Work to this exact version.").AppendLine();

        if (contractSlice is { Length: > 0 })
        {
            sb.AppendLine("## The contract you implement").AppendLine();
            sb.AppendLine(
                "These operations are yours. Match them exactly — paths, status codes and "
                + "response field names are the contract QA will test against, and they are "
                + "not yours to change. Use `[JsonPropertyName]` where a C# member name differs.")
                .AppendLine();
            sb.AppendLine("```yaml").AppendLine(contractSlice.TrimEnd()).AppendLine("```").AppendLine();
        }

        if (task.ContextPaths.Count > 0)
        {
            sb.AppendLine("## Start here").AppendLine();
            foreach (var path in task.ContextPaths) sb.AppendLine($"- {path}");
            sb.AppendLine();
        }

        sb.AppendLine($"## Budget\n\n{task.TokensSpent} of {task.TokenBudget} tokens already spent.").AppendLine();

        if (history is { Length: > 0 })
        {
            sb.AppendLine("## What has already been said about this task").AppendLine();
            sb.AppendLine(
                "Earlier attempts and the reviews of them, oldest first. Read it before you " +
                "start: an objection already answered does not need answering again, and one " +
                "raised twice is the thing standing between this task and merging. If two " +
                "reviews contradict each other, say so in your progress note rather than " +
                "silently picking one.").AppendLine();
            sb.AppendLine(history).AppendLine();
        }

        if (task.ProgressNote is { Length: > 0 } note)
        {
            sb.AppendLine("## Progress note from your predecessor").AppendLine();
            sb.AppendLine("A previous instance worked on this task and stopped. Its note:").AppendLine();
            sb.AppendLine(note).AppendLine();
            sb.AppendLine(
                "Verify the state of the workspace before trusting the note — read the " +
                "files and run the build. The note says what was intended, the repo says what is true.");
        }
        else
        {
            sb.AppendLine(
                "Begin now, with tool calls. If you need to look at something first, your " +
                "first call IS that read — not a sentence saying you are about to. Don't keep " +
                "exploring: the first write_file should come within your first few turns, not " +
                "after you have read everything twice.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Replays stored messages as an alternating conversation. The instance answering this turn
    /// never saw the previous ones; the messages table is the memory.
    /// </summary>
    public static IReadOnlyList<Llm.LlmMessage> Conversation(IEnumerable<Message> history)
    {
        List<Llm.LlmMessage> conversation = [];
        foreach (var message in history)
        {
            var role = message.FromAgent == "client" ? "user" : "assistant";
            if (conversation.Count > 0 && conversation[^1].Role == role)
            {
                // Same speaker twice (the client sent two messages before a reply):
                // fold them into one turn rather than emitting an illegal sequence.
                conversation[^1] = conversation[^1] with
                {
                    Content = $"{conversation[^1].Content}\n\n{message.Payload}",
                };
                continue;
            }
            conversation.Add(new Llm.LlmMessage(role, message.Payload));
        }

        // A conversation must open with the client; drop a leading agent turn.
        if (conversation.Count > 0 && conversation[0].Role == "assistant") conversation.RemoveAt(0);
        return conversation;
    }
}
