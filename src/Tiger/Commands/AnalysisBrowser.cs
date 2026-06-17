using System.Text.RegularExpressions;
using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser for viewing build analysis results. Shows recent analyses,
/// detail views with full diagnosis, and supports re-running analysis.
/// </summary>
public sealed partial class AnalysisBrowser
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
                    var firstLine = StripKnownBuildErrorPrefix(a.DiagnosisSummary.Split('\n')[0].Trim());
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
            var commands = new List<CommandBarItem>();
            if (_analysisService is not null)
            {
                commands.Add(new("Re-run", ConsoleKey.R, 1));
                commands.Add(new("Force full", ConsoleKey.F, 2));
            }
            commands.Add(new("View log", ConsoleKey.V, 3));
            commands.Add(new("Build detail", ConsoleKey.B, 4));

            _ui.RenderDetailPanel(
                ["Analysis", $"{Markup.Escape(analysis.DefinitionName)} #{analysis.BuildId}"],
                $"{FormatStatus(analysis.Status)}  {Markup.Escape(analysis.Project)}",
                () =>
                {
                    _ui.RenderField("Status", FormatStatus(analysis.Status));
                    _ui.RenderField("Organization", Markup.Escape(analysis.Organization));
                    _ui.RenderField("Project", Markup.Escape(analysis.Project));
                    _ui.RenderField("Build", Markup.Escape(analysis.BuildNumber));
                    _ui.RenderField("Branch", Markup.Escape(BrowserUI.SimplifyBranch(analysis.SourceBranch)));
                    if (analysis.Category is not null)
                    {
                        _ui.RenderField("Category", Markup.Escape(analysis.Category));
                    }
                    if (analysis.Confidence is not null)
                    {
                        _ui.RenderField("Confidence", Markup.Escape(analysis.Confidence));
                    }
                    _ui.RenderField("Created", Markup.Escape(analysis.CreatedAt));
                    if (analysis.CompletedAt is not null)
                    {
                        _ui.RenderField("Completed", Markup.Escape(analysis.CompletedAt));
                    }
                    if (analysis.LogPath is not null && File.Exists(analysis.LogPath))
                    {
                        _ui.RenderField("Full log", BrowserUI.FormatLink($"file://{analysis.LogPath}", Path.GetFileName(analysis.LogPath)));
                    }
                    _ui.RenderEmptyLine();

                    // Show diagnosis summary
                    if (analysis.DiagnosisSummary is not null)
                    {
                        if (analysis.Category == "known-issue")
                        {
                            _ui.RenderSectionTitle("Known Issues");
                            foreach (var line in analysis.DiagnosisSummary.Split('\n'))
                            {
                                var rendered = FormatKnownIssueLine(line.Trim());
                                if (rendered is not null)
                                {
                                    _ui.RenderPanelLine(rendered);
                                }
                            }
                        }
                        else
                        {
                            _ui.RenderSectionTitle("Diagnosis");
                            foreach (var diagLine in analysis.DiagnosisSummary.ReplaceLineEndings("\n").Split('\n'))
                            {
                                _ui.RenderPanelLine(Markup.Escape(diagLine));
                            }
                        }
                    }
                },
                PanelRenderer.BuildCommandBarString(commands));

            while (true)
            {
                var key = Console.ReadKey(true);
                if (_ui.HandleDetailScroll(key))
                {
                    continue;
                }

                if (key.Key == ConsoleKey.Escape)
                {
                    return;
                }

                if (key.Key == ConsoleKey.R && _analysisService is not null)
                {
                    _analysisService.RequestAnalysis(analysis.Organization, analysis.BuildId);
                    analysis = CreateQueuedAnalysis(analysis);
                    break; // re-render with queued state
                }

                if (key.Key == ConsoleKey.F && _analysisService is not null)
                {
                    _analysisService.RequestAnalysis(analysis.Organization, analysis.BuildId, fullAnalysisCheck: true);
                    analysis = CreateQueuedAnalysis(analysis);
                    break; // re-render with queued state
                }

                if (key.Key == ConsoleKey.V)
                {
                    ShowFullLog(analysis);
                    break; // re-render
                }

                if (key.Key == ConsoleKey.B)
                {
                    var buildBrowser = new BuildBrowser(_db, _clientFactory, _configDirectory, _analysisService);
                    buildBrowser.BrowseBuild(analysis.Organization, analysis.Project, analysis.BuildId);
                    break; // re-render
                }
            }
        }
    }

    private void ShowFullLog(BuildAnalysisInfo analysis)
    {
        _ui.RenderDetailPanel(
            ["Analysis", $"#{analysis.BuildId}", "Log"],
            $"[dim]{Markup.Escape(analysis.DefinitionName)}[/]",
            () =>
            {
                if (analysis.LogPath is null || !File.Exists(analysis.LogPath))
                {
                    _ui.RenderPanelLine("[dim]No log file available.[/]");
                    return;
                }

                var content = File.ReadAllText(analysis.LogPath);

                // Truncate very long logs for terminal display
                if (content.Length > 10000)
                {
                    content = content[..10000] + "\n\n... (truncated — see full file on disk)";
                }

                var markupLines = MarkdownRenderer.ToMarkupLines(content);
                foreach (var line in markupLines)
                {
                    _ui.RenderPanelLine(line);
                }
            },
            "[blue]Esc[/] Back");

        while (true)
        {
            var key = Console.ReadKey(true);
            if (_ui.HandleDetailScroll(key))
            {
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                return;
            }
        }
    }

    private static string FormatStatus(string status) => status switch
    {
        "complete" => "[green]Complete[/]",
        "running" => "[yellow]Running[/]",
        "queued" => "[yellow]Queued[/]",
        "pending" => "[dim]Pending[/]",
        "skipped" => "[blue]Skipped[/]",
        "failed" => "[red]Failed[/]",
        _ => Markup.Escape(status),
    };

    // Matches new format: [repo#number](url): title
    [GeneratedRegex(@"^\[(?<repo>[^\]]+)#(?<num>\d+)\]\((?<url>[^)]+)\):\s*(?<title>.+)$")]
    private static partial Regex NewFormatRegex();

    // Matches old format: #number: title  (or "Matches known issue(s): #number: title")
    [GeneratedRegex(@"^(?:Matches known issue\(s\):\s*)?#(?<num>\d+):\s*(?<title>.+)$")]
    private static partial Regex OldFormatRegex();

    /// <summary>
    /// Formats a single known-issue summary line as a clickable hyperlink.
    /// Handles both the new markdown-link format and the old plain format.
    /// For old-format entries, looks up the repository from the known_issues table.
    /// </summary>
    private string? FormatKnownIssueLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = NewFormatRegex().Match(line);
        if (match.Success)
        {
            var url = match.Groups["url"].Value;
            var repo = match.Groups["repo"].Value;
            var num = match.Groups["num"].Value;
            var title = StripKnownBuildErrorPrefix(match.Groups["title"].Value);
            return $"  {BrowserUI.FormatLink(url, $"{repo}#{num}")} {Markup.Escape(title)}";
        }

        var oldMatch = OldFormatRegex().Match(line);
        if (oldMatch.Success)
        {
            var num = oldMatch.Groups["num"].Value;
            var title = StripKnownBuildErrorPrefix(oldMatch.Groups["title"].Value);
            var issueNumber = int.Parse(num);

            // Look up repository from known_issues table to construct a link
            var repo = _db.WithCommand(cmd =>
            {
                cmd.CommandText = "SELECT repository FROM known_issues WHERE issue_number = @num LIMIT 1";
                cmd.Parameters.AddWithValue("@num", issueNumber);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? reader.GetString(0) : null;
            });

            if (repo is not null)
            {
                var url = $"https://github.com/{repo}/issues/{issueNumber}";
                return $"  {BrowserUI.FormatLink(url, $"{repo}#{num}")} {Markup.Escape(title)}";
            }

            return $"  #{num}: {Markup.Escape(title)}";
        }

        return $"  {Markup.Escape(StripKnownBuildErrorPrefix(line))}";
    }

    /// <summary>
    /// Strips the "[Known Build Error]" prefix from issue titles.
    /// </summary>
    private static string StripKnownBuildErrorPrefix(string text)
    {
        const string prefix = "[Known Build Error]";
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[prefix.Length..].TrimStart();
        }
        return text;
    }

    private static BuildAnalysisInfo CreateQueuedAnalysis(BuildAnalysisInfo original) => new()
    {
        Organization = original.Organization,
        BuildId = original.BuildId,
        Status = "queued",
        Project = original.Project,
        DefinitionName = original.DefinitionName,
        BuildNumber = original.BuildNumber,
        SourceBranch = original.SourceBranch,
        CreatedAt = original.CreatedAt,
    };
}


