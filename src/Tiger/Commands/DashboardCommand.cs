using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// The default interactive command center. Starts background services (poller, etc.)
/// and presents a menu of available commands.
/// </summary>
public sealed class DashboardCommand : AsyncCommand
{
    private const string MenuStatus = "Status (live service log)";
    private const string MenuBuilds = "Builds";
    private const string MenuFailures = "Test failures";
    private const string MenuConfig = "Configuration";
    private const string MenuQuit = "Quit";

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();
        var config = tigerContext.Config;
        var serviceLog = new ServiceLog();

        Func<string, string, AzdoClient> clientFactory = (org, proj) =>
            AzdoClient.Create(tigerContext.AzureCredential, org, proj);

        // Start background services (non-blocking)
        BuildPoller? poller = null;
        Task? backfillTask = null;
        if (config.Sources.Count > 0)
        {
            var ingestion = new BuildIngestionService(db, serviceLog);

            // Backfill runs in the background
            var backfill = new BuildBackfillService(config, db, ingestion, clientFactory, serviceLog);
            backfillTask = Task.Run(() => backfill.BackfillAsync(ct), ct);

            // Start the ongoing poller
            poller = new BuildPoller(config, db, clientFactory, serviceLog);
            poller.OnNewBuilds = ingestion.IngestBuildsAsync;
            poller.Start();
        }

        try
        {
            RenderBanner(config, tigerContext, poller);
            await RunMenuLoopAsync(tigerContext, db, clientFactory, poller, serviceLog, ct);
        }
        finally
        {
            if (poller is not null)
            {
                AnsiConsole.MarkupLine("[yellow]Stopping services...[/]");
                await poller.StopAsync();
            }
        }

        return 0;
    }

    private static void RenderBanner(TigerConfig config, TigerContext tigerContext, BuildPoller? poller)
    {
        AnsiConsole.Write(new FigletText("tiger").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[dim]CI/CD Infrastructure Management[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Service").AddColumn("Status");
        table.AddRow("Poller", poller?.IsRunning == true ? "[green]Running[/]" : "[red]Stopped[/]");
        table.AddRow("Backfill", "[green]Running[/]");
        table.AddRow("Database", $"[blue]{tigerContext.DatabasePath}[/]");
        table.AddRow("Sources", $"[blue]{config.Sources.Count}[/]");

        if (config.Sources.Count > 0)
        {
            table.AddRow("Poll interval", $"[blue]{config.PollIntervalSeconds}s[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task RunMenuLoopAsync(
        TigerContext tigerContext, TigerDatabase db,
        Func<string, string, AzdoClient> clientFactory,
        BuildPoller? poller,
        ServiceLog serviceLog, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .AddChoices(MenuBuilds, MenuFailures, MenuConfig, MenuStatus, MenuQuit));

            switch (choice)
            {
                case MenuStatus:
                    await ShowLiveStatusAsync(serviceLog, ct);
                    break;
                case MenuBuilds:
                    var browser = new BuildBrowser(db, clientFactory, tigerContext.ConfigDirectory);
                    browser.Browse();
                    break;
                case MenuFailures:
                    ShowTestFailures(db);
                    break;
                case MenuConfig:
                    ShowConfig(tigerContext);
                    break;
                case MenuQuit:
                    return;
            }

            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Live tail view of background service log. Auto-refreshes when new entries arrive.
    /// Press any key to return to the menu.
    /// </summary>
    private static async Task ShowLiveStatusAsync(ServiceLog serviceLog, CancellationToken ct)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Service Log (live)[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to return to menu...[/]");
        AnsiConsole.WriteLine();

        // Show existing entries
        RenderLogEntries(serviceLog.GetRecent(30));

        // Set up auto-refresh on new entries
        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var newEntryEvent = new ManualResetEventSlim(false);
        void OnEntry()
        {
            newEntryEvent.Set();
        }

        serviceLog.EntryAdded += OnEntry;
        try
        {
            // Wait for either a keypress or new log entries
            while (!refreshCts.Token.IsCancellationRequested)
            {
                // Check for keypress (non-blocking)
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    return;
                }

                // Wait briefly for new entries
                if (newEntryEvent.Wait(500, refreshCts.Token))
                {
                    newEntryEvent.Reset();
                    // Re-render
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[bold underline]Service Log (live)[/]");
                    AnsiConsole.MarkupLine("[dim]Press any key to return to menu...[/]");
                    AnsiConsole.WriteLine();
                    RenderLogEntries(serviceLog.GetRecent(30));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            serviceLog.EntryAdded -= OnEntry;
        }
    }

    private static void RenderLogEntries(List<ServiceLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No log entries yet...[/]");
            return;
        }

        foreach (var entry in entries)
        {
            var time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var levelColor = entry.Level switch
            {
                ServiceLogLevel.Success => "green",
                ServiceLogLevel.Warning => "yellow",
                ServiceLogLevel.Error => "red",
                _ => "blue",
            };
            var service = Markup.Escape(entry.Service);
            var message = Markup.Escape(entry.Message);
            AnsiConsole.MarkupLine($"[dim]{time}[/] [{levelColor}]{service}[/] {message}");
        }
    }

    private static void ShowTestFailures(TigerDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.test_case_title, COUNT(*) as fail_count,
                   GROUP_CONCAT(DISTINCT r.organization || '/' || r.project) as sources
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE tr.outcome = 'Failed'
            GROUP BY tr.test_case_title
            ORDER BY fail_count DESC
            LIMIT 20
            """;

        var table = new Table()
            .AddColumn("Test")
            .AddColumn("Failures")
            .AddColumn("Sources");

        using var reader = cmd.ExecuteReader();
        var hasRows = false;
        while (reader.Read())
        {
            hasRows = true;
            var title = reader.GetString(0);
            if (title.Length > 80)
                title = title[..77] + "...";
            table.AddRow(title, reader.GetInt64(1).ToString(), reader.GetString(2));
        }

        if (!hasRows)
        {
            AnsiConsole.MarkupLine("[yellow]No test failures recorded yet.[/]");
        }
        else
        {
            AnsiConsole.Write(table);
        }
    }

    private static void ShowConfig(TigerContext tigerContext)
    {
        var config = tigerContext.Config;
        AnsiConsole.MarkupLine($"[bold]Config path:[/] {TigerConfig.GetConfigPath(tigerContext.ConfigDirectory)}");
        AnsiConsole.MarkupLine($"[bold]Poll interval:[/] {config.PollIntervalSeconds}s");
        AnsiConsole.MarkupLine($"[bold]Sources:[/]");

        foreach (var source in config.Sources)
        {
            AnsiConsole.MarkupLine($"  [green]{source.Organization}[/] / [green]{source.Project}[/]");
            if (source.Repositories.Count > 0)
            {
                foreach (var repo in source.Repositories)
                    AnsiConsole.MarkupLine($"    - {repo}");
            }
            else
            {
                AnsiConsole.MarkupLine("    [dim](all repositories)[/]");
            }
        }
    }
}
