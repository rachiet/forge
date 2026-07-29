using System.ComponentModel;
using Forge.Core;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Agents;
using Forge.Core.Logging;
using Forge.Core.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

/// <summary>
/// The client's window on the project: a local page showing milestone and feature
/// progress, what each agent has spent, and the PM conversation — which is the only
/// way the client talks to the system.
///
/// Read model, not a second source of truth: every figure is queried from the same
/// tables the orchestrator writes, so the page cannot disagree with the ledger.
/// </summary>
public sealed class BoardCommand : AsyncCommand<BoardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("--port <PORT>")]
        [Description("Port to serve on (default 5177).")]
        public int Port { get; init; } = 5177;

        [CommandOption("--project-budget <USD>")]
        [Description(LlmSetup.BudgetDescription)]
        public decimal? ProjectBudget { get; init; }
    }

    /// <summary>
    /// One PM turn at a time. A turn writes to the project DB and the PM's git
    /// workspace, and `forge run` has no concurrency guard — so serialising the
    /// board's own writers is the least we can do. Held while the turn runs, which
    /// is why sending is a background job rather than a blocked HTTP request.
    /// </summary>
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private volatile bool _pmBusy;
    private volatile string? _pmError;

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

        // Roots resolved from the binary, not the working directory: `forge board` is run
        // from wherever the client happens to be, and the page ships beside the executable.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
        });
        builder.Logging.ClearProviders();          // the console belongs to Forge's own output
        builder.WebHost.UseUrls($"http://localhost:{settings.Port}");
        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // A fresh connection per request: SQLite connections are not thread-safe, and a
        // long-lived one shared across polls would serialise the whole page behind
        // whatever the PM is doing.
        app.MapGet("/api/board", () =>
        {
            using var conn = Database.OpenProject(dbPath);
            var snapshot = new BoardQuery(conn, settings.Project).Snapshot();
            return Results.Ok(new
            {
                snapshot.Project, snapshot.State, snapshot.TotalCostUsd, snapshot.Planned,
                snapshot.Milestones, snapshot.Features,
                snapshot.ProjectLevelCostUsd, snapshot.UnparentedTaskCostUsd,
                snapshot.Agents, snapshot.Chat,
                PmBusy = _pmBusy,
                PmError = _pmError,
            });
        });

        app.MapPost("/api/chat", async (ChatRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message cannot be empty." });
            if (_pmBusy)
                return Results.Conflict(new { error = "The project manager is still replying." });

            // Fire and forget on purpose: a PM turn is a real model call that can run for
            // a minute, and holding the request open that long would time out in the
            // browser. The reply lands in `messages`, which the page is already polling.
            _ = Task.Run(() => RunPmTurnAsync(paths, dbPath, settings, request.Message), CancellationToken.None);
            return Results.Accepted();
        });

        var url = $"http://localhost:{settings.Port}";
        AnsiConsole.MarkupLineInterpolated($"[green]Board for '{settings.Project}'[/] → [bold]{url}[/]");
        AnsiConsole.MarkupLine("[grey]The page polls every 3s. Ctrl-C to stop.[/]");

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task RunPmTurnAsync(ForgePaths paths, string dbPath, Settings settings, string message)
    {
        await _chatLock.WaitAsync().ConfigureAwait(false);
        _pmBusy = true;
        _pmError = null;
        try
        {
            using var conn = Database.OpenProject(dbPath);
            using var sink = new FileLogSink(paths.ProjectLog(settings.Project));
            var logger = new ForgeLogger(sink, settings.Project);
            var chat = new PmChat(
                paths, settings.Project, conn,
                LlmSetup.Metered(paths, conn, settings.ProjectBudget, logger),
                new SecretsVault(paths.VaultDir), PromptLibrary.Resolve(), logger);

            await chat.SendAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Surfaced on the page rather than thrown into a request nobody is waiting
            // on — an unpriced model or a bad key would otherwise fail silently.
            _pmError = ex.Message;
        }
        finally
        {
            _pmBusy = false;
            _chatLock.Release();
        }
    }

    public sealed record ChatRequest(string Message);
}
