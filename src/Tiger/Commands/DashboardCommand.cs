using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// The default interactive command center. Starts background services (poller, etc.)
/// and presents a menu of available commands.
/// </summary>
public sealed class DashboardCommand : AsyncCommand
{
    private const string MenuStatus = "Status";
    private const string MenuBuilds = "Builds";
    private const string MenuTests = "Tests";
    private const string MenuHealth = "Health";
    private const string MenuConfig = "Configuration";
    private const string MenuQuit = "Quit";

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();
        var config = tigerContext.Config;
        var serviceLog = new ServiceLog();

        // Ensure skills are registered for Copilot CLI
        SkillsRegistration.EnsureSkillsRegistered();

        Func<string, string, AzdoClient> clientFactory = (org, proj) =>
            AzdoClient.Create(tigerContext.AzureCredential, org, proj);

        // Start all background services unconditionally — they no-op if no sources configured
        var ingestion = new BuildIngestionService(db, serviceLog);

        var worker = new IngestionWorker(db, ingestion, clientFactory, serviceLog);
        worker.Start();

        var poller = new BuildPoller(config, db, clientFactory, serviceLog);
        poller.OnNewBuilds = ingestion.IngestBuildsAsync;
        poller.Start();

        var knownIssues = new KnownIssueService(config, db, serviceLog);
        knownIssues.Start();

        var healthAgent = new HealthAgentService(config, db, serviceLog);
        healthAgent.Start();

        var backfill = new BuildBackfillService(config, db, ingestion, clientFactory, serviceLog);
        backfill.Start();

        try
        {
            RenderBanner(config, tigerContext, poller, worker);
            await RunMenuLoopAsync(tigerContext, db, clientFactory, backfill, serviceLog, ct);
        }
        finally
        {
            AnsiConsole.MarkupLine("[yellow]Stopping services...[/]");
            await worker.StopAsync();
            await poller.StopAsync();
            await backfill.StopAsync();
        }

        return 0;
    }

    private static void RenderBanner(TigerConfig config, TigerContext tigerContext, BuildPoller poller, IngestionWorker worker)
    {
        AnsiConsole.Write(new FigletText("tiger").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[dim]CI/CD Infrastructure Management[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Service").AddColumn("Status");
        table.AddRow("Poller", poller.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]");
        table.AddRow("Ingestion Worker", worker.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]");
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
        BuildBackfillService backfill,
        ServiceLog serviceLog, CancellationToken ct)
    {
        var menuItems = new List<string>
        {
            $"[blue](B)[/] {MenuBuilds}",
            $"[blue](T)[/] {MenuTests}",
            $"[blue](H)[/] {MenuHealth}",
            $"[blue](C)[/] {MenuConfig}",
            $"[blue](S)[/] {MenuStatus}",
            $"[blue](Q)[/] {MenuQuit}",
        };
        var hotkeys = new Dictionary<ConsoleKey, int>
        {
            [ConsoleKey.B] = 0,
            [ConsoleKey.T] = 1,
            [ConsoleKey.H] = 2,
            [ConsoleKey.C] = 3,
            [ConsoleKey.S] = 4,
            [ConsoleKey.Q] = 5,
        };

        while (!ct.IsCancellationRequested)
        {
            var selected = BrowserUI.SelectWithEscape(
                "What would you like to do?", menuItems, extraKeys: hotkeys, useMarkup: true);
            if (selected < 0)
            {
                return;
            }

            var choice = selected switch
            {
                0 => MenuBuilds,
                1 => MenuTests,
                2 => MenuHealth,
                3 => MenuConfig,
                4 => MenuStatus,
                5 => MenuQuit,
                _ => MenuQuit,
            };

            switch (choice)
            {
                case MenuStatus:
                    await ShowLiveStatusAsync(serviceLog, ct);
                    break;
                case MenuBuilds:
                    var browser = new BuildBrowser(db, clientFactory, tigerContext.ConfigDirectory);
                    browser.Browse();
                    break;
                case MenuTests:
                    var testBrowser = new TestBrowser(db, clientFactory, tigerContext.ConfigDirectory);
                    testBrowser.Browse();
                    break;
                case MenuHealth:
                    var healthCmd = new HealthCommand();
                    await healthCmd.RunAsync(ct);
                    break;
                case MenuConfig:
                    var configEditor = new ConfigEditor(tigerContext.Config, tigerContext.ConfigDirectory);
                    configEditor.Show();
                    if (configEditor.Changed)
                    {
                        backfill.RequestBackfill();
                        AnsiConsole.MarkupLine("[green]Configuration changed — backfill requested.[/]");
                        Console.ReadKey(true);
                    }
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
}
