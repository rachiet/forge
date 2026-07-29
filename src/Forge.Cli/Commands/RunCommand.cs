using System.ComponentModel;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Forge.Core.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// The worker. v1 is one serial worker (spec §1) — it claims one task, runs the
/// agent loop against it, and integrates or parks the result.
/// </summary>
public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("--task <ID>")]
        [Description("Run one specific task instead of claiming the next one off the board.")]
        public long? TaskId { get; init; }

        [CommandOption("--loop")]
        [Description("Keep claiming tasks until the board has no more work for the role.")]
        public bool Loop { get; init; }

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

        // The undecorated provider adapter never leaves this line — every call an
        // agent makes goes through the supervisor.
        var llm = new MeteredLlmClient(
            new AnthropicLlmClient(), conn, settings.ProjectBudget);

        // Default sink: the project's log file. Swap this line to point logs
        // anywhere — a console sink, a composite, a remote service.
        using var sink = new FileLogSink(paths.ProjectLog(settings.Project));
        var logger = new ForgeLogger(sink, settings.Project);

        var runner = new TaskRunner(
            paths, settings.Project, conn, llm,
            new SecretsVault(paths.VaultDir), PromptLibrary.Resolve(), logger);

        if (settings.TaskId is { } id)
        {
            var task = new TaskRepository(conn).Get(id);
            Report(await runner.RunAsync(task, cancellationToken));
            return 0;
        }

        var ran = 0;
        do
        {
            // Priority: a Principal-owned stuck task (blocked/out-of-budget) is cleared
            // before the engineer advances, because it usually gates the rest of the DAG.
            var outcome = await runner.RunNextByPriorityAsync(cancellationToken);
            if (outcome is null)
            {
                AnsiConsole.MarkupLine(ran == 0
                    ? "[grey]No claimable work.[/]"
                    : "[grey]Board drained.[/]");
                break;
            }
            Report(outcome);
            ran++;
        }
        while (settings.Loop && !cancellationToken.IsCancellationRequested);

        return 0;
    }

    private static void Report(TaskRunOutcome outcome)
    {
        var colour = outcome.Status == Forge.Core.Model.TaskStatus.Done ? "green" : "yellow";
        // A project-level QA round carries no task id (0); label it as QA, not "Task 0".
        var subject = outcome.TaskId == 0 ? "QA" : $"Task {outcome.TaskId}";
        AnsiConsole.MarkupLineInterpolated(
            $"[{colour}]{subject} → {SnakeCaseEnum.ToSnakeCase(outcome.Status)}[/] ({outcome.End})");
        AnsiConsole.MarkupLineInterpolated($"[grey]{outcome.Summary}[/]");
    }
}
