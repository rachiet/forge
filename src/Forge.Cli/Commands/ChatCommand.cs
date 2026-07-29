using System.ComponentModel;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// The client's interface to the whole system (spec §7): they talk to the PM and
/// to nobody else. Conversation state lives in the messages table, so this can be
/// closed and reopened without losing the thread.
/// </summary>
public sealed class ChatCommand : AsyncCommand<ChatCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("-m|--message <TEXT>")]
        [Description("Send one message and exit, instead of opening an interactive session.")]
        public string? Message { get; init; }

        [CommandOption("--history")]
        [Description("Print the conversation so far and exit.")]
        public bool HistoryOnly { get; init; }

        [CommandOption("--project-budget <TOKENS>")]
        [Description("Hard project-wide token cap. Calls are refused once it is reached.")]
        public long? ProjectBudget { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        var dbPath = paths.ProjectDb(settings.Project);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No project '{settings.Project}' at {dbPath}.[/]");
            return 1;
        }

        using var conn = Database.OpenProject(dbPath);
        using var sink = new FileLogSink(paths.ProjectLog(settings.Project));
        var chat = new PmChat(
            paths, settings.Project, conn,
            new MeteredLlmClient(new AnthropicLlmClient(), conn, settings.ProjectBudget),
            new SecretsVault(paths.VaultDir), PromptLibrary.Resolve(),
            new ForgeLogger(sink, settings.Project));

        PrintHistory(chat);
        if (settings.HistoryOnly) return 0;

        if (settings.Message is { Length: > 0 } single)
        {
            await SendAsync(chat, single, cancellationToken);
            return 0;
        }

        AnsiConsole.MarkupLine("[grey]Talking to the PM. Blank line or Ctrl-C to leave.[/]\n");
        while (!cancellationToken.IsCancellationRequested)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold blue]you[/] >").AllowEmpty());
            if (string.IsNullOrWhiteSpace(input)) break;
            await SendAsync(chat, input, cancellationToken);
        }
        return 0;
    }

    private static async Task SendAsync(PmChat chat, string message, CancellationToken ct)
    {
        ChatTurn turn = default!;
        await AnsiConsole.Status().StartAsync("the PM is thinking…", async _ =>
        {
            turn = await chat.SendAsync(message, ct);
        });

        AnsiConsole.MarkupLineInterpolated($"\n[bold green]pm[/] > {turn.Reply}\n");
        if (turn.DocumentsChanged)
            AnsiConsole.MarkupLine("[grey]  (requirements committed to the project repo)[/]\n");
    }

    private static void PrintHistory(PmChat chat)
    {
        foreach (var message in chat.History())
        {
            var (who, colour) = message.FromAgent == "client"
                ? ("you", "blue")
                : ("pm", "green");
            AnsiConsole.MarkupLineInterpolated($"[bold {colour}]{who}[/] > {message.Payload}\n");
        }
    }
}
