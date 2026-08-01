using Forge.Cli.Commands;
using Forge.Core.Configuration;
using Spectre.Console.Cli;

// Forge's own credentials, before anything can need them. Agents never see these:
// the tool executor builds a scrubbed environment for every command it runs.
EnvFile.Load();

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("forge");

    config.AddCommand<LogCommand>("log")
        .WithDescription("Replay the project's conversation/decision trail and token spend from SQLite.");

    config.AddCommand<ChatCommand>("chat")
        .WithDescription("Talk to the Project Manager — intake, requirements, status. The client's only interface.");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Claim the next ready task and run an agent against it (v1: one serial worker).");

    config.AddBranch("task", task =>
    {
        task.SetDescription("Put work on the board and inspect it.");
        task.AddCommand<TaskAddCommand>("add")
            .WithDescription("Create a task. Until M3 the Principal isn't generating these yet.");
        task.AddCommand<TaskListCommand>("list")
            .WithDescription("Show the task board with status, budget, and progress notes.");
    });

    config.AddBranch("project", project =>
    {
        project.SetDescription("Manage client projects under the Forge data root.");
        project.AddCommand<ProjectInitCommand>("init")
            .WithDescription("Create a project's data directory: project.db, bare repo, workspaces.");
    });

    config.AddCommand<BoardCommand>("board")
        .WithDescription("Serve the client's progress page: milestones, features, agent spend, and the PM chat.");

    config.AddBranch("prices", prices =>
    {
        prices.SetDescription("Model prices: what Forge will charge each tier against.");
        prices.AddCommand<PricesShowCommand>("show")
            .WithDescription("Show the cached price table's age and the rate for every configured model.");
        prices.AddCommand<PricesUpdateCommand>("update")
            .WithDescription("Force a price refresh, ignoring the cache TTL.");
    });

    config.AddBranch("secrets", secrets =>
    {
        secrets.SetDescription("Manage the encrypted secrets vault. Agents only ever see {{secret:NAME}}.");
        secrets.AddCommand<SecretsSetCommand>("set")
            .WithDescription("Store a secret value (prompted, hidden) and register its name.");
        secrets.AddCommand<SecretsListCommand>("list")
            .WithDescription("List registered secret names (never values).");
    });
});
return app.Run(args);
