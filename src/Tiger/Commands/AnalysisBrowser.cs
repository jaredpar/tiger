using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser for viewing build analysis results. Shows recent analyses,
/// detail views with full diagnosis, and supports re-running analysis.
/// </summary>
public sealed class AnalysisBrowser
{
    private readonly TigerDatabase _db;
    private readonly BuildAnalysisService _analysisService;

    public AnalysisBrowser(TigerDatabase db, BuildAnalysisService analysisService)
    {
        _db = db;
        _analysisService = analysisService;
    }

    public void Browse()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold]Build Failure Analysis[/]");
            AnsiConsole.WriteLine();

            var analyses = _db.GetRecentAnalyses(50);
            if (analyses.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No analyses yet. Failed builds will be analyzed automatically.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to go back.[/]");
                Console.ReadKey(true);
                return;
            }

            var items = analyses.Select(a =>
            {
                var statusIcon = a.Status switch
                {
                    "complete" => "[green]✓[/]",
                    "running" => "[yellow]⟳[/]",
                    "pending" => "[dim]…[/]",
                    "skipped" => "[blue]⊘[/]",
                    "failed" => "[red]✗[/]",
                    _ => "[dim]?[/]",
                };
                var category = a.Category is not null ? $"[dim]({a.Category})[/]" : "";
                var confidence = a.Confidence is not null ? $"[dim][{a.Confidence}][/]" : "";
                return $"{statusIcon} {Markup.Escape(a.DefinitionName)} #{a.BuildId} {category} {confidence}";
            }).ToList();

            var selected = BrowserUI.SelectWithEscape("", items, useMarkup: true);
            if (selected < 0)
            {
                return;
            }

            ShowAnalysisDetail(analyses[selected]);
        }
    }

    private void ShowAnalysisDetail(BuildAnalysisInfo analysis)
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
                AnsiConsole.WriteLine(analysis.DiagnosisSummary);
                AnsiConsole.WriteLine();
            }

            // Show log path as clickable link
            if (analysis.LogPath is not null && File.Exists(analysis.LogPath))
            {
                AnsiConsole.MarkupLine($"[bold]Full log:[/] {BrowserUI.FormatLink($"file://{analysis.LogPath}", Path.GetFileName(analysis.LogPath))}");
                AnsiConsole.WriteLine();
            }

            // Menu: View Log, Re-run, Back
            var menuItems = new List<string>
            {
                $"[blue](R)[/] Re-run analysis",
                $"[blue](L)[/] View full log",
            };

            var extraKeys = new Dictionary<ConsoleKey, int>
            {
                [ConsoleKey.R] = 0,
                [ConsoleKey.L] = 1,
            };

            var menuChoice = BrowserUI.SelectWithEscape("", menuItems, useMarkup: true, extraKeys: extraKeys);

            switch (menuChoice)
            {
                case 0: // Re-run
                    _analysisService.RequestAnalysis(analysis.Organization, analysis.BuildId);
                    AnsiConsole.MarkupLine("[green]Analysis re-queued. It will run on the next poll cycle.[/]");
                    Console.ReadKey(true);
                    // Refresh the analysis info
                    var refreshed = _db.GetRecentAnalyses(50)
                        .FirstOrDefault(a => a.Organization == analysis.Organization && a.BuildId == analysis.BuildId);
                    if (refreshed is not null)
                    {
                        analysis = refreshed;
                    }
                    continue;
                case 1: // View log
                    ShowFullLog(analysis);
                    continue;
                default: // Escape
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
            AnsiConsole.MarkupLine("[dim]Press any key to go back.[/]");
            Console.ReadKey(true);
            return;
        }

        var content = File.ReadAllText(analysis.LogPath);

        // Truncate very long logs for terminal display
        if (content.Length > 10000)
        {
            content = content[..10000] + "\n\n... (truncated — see full file on disk)";
        }

        AnsiConsole.WriteLine(content);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to go back.[/]");
        Console.ReadKey(true);
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
