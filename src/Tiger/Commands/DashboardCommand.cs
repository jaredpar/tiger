using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// The default interactive command center. Starts background services (poller, etc.)
/// and presents a menu of available commands.
/// </summary>
public sealed class DashboardCommand : AsyncCommand
{
    private const int MenuBuilds = 0;
    private const int MenuTests = 1;
    private const int MenuHealth = 2;
    private const int MenuAnalysis = 3;
    private const int MenuAgents = 4;
    private const int MenuConfig = 5;
    private const int MenuStatus = 6;
    private const int MenuQuit = 7;

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();
        var config = tigerContext.Config;
        var serviceLog = new ServiceLog();

        // Ensure skills are registered for Copilot CLI
        SkillsRegistration.EnsureSkillsRegistered();

        var clientFactory = new AzdoClientFactory(tigerContext.AzureCredential);

        // Start all background services unconditionally — they no-op if no sources configured
        var ingestion = new BuildIngestionService(db, serviceLog);

        var knownIssues = new KnownIssueService(config, db, serviceLog);
        knownIssues.Start();

        var worker = new TaskIngestionService(db, ingestion, clientFactory, serviceLog);

        var analysisAgent = new BuildAnalysisService(db, clientFactory, knownIssues, serviceLog);
        worker.OnBuildIngested += analysisAgent.OnBuildIngested;

        worker.Start();
        analysisAgent.Start();

        var poller = new BuildPoller(config, db, clientFactory, serviceLog);
        poller.OnNewBuilds = ingestion.IngestBuildsAsync;
        poller.Start();

        var healthAgent = new HealthAgentService(config, db, serviceLog);
        healthAgent.Start();

        var backfill = new BuildBackfillService(config, db, ingestion, clientFactory, serviceLog);
        backfill.Start();

        try
        {
            await RunMenuLoopAsync(tigerContext, db, clientFactory, backfill, analysisAgent, serviceLog, ct);
        }
        finally
        {
            AnsiConsole.MarkupLine("[yellow]Stopping services...[/]");
            await worker.StopAsync();
            await poller.StopAsync();
            await backfill.StopAsync();
            await analysisAgent.StopAsync();
        }

        return 0;
    }

    private static async Task RunMenuLoopAsync(
        TigerContext tigerContext, TigerDatabase db,
        AzdoClientFactory clientFactory,
        BuildBackfillService backfill,
        BuildAnalysisService analysisAgent,
        ServiceLog serviceLog, CancellationToken ct)
    {
        var commands = new List<CommandBarItem>
        {
            new("Builds", ConsoleKey.B, MenuBuilds),
            new("Tests", ConsoleKey.T, MenuTests),
            new("Health", ConsoleKey.H, MenuHealth),
            new("Analysis", ConsoleKey.A, MenuAnalysis),
            new("Agents", ConsoleKey.G, MenuAgents),
            new("Config", ConsoleKey.C, MenuConfig),
            new("Status", ConsoleKey.S, MenuStatus),
            new("Quit", ConsoleKey.Q, MenuQuit),
        };

        while (!ct.IsCancellationRequested)
        {
            var choice = PanelLayout.ShowMainMenu(commands);

            switch (choice)
            {
                case MenuStatus:
                    await ShowLiveStatusAsync(serviceLog, ct);
                    break;
                case MenuBuilds:
                    var browser = new BuildBrowser(db, clientFactory, tigerContext.ConfigDirectory, analysisAgent);
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
                case MenuAnalysis:
                    var analysisBrowser = new AnalysisBrowser(db, analysisAgent, clientFactory, tigerContext.ConfigDirectory);
                    analysisBrowser.Browse();
                    break;
                case MenuAgents:
                    var agentBrowser = new AgentBrowser(db);
                    agentBrowser.Browse();
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
                case -1:
                    return;
            }
        }
    }

    /// <summary>
    /// Live tail view of background service log with filtering and scrolling.
    /// Hotkeys: E = toggle errors only, Escape = return to menu,
    /// Up/Down = scroll, End = jump to latest (live tail).
    /// </summary>
    private static async Task ShowLiveStatusAsync(ServiceLog serviceLog, CancellationToken ct)
    {
        var errorsOnly = false;
        var scrollOffset = 0;
        var maxVisible = Math.Max(Console.WindowHeight - 10, 10);

        void Render()
        {
            var filterLabel = errorsOnly ? "[yellow](errors only)[/]" : "";
            var scrollLabel = scrollOffset > 0 ? $"[dim](scrolled back {scrollOffset})[/]" : "[dim](live)[/]";
            var context = $"{filterLabel} {scrollLabel}".Trim();

            var all = serviceLog.GetRecent(500);
            var filtered = errorsOnly
                ? all.Where(e => e.Level is ServiceLogLevel.Error or ServiceLogLevel.Warning).ToList()
                : all;

            var end = filtered.Count - scrollOffset;
            if (end < 0)
            {
                end = 0;
            }
            var start = Math.Max(0, end - maxVisible);
            var visible = filtered.Skip(start).Take(end - start).ToList();

            PanelLayout.RenderDetailPanel(
                ["Status", "Service Log"],
                context,
                () =>
                {
                    if (visible.Count == 0)
                    {
                        PanelLayout.RenderPanelLine("[dim]No log entries yet...[/]");
                    }
                    else
                    {
                        foreach (var entry in visible)
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
                            PanelLayout.RenderPanelLine($"[dim]{time}[/] [{levelColor}]{service}[/] {message}");
                        }
                    }
                },
                PanelLayout.BuildCommandBarString(new List<CommandBarItem>
                {
                    new("Errors toggle", ConsoleKey.E, -2),
                }) + "  [blue]↑/↓[/] Scroll  [blue]End[/] Latest  [blue]Esc[/] Back");
        }

        Render();

        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var newEntryEvent = new ManualResetEventSlim(false);
        void OnEntry()
        {
            newEntryEvent.Set();
        }

        serviceLog.EntryAdded += OnEntry;
        try
        {
            while (!refreshCts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        return;
                    }

                    switch (key.Key)
                    {
                        case ConsoleKey.E:
                            errorsOnly = !errorsOnly;
                            scrollOffset = 0;
                            break;
                        case ConsoleKey.UpArrow:
                            scrollOffset += 3;
                            break;
                        case ConsoleKey.DownArrow:
                            scrollOffset = Math.Max(0, scrollOffset - 3);
                            break;
                        case ConsoleKey.PageUp:
                            scrollOffset += maxVisible;
                            break;
                        case ConsoleKey.PageDown:
                            scrollOffset = Math.Max(0, scrollOffset - maxVisible);
                            break;
                        case ConsoleKey.End:
                            scrollOffset = 0;
                            break;
                    }

                    Render();
                    continue;
                }

                if (newEntryEvent.Wait(500, refreshCts.Token))
                {
                    newEntryEvent.Reset();
                    if (scrollOffset == 0)
                    {
                        Render();
                    }
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
}
