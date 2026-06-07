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
    private const string MenuAnalysis = "Analysis";
    private const string MenuAgents = "Agents";
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

    private static void RenderBanner()
    {
        AnsiConsole.Write(new FigletText("tiger").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[dim]CI/CD Infrastructure Management[/]");
        AnsiConsole.WriteLine();
    }

    private static async Task RunMenuLoopAsync(
        TigerContext tigerContext, TigerDatabase db,
        AzdoClientFactory clientFactory,
        BuildBackfillService backfill,
        BuildAnalysisService analysisAgent,
        ServiceLog serviceLog, CancellationToken ct)
    {
        var menuLabels = new[]
        {
            $"[blue]B[/]uilds",
            $"[blue]T[/]ests",
            $"[blue]H[/]ealth",
            $"[blue]A[/]nalysis",
            $"A[blue]g[/]ents",
            $"[blue]C[/]onfiguration",
            $"[blue]S[/]tatus",
            $"[blue]Q[/]uit",
        };

        var selected = 0;
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.Clear();
            RenderBanner();

            AnsiConsole.MarkupLine("[bold]What would you like to do?[/]");
            for (var i = 0; i < menuLabels.Length; i++)
            {
                if (i == selected)
                {
                    AnsiConsole.MarkupLine($"  [blue]>[/] {menuLabels[i]}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"    {menuLabels[i]}");
                }
            }
            AnsiConsole.MarkupLine("  [blue]↑↓[/] Navigate   [blue]Enter[/] Select");

            var key = Console.ReadKey(true);

            // Hotkeys
            var hotkey = char.ToUpperInvariant(key.KeyChar) switch
            {
                'B' => 0, 'T' => 1, 'H' => 2, 'A' => 3, 'G' => 4, 'C' => 5, 'S' => 6, 'Q' => 7,
                _ => -1,
            };
            if (hotkey >= 0)
            {
                selected = hotkey;
            }
            else
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + menuLabels.Length) % menuLabels.Length;
                        continue;
                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % menuLabels.Length;
                        continue;
                    case ConsoleKey.Enter:
                        break;
                    case ConsoleKey.Escape:
                        return;
                    default:
                        continue;
                }
            }

            var choice = selected switch
            {
                0 => MenuBuilds,
                1 => MenuTests,
                2 => MenuHealth,
                3 => MenuAnalysis,
                4 => MenuAgents,
                5 => MenuConfig,
                6 => MenuStatus,
                7 => MenuQuit,
                _ => MenuQuit,
            };

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
        var scrollOffset = 0; // 0 = live tail (showing latest), >0 = scrolled back N entries
        var maxVisible = Math.Max(Console.WindowHeight - 5, 10);

        void Render()
        {
            AnsiConsole.Clear();
            var filterLabel = errorsOnly ? " [yellow](errors only)[/]" : "";
            var scrollLabel = scrollOffset > 0 ? $" [dim](scrolled back {scrollOffset})[/]" : " [dim](live)[/]";
            AnsiConsole.MarkupLine($"[bold underline]Service Log[/]{filterLabel}{scrollLabel}");
            AnsiConsole.WriteLine();

            var all = serviceLog.GetRecent(500);
            var filtered = errorsOnly
                ? all.Where(e => e.Level is ServiceLogLevel.Error or ServiceLogLevel.Warning).ToList()
                : all;

            // Apply scroll offset from the end
            var end = filtered.Count - scrollOffset;
            if (end < 0)
            {
                end = 0;
            }
            var start = Math.Max(0, end - maxVisible);
            var visible = filtered.Skip(start).Take(end - start).ToList();

            RenderLogEntries(visible);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [blue]E[/]rrors toggle   [blue]↑/↓[/] Scroll   [blue]End[/] Latest   [blue]Esc[/] Back");
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
