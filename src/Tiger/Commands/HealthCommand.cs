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

            var selected = SelectWithEscape("Select a pipeline to view health state:", items);
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
            AnsiConsole.MarkupLine("[dim]Press Enter to view agent runs, Esc/B to go back[/]");

            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.B)
                return;
            if (key.Key == ConsoleKey.Enter)
            {
                ShowRunsPage(agent, repository, definition);
            }
        }
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

            var selected = SelectWithEscape("Select a run to view full log:", items);
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
        AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
        Console.ReadKey(true);
    }

    // ── Selection helper ────────────────────────────────────────────

    /// <summary>
    /// Arrow-key selection list with Escape/B to go back.
    /// Returns selected index or -1 if user backs out.
    /// </summary>
    private static int SelectWithEscape(string title, List<string> items, int pageSize = 20)
    {
        if (items.Count == 0)
            return -1;

        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(title)}[/]");
        AnsiConsole.WriteLine();

        var selected = 0;
        var scrollOffset = 0;
        var visibleCount = Math.Min(pageSize, items.Count);
        var startTop = Console.CursorTop;

        while (true)
        {
            Console.SetCursorPosition(0, startTop);

            for (var i = 0; i < visibleCount; i++)
            {
                var idx = scrollOffset + i;
                if (idx >= items.Count)
                    break;

                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                var text = Markup.Escape(items[idx]);
                if (idx == selected)
                {
                    AnsiConsole.MarkupLine($"  [blue]>[/] [bold]{text}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"    {text}");
                }
            }

            if (items.Count > visibleCount)
            {
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                AnsiConsole.MarkupLine($"  [dim]({selected + 1}/{items.Count})[/]");
            }

            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, Console.CursorTop);
            AnsiConsole.MarkupLine("[dim]  ↑↓ navigate  Enter select  Esc/B back[/]");

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selected > 0)
                    {
                        selected--;
                        if (selected < scrollOffset)
                            scrollOffset = selected;
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (selected < items.Count - 1)
                    {
                        selected++;
                        if (selected >= scrollOffset + visibleCount)
                            scrollOffset = selected - visibleCount + 1;
                    }
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape:
                case ConsoleKey.B:
                    return -1;
            }
        }
    }
}
