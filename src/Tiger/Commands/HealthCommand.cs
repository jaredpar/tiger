using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// Interactive viewer for health agent results.
/// Shows repo+pipeline combos, drill into state-of-the-build, then into individual runs.
/// </summary>
public sealed class HealthCommand : AsyncCommand
{
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
    private static void ShowCombosPage(HealthAgentService agent)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold underline]Tiger Health Reports[/]");
            AnsiConsole.WriteLine();

            var runs = agent.GetRecentRuns();
            if (runs.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No health reports available yet. The agent runs every 15 minutes.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to exit...[/]");
                Console.ReadKey(true);
                return;
            }

            // Get distinct combos
            var combos = runs
                .Select(r => (r.Repository, r.Definition))
                .Distinct()
                .ToList();

            var items = combos.Select(c => $"{c.Repository} / {c.Definition}").ToList();

            var selected = BrowserUI.SelectWithEscape("Select a pipeline:", items);
            if (selected < 0)
                return;

            var (repo, def) = combos[selected];
            ShowStatePage(agent, repo, def);
        }
    }

    /// <summary>
    /// Second level: shows the current state-of-the-build for a combo.
    /// User can drill into individual agent runs from here.
    /// </summary>
    private static void ShowStatePage(HealthAgentService agent, string repository, string definition)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold underline]{Markup.Escape(repository)} / {Markup.Escape(definition)}[/]");
            AnsiConsole.WriteLine();

            var state = agent.GetCurrentState(repository, definition);
            if (state is not null)
            {
                MarkdownRenderer.Render(state);
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]No state summary available yet.[/]");
            }

            AnsiConsole.WriteLine();

            var menuItems = new List<string>
            {
                $"[blue](R)[/] Re-run health analysis",
                $"[blue](G)[/] Create public gist",
                $"[blue](V)[/] View agent runs",
            };

            var extraKeys = new Dictionary<ConsoleKey, int>
            {
                [ConsoleKey.R] = 0,
                [ConsoleKey.G] = 1,
                [ConsoleKey.V] = 2,
            };

            var menuChoice = BrowserUI.SelectWithEscape("", menuItems, useMarkup: true, extraKeys: extraKeys);

            switch (menuChoice)
            {
                case 0:
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
                case 1:
                    CreateGist(repository, definition, state);
                    break;
                case 2:
                    ShowRunsPage(agent, repository, definition);
                    break;
                default:
                    return;
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
    private static void ShowRunsPage(HealthAgentService agent, string repository, string definition)
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold underline]Agent Runs — {Markup.Escape(repository)} / {Markup.Escape(definition)}[/]");
            AnsiConsole.WriteLine();

            var runs = agent.GetRecentRuns(repository, definition);
            if (runs.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No runs found.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
                Console.ReadKey(true);
                return;
            }

            var items = runs.Select(r => r.Timestamp.Replace("_", " ")).ToList();

            var selected = BrowserUI.SelectWithEscape("Select a run:", items);
            if (selected < 0)
                return;

            ShowRunDetail(runs[selected]);
        }
    }

    /// <summary>
    /// Fourth level: full log of a single agent run.
    /// </summary>
    private static void ShowRunDetail(HealthRunInfo run)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold underline]Health Report — {Markup.Escape(run.Timestamp.Replace("_", " "))}[/]");
        AnsiConsole.WriteLine();

        if (File.Exists(run.LogPath))
        {
            var content = File.ReadAllText(run.LogPath);
            MarkdownRenderer.Render(content);
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Log file not found.[/]");
        }

        AnsiConsole.WriteLine();

        var menuItems = new List<string>
        {
            $"[blue](B)[/] Back",
        };

        var extraKeys = new Dictionary<ConsoleKey, int>
        {
            [ConsoleKey.B] = 0,
        };

        BrowserUI.SelectWithEscape("", menuItems, useMarkup: true, extraKeys: extraKeys);
    }
}
