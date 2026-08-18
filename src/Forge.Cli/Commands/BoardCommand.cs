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
using Forge.Core.Ui;
using Forge.Core.Workspaces;
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
/// Serves the client's web page and its API: create a project, talk to the PM, watch
/// requirements, features and spend, approve requirements, and start or stop the build. Every
/// project is served by one host, with the project as a query parameter.
/// </summary>
public sealed class BoardCommand : AsyncCommand<BoardCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--port <PORT>")]
        [Description("Port to serve on (default 5177).")]
        public int Port { get; init; } = 5177;
    }

    /// <summary>Consecutive build steps that may throw before the worker gives up on the board.</summary>
    private const int MaxFailedTicks = 3;

    private readonly SemaphoreSlim _chatLock = new(1, 1);
    // Concurrent because background PM and worker tasks write these while request threads read.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _pmBusy = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _pmError = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _runError = new();

    // One log sink per project for the life of the host, so a chat turn and a worker never
    // open two writers on the same file.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileLogSink> _sinks = new();

    /// <summary>The logger for a project, sharing one sink per project across the host.</summary>
    private ForgeLogger LoggerFor(string project) => new(
        _sinks.GetOrAdd(project, p => new FileLogSink(_paths.ProjectLog(p))), project);

    // The build the board itself is running, if any. WorkerLease enforces one per machine.
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

            // Required, not defaulted: the provider decides which models every agent on this
            // project runs on, and there is no machine-wide setting to fall back to.
            if (!LlmClientFactory.IsSupported(request.Provider))
                return Results.BadRequest(new
                {
                    error = $"Pick a provider: {string.Join(", ", LlmClientFactory.Supported)}.",
                });

            try
            {
                ProjectBootstrap.Init(_paths, name);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            // Stored per project, so two projects can use different providers and budgets.
            using var conn = Database.OpenProject(_paths.ProjectDb(name));
            var project = new ProjectSettings(conn)
            {
                Provider = request.Provider!,
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
                snapshot.AwaitingClient, snapshot.NeedsClient, snapshot.Delivery,
                snapshot.ThemeOffered,
                // The look the project is built with, named on the rail so the client can see
                // and change it without asking anyone.
                Theme = ThemeName(conn),
                snapshot.Plan,
                AgentName = ClientFacing.AgentName,
                snapshot.ProjectLevelCostUsd,
                snapshot.Agents, snapshot.Chat,
                // Read whenever there is anything committed: the page shows it in the rail once
                // the client has approved, and in the review dialog while it is still a draft.
                Spec = SpecReader.Read(_paths, project),
                // What this change does to the spec, and the chain of changes behind it. Empty
                // on a first build, where the whole specification IS the change.
                SpecChanges = SpecReader.Changes(_paths, project, SpecBaseline.Get(conn)),
                Changes = ChangeLog.Read(_paths, project),
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
            // Raising the cap is how a client continues a build that hit it.
            settings.BudgetUsd = request.BudgetUsd;

            // Clear the escalation the spent cap raised; it would otherwise keep its task
            // out of the queue.
            var messages = new MessageRepository(conn);
            foreach (var m in messages.Pending("pm"))
                if (m is EscalationMessage && m.Payload.StartsWith("Project budget exhausted", StringComparison.Ordinal))
                    messages.SetStatus(m.Id, MessageStatus.Received);

            return Results.Ok(new { budgetUsd = request.BudgetUsd });
        });

        // Approving opens the Feature and starts the build; it is the client's one commitment.
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

            // The entry becomes the durable record of this change the moment it is accepted;
            // stamped before the Feature opens, so no design run can read it as still pending.
            if (proposal.ChangeEntry is { Length: > 0 } entry)
                new WorkspaceManager(_paths, request.Project).EditTrunkFile(
                    _paths.RoleWorkspace(request.Project, "pm"), entry,
                    ChangeLog.Approve, $"docs(pm): {System.IO.Path.GetFileName(entry)} approved");

            var feature = proposal.Approve(conn);

            // Written here rather than by a PM turn, so it lands on the very next poll.
            new MessageRepository(conn).Insert(Message.Create(
                MessageType.Status, "pm", "client", ClientFacing.ApprovalAcknowledgement, feature.Id));

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

            // A PM turn outlasts any browser request; its reply arrives via the page's polling.
            _ = Task.Run(() => RunPmTurnAsync(dbPath, request.Project, request.Message));
            return Results.Accepted();
        });

        // What the interface currently looks like, and everything it could look like.
        app.MapGet("/api/theme", (string project) =>
        {
            if (Resolve(project) is not { } dbPath) return Results.NotFound();
            using var conn = Database.OpenProject(dbPath);
            var prompts = PromptLibrary.Resolve();

            return Results.Ok(new
            {
                Current = ThemeChoice.From(new ProjectMetaRepository(conn)),
                Themes = UiKit.ThemeTiles(prompts),
                ModeCss = UiKit.ModeStylesheets(prompts),
                Modes = ThemeChoice.Modes,
                // Hue and chroma per accent, so a tile can repaint its accent ramp locally
                // exactly as theme.css would.
                Accents = ThemeChoice.Accents.ToDictionary(a => a.Key, a => new { a.Value.Hue, a.Value.Chroma }),
                Densities = ThemeChoice.Densities.Keys,
                Radii = ThemeChoice.Radii.Keys,
            });
        });

        // The client changing the look themselves: the choice is recorded and installed
        // straight onto trunk, with no agent and no task.
        app.MapPost("/api/theme", (ThemeChange request) =>
        {
            if (Resolve(request.Project) is not { } dbPath) return Results.NotFound();
            var prompts = PromptLibrary.Resolve();

            var choice = new ThemeChoice(
                request.Theme ?? ThemeChoice.DefaultTheme,
                request.Mode ?? ThemeChoice.DefaultMode,
                request.Accent,
                request.Density ?? ThemeChoice.DefaultDensity,
                request.Radius ?? ThemeChoice.DefaultRadius);

            if (choice.Invalid(UiKit.Themes(prompts)) is { } problem)
                return Results.BadRequest(new { error = problem });

            using var conn = Database.OpenProject(dbPath);
            var meta = new ProjectMetaRepository(conn);
            choice.Save(meta);
            ThemeOffer.Clear(meta);

            // A running build owns trunk, so leave the write to its next task.
            var applied = _workerProject != request.Project
                       && AppearanceChange.Apply(_paths, request.Project, choice, prompts);

            LoggerFor(request.Project).Message($"Client set the interface to {choice.Describe()}.");
            return Results.Ok(new { theme = choice.Describe(), appliedToTrunk = applied });
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

    /// <summary>Every project, with the little state the dropdown shows for each.</summary>
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

    /// <summary>
    /// Runs one PM turn in the background and starts a worker afterwards if the turn left work
    /// on the board. The reply reaches the page through its ordinary polling.
    /// </summary>
    private async Task RunPmTurnAsync(string dbPath, string project, string message)
    {
        await _chatLock.WaitAsync().ConfigureAwait(false);
        _pmBusy[project] = true;
        _pmError.TryRemove(project, out _);
        var resume = false;
        try
        {
            using var conn = Database.OpenProject(dbPath);
            var logger = LoggerFor(project);
            var chat = new PmChat(
                _paths, project, conn, LlmSetup.Metered(_paths, conn, logger: logger),
                new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger);

            await chat.SendAsync(message).ConfigureAwait(false);

            // A turn can put work back on the board, so check whether a worker is now needed:
            // the previous one exited when the board went quiet.
            resume = new TaskRepository(conn).HasWorkForAWorker();
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

        // Outside the chat lock and after the connection closes, since StartWorker opens its
        // own. A worker already building wins, and reaches the task on its next tick.
        if (resume) StartWorker(project, dbPath, out _);
    }

    /// <summary>
    /// Takes the machine-wide lease and starts the build loop in the background, or reports why
    /// it could not. Shared by the Start control, approving a proposal, and a PM turn that put
    /// work back on the board.
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

    /// <summary>
    /// The build loop, run inside the board so the client can start and stop it. Holds the
    /// machine-wide lease for its whole life and beats it on every tick, so the page can tell
    /// working from idle and no second worker can start.
    /// </summary>
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

        // Consecutive ticks that threw. A tick leaves its task claimable, so one failure is
        // retried by the next tick; a deterministic one would otherwise spin forever.
        var failedTicks = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                lease.Beat();

                // Per tick, so one bad step costs that step and not the build.
                try
                {
                    var outcome = await runner.RunNextByPriorityAsync(ct).ConfigureAwait(false);
                    if (outcome is null) break;      // board drained
                    failedTicks = 0;
                    if (outcome.ProjectBudgetExhausted)
                    {
                        // Nothing failed; the cap is spent. Stop pulling work — the page's
                        // budget strip explains, and Raise + Start resumes from here.
                        _runError[project] = "Project budget exhausted — the build is paused. " +
                                             "Raise the budget and press Start to continue.";
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;   // the client pressed stop; the outer handler ends the run
                }
                catch (Exception ex)
                {
                    logger.Message($"Build step failed ({++failedTicks} of {MaxFailedTicks}): {ex.Message}");
                    if (failedTicks < MaxFailedTicks) continue;

                    _runError[project] = $"The build stopped after {MaxFailedTicks} failed steps: {ex.Message}";
                    logger.Event(EventType.ErrorInternal, _runError[project]);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* the client pressed stop */ }
        catch (Exception ex)
        {
            // The last resort: anything thrown outside the tick itself, so the worker still
            // exits cleanly and releases its lease rather than taking the host down.
            logger.Message($"Board worker stopped: {ex.Message}");
            _runError[project] = $"The build stopped unexpectedly: {ex.Message}";
        }
    }

    public sealed record NewProject(string? Name, decimal? BudgetUsd, string? Provider);
    public sealed record ChatRequest(string Project, string Message);
    public sealed record BudgetChange(string Project, decimal? BudgetUsd);
    public sealed record RunRequest(string Project, string Action);
    public sealed record ProposalDecision(string Project, string Decision);
    /// <summary>The project's current look, as the rail card names it.</summary>
    private static object ThemeName(System.Data.IDbConnection conn)
    {
        var choice = ThemeChoice.From(new ProjectMetaRepository(conn));
        return new { choice.Theme, choice.Mode, choice.Accent, Describe = choice.Describe() };
    }

    public sealed record ThemeChange(
        string Project, string? Theme, string? Mode, string? Accent, string? Density, string? Radius);
}
