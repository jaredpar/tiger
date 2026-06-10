using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser for viewing build analysis results. Shows recent analyses,
/// detail views with full diagnosis, and supports re-running analysis.
/// </summary>
public sealed class AnalysisBrowser
{
    private readonly PanelRenderer _ui = PanelRenderer.Create();

    private readonly TigerDatabase _db;
    private readonly BuildAnalysisService? _analysisService;
    private readonly AzdoClientFactory _clientFactory;
    private readonly string _configDirectory;

    public AnalysisBrowser(TigerDatabase db, BuildAnalysisService? analysisService, AzdoClientFactory clientFactory, string configDirectory)
    {
        _db = db;
        _analysisService = analysisService;
        _clientFactory = clientFactory;
        _configDirectory = configDirectory;
    }

    public void Browse()
    {
        while (true)
        {
            var analyses = _db.GetRecentAnalyses(50);
            if (analyses.Count == 0)
            {
                _ui.RenderDetailPanel(
                    ["Analysis"],
                    null,
                    () => _ui.RenderPanelLine("[dim]No analyses yet. Failed builds will be analyzed automatically.[/]"),
                    "[blue]Esc[/] Back");
                Console.ReadKey(true);
                return;
            }

            var items = analyses.Select(a =>
            {
                var statusIcon = a.Status switch
                {
                    "complete" => "[green]+[/]",
                    "running" => "[yellow]~[/]",
                    "pending" => "[dim]...[/]",
                    "skipped" => "[blue]-[/]",
                    "failed" => "[red]X[/]",
                    _ => "[dim]?[/]",
                };
                var category = a.Category is not null ? $"[dim]({Markup.Escape(a.Category)})[/]" : "";
                var label = $"{statusIcon} {Markup.Escape(a.DefinitionName)} #{a.BuildId} {category}";

                if (a.DiagnosisSummary is not null)
                {
                    var firstLine = a.DiagnosisSummary.Split('\n')[0].Trim();
                    if (firstLine.Length > 80)
                    {
                        firstLine = firstLine[..77] + "...";
                    }
                    label += $" [dim]— {Markup.Escape(firstLine)}[/]";
                }

                return label;
            }).ToList();

            var commands = new List<CommandBarItem>();

            var selected = _ui.SelectInPanel(
                ["Analysis"],
                $"[dim]{analyses.Count} analysis result(s)[/]",
                items,
                commands);
            if (selected < 0)
            {
                return;
            }

            ShowAnalysisDetail(analyses[selected]);
        }
    }

