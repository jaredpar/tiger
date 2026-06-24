using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// Interactive viewer for health agent results.
/// Shows repo+pipeline combos, drill into state-of-the-build, then into individual runs.
/// </summary>
public sealed class HealthCommand : AsyncCommand
{
    private readonly PanelRenderer _ui = PanelRenderer.Create();

    public async Task RunAsync(CancellationToken ct)
    {
        await ExecuteAsync(null!, ct);
    }

    protected override Task<int> ExecuteAsync(Spectre.Console.Cli.CommandContext context, CancellationToken ct)
    {
        var configDir = TigerUtils.GetConfigDirectory();
        var dbPath = Path.Combine(configDir, "tiger.db");

        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]Database not found. Run tiger to start collecting data first.[/]");
            return Task.FromResult(1);
        }

        using var db = TigerDatabase.Open(dbPath);
        var config = TigerConfig.Load(configDir);
        var agent = new HealthAgentService(config, db);

        ShowCombosPage(agent);
        return Task.FromResult(0);
    }

    /// <summary>
    /// Top-level page: list of repo + pipeline combos that have health reports.
    /// </summary>
    private void ShowCombosPage(HealthAgentService agent)
    {
        while (true)
        {
            var runs = agent.GetRecentRuns();
            if (runs.Count == 0)
            {
                _ui.RenderDetailPanel(
                    ["Health"],
                    null,
                    ["[yellow]No health reports available yet. The agent runs every 15 minutes.[/]"],
                    "[blue]Esc[/] Back");
                Console.ReadKey(true);
                return;
            }

            var combos = runs
                .Select(r => (r.Repository, r.Definition))
                .Distinct()
                .ToList();

            var items = combos.Select(c => $"{c.Repository} / {c.Definition}").ToList();

            var selected = _ui.SelectInPanel(
                ["Health"],
                $"[dim]{combos.Count} pipeline(s)[/]",
                items,
                new List<CommandBarItem>());
            if (selected < 0)
            {
                return;
            }

            var (repo, def) = combos[selected];
            ShowStatePage(agent, repo, def);
        }
    }

    /// <summary>
    /// Second level: shows the current state-of-the-build for a combo.
    /// User can drill into individual agent runs from here.
    /// </summary>
    private void ShowStatePage(HealthAgentService agent, string repository, string definition)
    {
        _ui.TruncationEnabled = false;
        var showRawMarkdown = false;
        while (true)
        {
            var state = agent.GetCurrentState(repository, definition);
            var detailLines = new List<string>();
            if (state is not null)
            {
                if (showRawMarkdown)
                {
                    detailLines = state.Split('\n').Select(l => Markup.Escape(l)).ToList();
                }
                else
                {
                    detailLines = MarkdownRenderer.ToMarkupLines(state, _ui.ContentWidth);
                }
            }
            else
            {
                detailLines.Add("[dim]No state summary available yet.[/]");
            }

            _ui.RenderDetailPanel(
                ["Health", $"{Markup.Escape(repository)} / {Markup.Escape(definition)}"],
                null,
                detailLines,
                PanelRenderer.BuildCommandBarString(new List<CommandBarItem>
                {
                    new("Re-run", ConsoleKey.R, -2),
                    new("Gist", ConsoleKey.G, -3),
                    new("View runs", ConsoleKey.V, -4),
                    new(showRawMarkdown ? "Rendered" : "Markdown", ConsoleKey.M, -5),
                }));

            while (true)
            {
                var key = Console.ReadKey(true);
                if (_ui.HandleDetailScroll(key))
                {
                    continue;
                }

                switch (key.Key)
                {
                    case ConsoleKey.R:
                        AnsiConsole.MarkupLine("[dim]Running health analysis...[/]");
                        try
                        {
                            agent.RequestEvaluationAsync(repository, definition).GetAwaiter().GetResult();
                            AnsiConsole.MarkupLine("[green]Health analysis complete.[/]");
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]Analysis failed: {Markup.Escape(ex.Message)}[/]");
                        }
                        break;
                    case ConsoleKey.G:
                        CreateGist(repository, definition, state);
                        break;
                    case ConsoleKey.V:
                        ShowRunsPage(agent, repository, definition);
                        break;
                    case ConsoleKey.M:
                        showRawMarkdown = !showRawMarkdown;
                        break;
                    case ConsoleKey.Escape:
                        return;
                }
                break;
            }
        }
    }

    private static void CreateGist(string repository, string definition, string? markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            AnsiConsole.MarkupLine("[yellow]No state content to create a gist from.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
            Console.ReadKey(true);
            return;
        }

        AnsiConsole.MarkupLine("[dim]Creating gist...[/]");

        // Write content to a temp file for gh CLI
        var tempFile = Path.GetTempFileName();
        var fileName = $"{repository.Replace("/", "-")}-{definition.Replace(" ", "-")}-health.md";
        var tempMdFile = Path.Combine(Path.GetDirectoryName(tempFile)!, fileName);
        try
        {
            File.Move(tempFile, tempMdFile);
            File.WriteAllText(tempMdFile, $"# Health Report — {repository} / {definition}\n\n{markdownContent}");

            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "gh";
            process.StartInfo.Arguments = $"gist create --public \"{tempMdFile}\"";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                var url = output.Trim();
                AnsiConsole.MarkupLine($"[green]Gist created:[/] {Markup.Escape(url)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to create gist:[/] {Markup.Escape(error.Trim())}");
            }
        }
        finally
        {
            if (File.Exists(tempMdFile))
                File.Delete(tempMdFile);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }

        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    /// <summary>
    /// Third level: list of individual agent runs for a combo, most recent first.
    /// </summary>
    private void ShowRunsPage(HealthAgentService agent, string repository, string definition)
    {
        while (true)
        {
            var runs = agent.GetRecentRuns(repository, definition);
            if (runs.Count == 0)
            {
                _ui.RenderDetailPanel(
                    ["Health", $"{Markup.Escape(repository)}", "Runs"],
                    null,
                    ["[dim]No runs found.[/]"],
                    "[blue]Esc[/] Back");
                Console.ReadKey(true);
                return;
            }

            var items = runs.Select(r => r.Timestamp.Replace("_", " ")).ToList();

            var selected = _ui.SelectInPanel(
                ["Health", $"{Markup.Escape(repository)}", "Runs"],
                $"[dim]{runs.Count} run(s)[/]",
                items,
                new List<CommandBarItem>());
            if (selected < 0)
            {
                return;
            }

            ShowRunDetail(runs[selected]);
        }
    }

    private void ShowRunDetail(HealthRunInfo run)
    {
        _ui.TruncationEnabled = false;
        var showRawMarkdown = false;
        while (true)
        {
            List<string> detailLines;
            if (File.Exists(run.LogPath))
            {
                var content = File.ReadAllText(run.LogPath);
                if (showRawMarkdown)
                {
                    detailLines = content.Split('\n').Select(l => Markup.Escape(l)).ToList();
                }
                else
                {
                    detailLines = MarkdownRenderer.ToMarkupLines(content, _ui.ContentWidth);
                }
            }
            else
            {
                detailLines = ["[red]Log file not found.[/]"];
            }
            _ui.RenderDetailPanel(
                ["Health", "Run", Markup.Escape(run.Timestamp.Replace("_", " "))],
                null,
                detailLines,
                PanelRenderer.BuildCommandBarString(new List<CommandBarItem>
                {
                    new(showRawMarkdown ? "Rendered" : "Markdown", ConsoleKey.M, -5),
                }));

            while (true)
            {
                var key = Console.ReadKey(true);
                if (_ui.HandleDetailScroll(key))
                {
                    continue;
                }
                switch (key.Key)
                {
                    case ConsoleKey.M:
                        showRawMarkdown = !showRawMarkdown;
                        break;
                    case ConsoleKey.Escape:
                        return;
                    default:
                        continue;
                }
                break;
            }
        }
    }
}

