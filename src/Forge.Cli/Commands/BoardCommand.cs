using System.ComponentModel;
using Dapper;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Scheduling;
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
/// The client's window on Forge: create a project, talk to the PM, watch milestones,
/// features and spend, and start or stop the build — all without a terminal, because
/// this page is meant to be the client's only interface.
///
/// Serves every project rather than one: the project is a query parameter, so the
/// dropdown switches without restarting the host.
/// </summary>
public sealed class BoardCommand : AsyncCommand<BoardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        [Description("Port to serve on (default 5177).")]
        public int Port { get; init; } = 5177;
    }

    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private readonly Dictionary<string, bool> _pmBusy = new();
    private readonly Dictionary<string, string> _pmError = new();

    // The build the board itself is running, if any. One at a time by decision, and
    // enforced across the whole machine by WorkerLease — a terminal `forge run` takes
    // the same lease, so a Start click cannot collide with one.
    private CancellationTokenSource? _workerCancel;
    private Task? _worker;
    private string? _workerProject;

    private ForgePaths _paths = null!;

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        _paths = ForgePaths.Resolve();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = "wwwroot",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{settings.Port}");
        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/projects", () => Results.Ok(new
        {
            Projects = ListProjects(),
            Providers = LlmClientFactory.Supported,
            Worker = WorkerLease.Current(_paths),
        }));

        app.MapPost("/api/projects", (NewProject request) =>
        {
            var name = (request.Name ?? "").Trim();
            try
            {
                ForgePaths.ValidName(name);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (request.Provider is { Length: > 0 } p &&
                !LlmClientFactory.Supported.Contains(p, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unknown provider '{p}'." });

            try
            {
                ProjectBootstrap.Init(_paths, name);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            // The client's choices live with the project, not in a machine-wide file:
            // two projects must be able to run on different providers and budgets.
            using var conn = Database.OpenProject(_paths.ProjectDb(name));
            var project = new ProjectSettings(conn)
            {
                Provider = request.Provider,
                BudgetUsd = request.BudgetUsd,
            };
            return Results.Ok(new { name });
        });

        app.MapGet("/api/board", (string project) =>
        {
            if (Resolve(project) is not { } dbPath) return Results.NotFound();
            using var conn = Database.OpenProject(dbPath);

            var snapshot = new BoardQuery(conn, project).Snapshot();
            var worker = WorkerLease.Current(_paths);

            return Results.Ok(new
            {
                snapshot.Project, snapshot.State, snapshot.TotalCostUsd,
                snapshot.BudgetUsd, snapshot.BudgetRemainingUsd, snapshot.BudgetExhausted,
                snapshot.Provider, snapshot.Planned, snapshot.SpecReady,
                snapshot.Milestones, snapshot.Features,
                snapshot.ProjectLevelCostUsd, snapshot.UnparentedTaskCostUsd,
                snapshot.Agents, snapshot.Chat,
                // Only read once the PM has handed work over; before that it is a draft.
                Spec = snapshot.SpecReady ? SpecReader.Read(_paths, project) : [],
                PmBusy = _pmBusy.GetValueOrDefault(project),
                PmError = _pmError.GetValueOrDefault(project),
                Worker = worker,
                Building = worker?.Project == project,
            });
        });

        app.MapPost("/api/budget", (BudgetChange request) =>
        {
            if (Resolve(request.Project) is not { } dbPath) return Results.NotFound();
            if (request.BudgetUsd is not > 0)
                return Results.BadRequest(new { error = "Budget must be greater than zero." });

            using var conn = Database.OpenProject(dbPath);
            var settings = new ProjectSettings(conn);
            // Raising it is the whole point: hitting the cap stops the build, and the
            // client needs a way to continue that does not involve a terminal.
            settings.BudgetUsd = request.BudgetUsd;
            return Results.Ok(new { budgetUsd = request.BudgetUsd });
        });

        app.MapPost("/api/chat", (ChatRequest request) =>
        {
            if (Resolve(request.Project) is not { } dbPath) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message cannot be empty." });
            if (_pmBusy.GetValueOrDefault(request.Project))
                return Results.Conflict(new { error = "The project manager is still replying." });

            // A PM turn is a real model call that can run for a minute; the browser would
            // time out long before it returns. The reply lands in `messages`, which the
            // page is already polling.
            _ = Task.Run(() => RunPmTurnAsync(dbPath, request.Project, request.Message));
            return Results.Accepted();
        });

        app.MapPost("/api/run", (RunRequest request) =>
        {
            if (Resolve(request.Project) is not { } dbPath) return Results.NotFound();

            if (request.Action == "stop")
            {
                _workerCancel?.Cancel();
                return Results.Accepted();
            }

            if (_worker is { IsCompleted: false })
                return Results.Conflict(new { error = $"Already building {_workerProject}." });

            if (WorkerLease.Current(_paths) is { } held)
                return Results.Conflict(new
                {
                    error = $"'{held.Project}' is already building (pid {held.Pid}). " +
                            "Only one project builds at a time.",
                });

            _workerCancel = new CancellationTokenSource();
            _workerProject = request.Project;
            _worker = Task.Run(() => RunWorkerAsync(dbPath, request.Project, _workerCancel.Token));
            return Results.Accepted();
        });

        var url = $"http://localhost:{settings.Port}";
        AnsiConsole.MarkupLineInterpolated($"[green]Forge board[/] → [bold]{url}[/]");
        AnsiConsole.MarkupLine("[grey]The page polls every 3s. Ctrl-C to stop.[/]");

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        _workerCancel?.Cancel();
        return 0;
    }

    /// <summary>The project's database, or null if there is no such project.</summary>
    private string? Resolve(string? project)
    {
        if (string.IsNullOrWhiteSpace(project)) return null;
        try
        {
            var path = _paths.ProjectDb(project);
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;   // a name that could traverse is simply not a project
        }
    }

    /// <summary>
    /// The registry plus enough of each project's state to fill the dropdown. Cheap
    /// enough per poll: a handful of projects, one small query each.
    /// </summary>
    private IReadOnlyList<object> ListProjects()
    {
        using var global = Database.OpenGlobal(_paths.GlobalDb);
        var names = global.Query<string>("SELECT name FROM projects ORDER BY name").ToList();

        List<object> projects = [];
        foreach (var name in names)
        {
            if (Resolve(name) is not { } dbPath) continue;
            try
            {
                using var conn = Database.OpenProject(dbPath);
                var snapshot = new BoardQuery(conn, name).Snapshot(chatLimit: 0);
                projects.Add(new
                {
                    name,
                    state = snapshot.State,
                    totalCostUsd = snapshot.TotalCostUsd,
                    budgetUsd = snapshot.BudgetUsd,
                    provider = snapshot.Provider,
                });
            }
            catch (Exception ex)
            {
                projects.Add(new { name, state = "unreadable", error = ex.Message });
            }
        }
        return projects;
    }

    private async Task RunPmTurnAsync(string dbPath, string project, string message)
    {
        await _chatLock.WaitAsync().ConfigureAwait(false);
        _pmBusy[project] = true;
        _pmError.Remove(project);
        try
        {
            using var conn = Database.OpenProject(dbPath);
            using var sink = new FileLogSink(_paths.ProjectLog(project));
            var logger = new ForgeLogger(sink, project);
            var chat = new PmChat(
                _paths, project, conn, LlmSetup.Metered(_paths, conn, logger: logger),
                new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger);

            await chat.SendAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pmError[project] = ex.Message;
        }
        finally
        {
            _pmBusy[project] = false;
            _chatLock.Release();
        }
    }

    /// <summary>
    /// The build loop, run inside the board so the client can start and stop it. Holds
    /// the machine-wide lease for its whole life and beats on every tick, so the page
    /// can tell "working" from "idle" and a second worker cannot start.
    /// </summary>
    private async Task RunWorkerAsync(string dbPath, string project, CancellationToken ct)
    {
        using var lease = WorkerLease.TryAcquire(_paths, project);
        if (lease is null) return;

        using var conn = Database.OpenProject(dbPath);
        using var sink = new FileLogSink(_paths.ProjectLog(project));
        var logger = new ForgeLogger(sink, project);
        var runner = new TaskRunner(
            _paths, project, conn, LlmSetup.Metered(_paths, conn, logger: logger),
            new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                lease.Beat();
                var outcome = await runner.RunNextByPriorityAsync(ct).ConfigureAwait(false);
                if (outcome is null) break;      // board drained
            }
        }
        catch (OperationCanceledException) { /* the client pressed stop */ }
        catch (Exception ex)
        {
            logger.Message($"Board worker stopped: {ex.Message}");
        }
    }

    public sealed record NewProject(string? Name, decimal? BudgetUsd, string? Provider);
    public sealed record ChatRequest(string Project, string Message);
    public sealed record BudgetChange(string Project, decimal? BudgetUsd);
    public sealed record RunRequest(string Project, string Action);
}
