using System.Text;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

/// <summary>
/// Assembles agent instructions from the three layers (CLAUDE.md, [DECIDED]):
///   A — role identity      prompts/roles/&lt;role&gt;.md
///   B — task-type rules    prompts/tasks/&lt;type&gt;.md  (task work only)
///   C — the task packet    columns on the tasks row, rendered to markdown here
/// Layer C markdown is rendered into the prompt and never written to disk.
/// </summary>
public sealed class PromptAssembler(PromptLibrary prompts)
{
    /// <summary>Layers A + B + protocol + standing context, for an agent working a task.</summary>
    public string SystemPrompt(AgentRecipe recipe, TaskRecord task, PathJail workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prompts.Role(recipe.RolePrompt).TrimEnd()).AppendLine();
        sb.AppendLine(prompts.TaskType(task.Type).TrimEnd()).AppendLine();
        AppendCommon(sb, recipe, workspace);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Layer A + protocol + standing context, for a role in conversation rather
    /// than on a task. There is no Layer B because a chat has no task type — the
    /// client's words are the packet.
    /// </summary>
    public string ChatSystemPrompt(AgentRecipe recipe, PathJail workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prompts.Role(recipe.RolePrompt).TrimEnd()).AppendLine();
        AppendCommon(sb, recipe, workspace);
        return sb.ToString().TrimEnd();
    }

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
    /// Generated from the recipe rather than written prose, so the documented
    /// surface cannot drift from the executable one — a role that loses a tool
    /// stops being told it has one, with no second place to update.
    /// </summary>
    public static string ToolProtocol(AgentRecipe recipe)
    {
        var tools = string.Join("\n", recipe.Tools
            .Where(AgentToolset.Catalogue.ContainsKey)
            .Select(name => "- " + AgentToolset.Catalogue[name]));

        var sb = new StringBuilder($$$"""
            # Tools

            Act by emitting tool calls. A turn with no tool call accomplishes nothing.
            You may emit several calls in one turn; they run in order, and you see all
            the observations before your next turn. Syntax (raw text, no escaping):

            <tool name="write_file">
            <arg name="path">docs/requirements/INDEX.md</arg>
            <arg name="content">
            # Requirements
            </arg>
            </tool>

            Available tools:
            {{{tools}}}

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

        if (recipe.Tools.Contains("run"))
        {
            sb.AppendLine();
            sb.Append("- run() has no shell: no pipes, redirects, &&, or $(). One binary per call. ")
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

    /// <summary>Layer C — the task packet, the agent's first user turn.</summary>
    /// <param name="standingGuidance">
    /// Instructions that outlive one attempt — the client's answer when a task was put to
    /// them. Passed separately because <see cref="TaskRecord.ProgressNote"/> is overwritten
    /// by every redirect and review, so guidance stored only there is lost by the time an
    /// engineer reads it.
    /// </param>
    public static string TaskPacket(TaskRecord task, IReadOnlyList<string>? standingGuidance = null)
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

        if (task.ContextPaths.Count > 0)
        {
            sb.AppendLine("## Start here").AppendLine();
            foreach (var path in task.ContextPaths) sb.AppendLine($"- {path}");
            sb.AppendLine();
        }

        sb.AppendLine($"## Budget\n\n{task.TokensSpent} of {task.TokenBudget} tokens already spent.").AppendLine();

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
                "Begin. Read the files in your packet once to orient, then write. " +
                "Don't keep exploring — the first write_file should come within your " +
                "first few turns, not after you've read everything twice.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Rehydrate a chat as an alternating conversation. Statelessness mechanics
    /// (Principle 2): the instance answering this turn never saw the previous
    /// ones — the messages table is the memory, replayed on every spin-up.
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
