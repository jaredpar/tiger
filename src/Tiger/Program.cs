using Spectre.Console.Cli;
using Tiger.Commands;

var app = new CommandApp();

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
});

return await app.RunAsync(args);