    /// <summary>
    /// Shows the detail view for a single analysis, with re-run, log view, and build navigation.
    /// </summary>
    public void ShowAnalysisDetail(BuildAnalysisInfo analysis)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold]Analysis: {Markup.Escape(analysis.DefinitionName)} #{analysis.BuildId}[/]");
            AnsiConsole.WriteLine();

            var table = new Table().NoBorder().HideHeaders().AddColumn("Key").AddColumn("Value");
            table.AddRow("[bold]Status[/]", FormatStatus(analysis.Status));
            table.AddRow("[bold]Organization[/]", Markup.Escape(analysis.Organization));
            table.AddRow("[bold]Project[/]", Markup.Escape(analysis.Project));
            table.AddRow("[bold]Build[/]", Markup.Escape(analysis.BuildNumber));
            table.AddRow("[bold]Branch[/]", Markup.Escape(analysis.SourceBranch));
            if (analysis.Category is not null)
            {
                table.AddRow("[bold]Category[/]", Markup.Escape(analysis.Category));
            }
            if (analysis.Confidence is not null)
            {
                table.AddRow("[bold]Confidence[/]", Markup.Escape(analysis.Confidence));
            }
            table.AddRow("[bold]Created[/]", Markup.Escape(analysis.CreatedAt));
            if (analysis.CompletedAt is not null)
            {
                table.AddRow("[bold]Completed[/]", Markup.Escape(analysis.CompletedAt));
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // Show diagnosis summary
            if (analysis.DiagnosisSummary is not null)
            {
                AnsiConsole.MarkupLine("[bold]Diagnosis:[/]");
                MarkdownRenderer.Render(analysis.DiagnosisSummary);
                AnsiConsole.WriteLine();
            }

            // Show log path as clickable link
            if (analysis.LogPath is not null && File.Exists(analysis.LogPath))
            {
                AnsiConsole.MarkupLine($"[bold]Full log:[/] {BrowserUI.FormatLink($"file://{analysis.LogPath}", Path.GetFileName(analysis.LogPath))}");
                AnsiConsole.WriteLine();
            }

            // Menu: conditionally include re-run options based on analysis service availability
            var menuItems = new List<string>();
            var extraKeys = new Dictionary<ConsoleKey, int>();
            var actions = new List<string>();

            if (_analysisService is not null)
            {
                menuItems.Add($"[blue]R[/]e-run analysis");
                extraKeys[ConsoleKey.R] = menuItems.Count - 1;
                actions.Add("rerun");

                menuItems.Add($"[blue]F[/]orce full analysis (skip known issue check)");
                extraKeys[ConsoleKey.F] = menuItems.Count - 1;
                actions.Add("force");
            }

            menuItems.Add($"[blue]V[/]iew full log");
            extraKeys[ConsoleKey.V] = menuItems.Count - 1;
            actions.Add("log");

            menuItems.Add($"[blue]B[/]uild detail");
            extraKeys[ConsoleKey.B] = menuItems.Count - 1;
            actions.Add("build");

            var menuChoice = BrowserUI.SelectWithEscape("", menuItems, useMarkup: true, extraKeys: extraKeys);

            if (menuChoice < 0)
            {
                return;
            }

            switch (actions[menuChoice])
            {
                case "rerun":
                    _analysisService!.RequestAnalysis(analysis.Organization, analysis.BuildId);
                    AnsiConsole.MarkupLine("[green]Analysis queued.[/]");
                    Console.ReadKey(true);
                    var refreshed = _db.GetRecentAnalyses(50)
                        .FirstOrDefault(a => a.Organization == analysis.Organization && a.BuildId == analysis.BuildId);
                    if (refreshed is not null)
                    {
                        analysis = refreshed;
                    }
                    continue;
                case "force":
                    _analysisService!.RequestAnalysis(analysis.Organization, analysis.BuildId, fullAnalysisCheck: true);
                    AnsiConsole.MarkupLine("[green]Full analysis queued (skipping known issue check).[/]");
                    Console.ReadKey(true);
                    var refreshedFull = _db.GetRecentAnalyses(50)
                        .FirstOrDefault(a => a.Organization == analysis.Organization && a.BuildId == analysis.BuildId);
                    if (refreshedFull is not null)
                    {
                        analysis = refreshedFull;
                    }
                    continue;
                case "log":
                    ShowFullLog(analysis);
                    continue;
                case "build":
                    var buildBrowser = new BuildBrowser(_db, _clientFactory, _configDirectory, _analysisService);
                    buildBrowser.BrowseBuild(analysis.Organization, analysis.Project, analysis.BuildId);
                    continue;
                default:
                    return;
            }
        }
    }

    private static void ShowFullLog(BuildAnalysisInfo analysis)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold]Log: {Markup.Escape(analysis.DefinitionName)} #{analysis.BuildId}[/]");
        AnsiConsole.WriteLine();

        if (analysis.LogPath is null || !File.Exists(analysis.LogPath))
        {
            AnsiConsole.MarkupLine("[dim]No log file available.[/]");
            AnsiConsole.MarkupLine("[dim]Press Escape to go back.[/]");
            Console.ReadKey(true);
            return;
        }

        var content = File.ReadAllText(analysis.LogPath);

        // Truncate very long logs for terminal display
        if (content.Length > 10000)
        {
            content = content[..10000] + "\n\n... (truncated — see full file on disk)";
        }

        MarkdownRenderer.Render(content);
        AnsiConsole.WriteLine();

        var menuItems = new List<string>
        {
            $"[blue]B[/]ack",
        };

        var extraKeys = new Dictionary<ConsoleKey, int>
        {
            [ConsoleKey.B] = 0,
        };

        BrowserUI.SelectWithEscape("", menuItems, useMarkup: true, extraKeys: extraKeys);
    }

    private static string FormatStatus(string status) => status switch
    {
        "complete" => "[green]Complete[/]",
        "running" => "[yellow]Running[/]",
        "pending" => "[dim]Pending[/]",
        "skipped" => "[blue]Skipped[/]",
        "failed" => "[red]Failed[/]",
        _ => Markup.Escape(status),
    };
}


