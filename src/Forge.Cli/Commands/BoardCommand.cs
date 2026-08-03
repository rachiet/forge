using System.ComponentModel;
using Dapper;
using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Board;
using Forge.Core.Chat;
using Forge.Core.Configuration;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
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
    // Concurrent on purpose: written from background PM/worker tasks and read on
    // ASP.NET request threads at the same time — a plain Dictionary is undefined
    // behaviour under that access pattern.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _pmBusy = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pmError = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _runError = new();

    // One sink per project for the life of the host: a chat turn and a worker running
    // together used to open two writers on the same forge.log, interleaving lines and
    // throwing outright on Windows. Never disposed per-turn; the process owns them.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileLogSink> _sinks = new();

    private ForgeLogger LoggerFor(string project) => new(
        _sinks.GetOrAdd(project, p => new FileLogSink(_paths.ProjectLog(p))), project);

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
                snapshot.Provider, snapshot.Planned, snapshot.SpecReady, snapshot.Proposal,
                snapshot.AwaitingClient, snapshot.NeedsClient,
                snapshot.Milestones, snapshot.Features,
                AgentName = ClientFacing.AgentName,
                snapshot.ProjectLevelCostUsd, snapshot.UnparentedTaskCostUsd,
                snapshot.Agents, snapshot.Chat,
                // Only read once the PM has handed work over; before that it is a draft.
                Spec = snapshot.SpecReady ? SpecReader.Read(_paths, project) : [],
                PmBusy = _pmBusy.GetValueOrDefault(project),
                PmError = _pmError.GetValueOrDefault(project),
                RunError = _runError.GetValueOrDefault(project),
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

            // The cap being spent is the only thing that escalation reported, and it no
            // longer is. Left pending it would keep excluding its task from the queue.
            var messages = new MessageRepository(conn);
            foreach (var m in messages.Pending("pm"))
                if (m is EscalationMessage && m.Payload.StartsWith("Project budget exhausted", StringComparison.Ordinal))
                    messages.SetStatus(m.Id, MessageStatus.Received);

            return Results.Ok(new { budgetUsd = request.BudgetUsd });
        });

        // The client's one approval. Approving is what opens the Feature and starts the
        // build, so the PM hands nothing to engineering until the person paying says so.
        app.MapPost("/api/proposal", (ProposalDecision request) =>
        {
            if (Resolve(request.Project) is not { } dbPath) return Results.NotFound();
            using var conn = Database.OpenProject(dbPath);

            if (RequirementsProposal.Load(conn) is not { } proposal)
                return Results.Conflict(new { error = "There is nothing waiting for approval." });

            if (request.Decision == "decline")
            {
                RequirementsProposal.Clear(conn);
                return Results.Ok(new { approved = false });
            }
            if (request.Decision != "approve")
                return Results.BadRequest(new { error = $"Unknown decision '{request.Decision}'." });

            var feature = proposal.Approve(conn);
            var started = StartWorker(request.Project, dbPath, out var error);
            return Results.Ok(new { approved = true, feature = feature.Id, started, error });
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
            if (request.Action != "start")
                return Results.BadRequest(new { error = $"Unknown action '{request.Action}' — start or stop." });

            return StartWorker(request.Project, dbPath, out var error)
                ? Results.Accepted()
                : Results.Conflict(new { error });
        });

        var url = $"http://localhost:{settings.Port}";
        AnsiConsole.MarkupLineInterpolated($"[green]Forge board[/] → [bold]{url}[/]");
        AnsiConsole.MarkupLine("[grey]The page polls every 3s. Ctrl-C to stop.[/]");

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        _workerCancel?.Cancel();
        foreach (var sink in _sinks.Values) sink.Dispose();
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
                var (state, totalCostUsd, budgetUsd, provider) = new BoardQuery(conn, name).Summary();
                projects.Add(new { name, state, totalCostUsd, budgetUsd, provider });
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
        _pmError.TryRemove(project, out _);
        try
        {
            using var conn = Database.OpenProject(dbPath);
            var logger = LoggerFor(project);
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
    /// <summary>
    /// Takes the machine-wide lease and starts the build loop, or reports why it could
    /// not. Shared by the Resume control and by approving a proposal, so both paths get
    /// the same one-build-at-a-time guarantee.
    /// </summary>
    private bool StartWorker(string project, string dbPath, out string? error)
    {
        if (_worker is { IsCompleted: false })
        {
            error = $"Already building {_workerProject}.";
            return false;
        }
        if (WorkerLease.Current(_paths) is { } held)
        {
            error = $"'{held.Project}' is already building (pid {held.Pid}). " +
                    "Only one project builds at a time.";
            return false;
        }

        _runError.TryRemove(project, out _);
        _workerCancel = new CancellationTokenSource();
        _workerProject = project;
        _worker = Task.Run(() => RunWorkerAsync(dbPath, project, _workerCancel.Token));
        error = null;
        return true;
    }

    private async Task RunWorkerAsync(string dbPath, string project, CancellationToken ct)
    {
        using var lease = WorkerLease.TryAcquire(_paths, project);
        if (lease is null)
        {
            // The client got a 202 before we got here; without this line a lost race
            // (a terminal run grabbed the lease first) would look like a dead button.
            _runError[project] = WorkerLease.Current(_paths) is { } held
                ? $"'{held.Project}' is already building (pid {held.Pid}) — one build at a time."
                : "Could not start: another worker grabbed the lease first.";
            return;
        }
        _runError.TryRemove(project, out _);

        using var conn = Database.OpenProject(dbPath);
        var logger = LoggerFor(project);
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
                if (outcome.ProjectBudgetExhausted)
                {
                    // Nothing failed; the cap is spent. Stop pulling work — the page's
                    // budget strip explains, and Raise + Start resumes from here.
                    _runError[project] = "Project budget exhausted — the build is paused. " +
                                         "Raise the budget and press Start to continue.";
                    break;
                }
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
    public sealed record ProposalDecision(string Project, string Decision);
}
