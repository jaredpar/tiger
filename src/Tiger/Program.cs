using Spectre.Console;
using Spectre.Console.Cli;
using Tiger;
using Tiger.Commands;

// Warn about missing external tools
CheckRequiredTools();

// Check for outdated database schema
if (!CheckDatabaseSchema())
    return 0;

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
        azdo.AddCommand<AzdoDownloadDumpsCommand>("download-dumps")
            .WithDescription("Download crash dump files from build artifacts");
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

    config.AddBranch("db", db =>
    {
        db.SetDescription("Database management");
        db.AddCommand<DbDeleteBuildCommand>("delete-build")
            .WithDescription("Delete a build and all associated data from the database");
    });

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show poller status, build counts, and recent activity");

    config.AddCommand<HealthCommand>("health")
        .WithDescription("Interactive agent session for CI health reporting");
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return 1;
}

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

/// <summary>
/// Checks if the database schema is outdated and prompts to delete if so.
/// Returns true to continue startup, false to abort.
/// </summary>
static bool CheckDatabaseSchema()
{
    var configDir = TigerUtils.GetConfigDirectory();
    var dbPath = Path.Combine(configDir, "tiger.db");

    if (!TigerDatabase.IsOutdated(dbPath))
        return true;

    var existingVersion = TigerDatabase.GetExistingSchemaVersion(dbPath);
    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Database schema is outdated (v{existingVersion} → v{TigerDatabase.CurrentSchemaVersion}).");
    AnsiConsole.MarkupLine("[yellow]The database must be deleted and rebuilt from scratch.[/]");

    if (!AnsiConsole.Confirm("Delete database and repopulate?", defaultValue: true))
    {
        AnsiConsole.MarkupLine("[dim]Aborted. Exiting.[/]");
        return false;
    }

    // Delete the DB file (and WAL/SHM files if present)
    foreach (var suffix in new[] { "", "-wal", "-shm" })
    {
        var file = dbPath + suffix;
        if (File.Exists(file))
            File.Delete(file);
    }

    // Delete health and analysis artifacts — they reference DB data that no longer exists
    foreach (var subDir in new[] { TigerUtils.HealthDirectoryName, TigerUtils.AnalysisLogsDirectoryName })
    {
        var dir = Path.Combine(configDir, subDir);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    AnsiConsole.MarkupLine("[green]Database and artifacts deleted. They will be recreated on next use.[/]");
    return true;
}
