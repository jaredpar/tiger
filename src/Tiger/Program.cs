using Spectre.Console;
using Spectre.Console.Cli;
using Tiger.Commands;

// Warn about missing external tools
CheckRequiredTools();

// Running `tiger` with no arguments starts the interactive dashboard
var app = new CommandApp<DashboardCommand>();

app.Configure(config =>
{
    config.SetApplicationName("tiger");

    config.AddBranch("azdo", azdo =>
    {
        azdo.SetDescription("Azure DevOps commands");
        azdo.AddCommand<AzdoBuildsCommand>("builds")
            .WithDescription("Get recent builds, optionally filtered by definition ID");
        azdo.AddCommand<AzdoTestsCommand>("tests")
            .WithDescription("Get test failures for a build");
        azdo.AddCommand<AzdoTestSummaryCommand>("test-summary")
            .WithDescription("Get test counts per job for a build");
        azdo.AddCommand<AzdoTimelineCommand>("timeline")
            .WithDescription("Get the timeline for a build");
        azdo.AddCommand<AzdoArtifactsCommand>("artifacts")
            .WithDescription("Get artifacts for a build");
        azdo.AddCommand<AzdoJobsCommand>("jobs")
            .WithDescription("Get job records from a build timeline");
        azdo.AddCommand<AzdoDownloadCommand>("download")
            .WithDescription("Download an artifact from a build");
        azdo.AddCommand<AzdoPrBuildsCommand>("pr-builds")
            .WithDescription("Get builds for a pull request");
        azdo.AddCommand<AzdoRepoBuildsCommand>("repo-builds")
            .WithDescription("Get builds for a repository");
    });

    config.AddBranch("helix", helix =>
    {
        helix.SetDescription("Helix commands");
        helix.AddCommand<HelixWorkItemsCommand>("workitems")
            .WithDescription("List work items for a Helix job");
        helix.AddCommand<HelixConsoleCommand>("console")
            .WithDescription("Get console output for a Helix work item");
        helix.AddCommand<HelixFilesCommand>("files")
            .WithDescription("List or download files from a Helix work item");
    });

    config.AddBranch("config", cfg =>
    {
        cfg.SetDescription("Configuration management");
        cfg.AddCommand<ConfigShowCommand>("show")
            .WithDescription("Display current configuration");
        cfg.AddCommand<ConfigInitCommand>("init")
            .WithDescription("Create default config file");
        cfg.AddCommand<ConfigPathCommand>("path")
            .WithDescription("Print config file path");
    });

    config.AddBranch("poll", poll =>
    {
        poll.SetDescription("Build poller management");
        poll.AddCommand<PollStartCommand>("start")
            .WithDescription("Start the build poller (foreground, Ctrl+C to stop)");
    });

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show poller status, build counts, and recent activity");

    config.AddCommand<HealthCommand>("health")
        .WithDescription("Interactive agent session for CI health reporting");
});

return await app.RunAsync(args);

static void CheckRequiredTools()
{
    var missing = new List<string>();
    foreach (var tool in new[] { "sqlite3", "gh" })
    {
        if (!IsOnPath(tool))
        {
            missing.Add(tool);
        }
    }

    if (missing.Count > 0)
    {
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] Required tools not found on PATH: [bold]{string.Join(", ", missing)}[/]");
        AnsiConsole.MarkupLine("[yellow]Some features may not work correctly.[/]");
    }
}

static bool IsOnPath(string tool)
{
    var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
    var separator = OperatingSystem.IsWindows() ? ';' : ':';
    var extensions = OperatingSystem.IsWindows()
        ? new[] { ".exe", ".cmd", ".bat" }
        : Array.Empty<string>();

    foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
    {
        // Check without extension (works on Unix, and for extensionless files on Windows)
        if (File.Exists(Path.Combine(dir, tool)))
        {
            return true;
        }

        // On Windows, check common executable extensions
        foreach (var ext in extensions)
        {
            if (File.Exists(Path.Combine(dir, tool + ext)))
            {
                return true;
            }
        }
    }

    return false;
}
