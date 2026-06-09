using System.Text.Json;
using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser for viewing and managing Copilot Coding Agent tasks.
/// Lists tasks from <c>gh agent-task list</c> and highlights tasks
/// submitted from Tiger.
/// </summary>
public sealed class AgentBrowser
{
    private readonly TigerDatabase _db;

    public AgentBrowser(TigerDatabase db)
    {
        _db = db;
    }

    public void Browse()
    {
        while (true)
        {
            var tasks = LoadTasks();
            if (tasks is null)
            {
                PanelLayout.RenderDetailPanel(
                    ["Agents"],
                    null,
                    () => PanelLayout.RenderPanelLine("[red]Failed to load agent tasks from gh CLI.[/]"),
                    "[blue]Esc[/] Back");
                Console.ReadKey(true);
                return;
            }

            if (tasks.Count == 0)
            {
                PanelLayout.RenderDetailPanel(
                    ["Agents"],
                    null,
                    () => PanelLayout.RenderPanelLine("[dim]No agent tasks found.[/]"),
                    "[blue]R[/]efresh   [blue]Esc[/] Back");

                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        return;
                    }
                    if (key.Key == ConsoleKey.R)
                    {
                        break;
                    }
                }
                continue;
            }

            var trackedIds = _db.GetAgentTaskSessionIds();

            var items = new List<string>();
            foreach (var task in tasks)
            {
                var isTracked = task.Id is not null && trackedIds.Contains(task.Id);
                var stateIcon = FormatState(task.State);
                var repo = task.Repository ?? "unknown";
                var name = task.Name ?? "unnamed";
                if (name.Length > 60)
                {
                    name = name[..57] + "...";
                }
                var prInfo = task.PullRequestNumber is not null
                    ? $" PR #{task.PullRequestNumber}"
                    : "";
                var tigerMark = isTracked ? " [yellow]★[/]" : "";
                items.Add($"{stateIcon} {Markup.Escape(name)}{prInfo}{tigerMark}  [dim]{Markup.Escape(repo)}[/]");
            }

            var commands = new List<CommandBarItem>
            {
                new("Refresh", ConsoleKey.R, -2),
            };

            var selected = PanelLayout.SelectInPanel(
                ["Agents"],
                $"[dim]{tasks.Count} task(s)[/]  [yellow]★[/] = submitted from Tiger",
                items,
                commands);

            if (selected == -1)
            {
                return;
            }
            if (selected == -2)
            {
                continue;
            }

            ShowTaskDetail(tasks[selected], trackedIds);
        }
    }

    private void ShowTaskDetail(AgentTaskInfo task, HashSet<string> trackedIds)
    {
        while (true)
        {
            var isTracked = task.Id is not null && trackedIds.Contains(task.Id);

            var menuItems = new List<string>();
            var actions = new List<string>();
            var extraKeys = new Dictionary<ConsoleKey, int>();

            if (task.PullRequestUrl is not null)
            {
                menuItems.Add("[blue]O[/]pen PR in browser");
                extraKeys[ConsoleKey.O] = menuItems.Count - 1;
                actions.Add("open_pr");
            }

            menuItems.Add("[blue]V[/]iew logs");
            extraKeys[ConsoleKey.V] = menuItems.Count - 1;
            actions.Add("logs");

            menuItems.Add("[blue]R[/]efresh");
            extraKeys[ConsoleKey.R] = menuItems.Count - 1;
            actions.Add("refresh");

            // Use RenderDetailPanel for the header info, then SelectInPanel for menu
            PanelLayout.RenderDetailPanel(
                ["Agents", Markup.Escape(task.Name ?? "unnamed")],
                $"{FormatState(task.State)}  {Markup.Escape(task.Repository ?? "unknown")}",
                () =>
                {
                    PanelLayout.RenderField("Name", Markup.Escape(task.Name ?? "unnamed"));
                    PanelLayout.RenderField("State", FormatState(task.State));
                    PanelLayout.RenderField("Repository", Markup.Escape(task.Repository ?? "unknown"));
                    if (task.Id is not null)
                    {
                        PanelLayout.RenderField("Session", Markup.Escape(task.Id));
                    }
                    if (task.CreatedAt is not null)
                    {
                        PanelLayout.RenderField("Created", BrowserUI.FormatTime(task.CreatedAt));
                    }
                    if (task.UpdatedAt is not null)
                    {
                        PanelLayout.RenderField("Updated", BrowserUI.FormatTime(task.UpdatedAt));
                    }
                    if (task.PullRequestNumber is not null && task.PullRequestUrl is not null)
                    {
                        PanelLayout.RenderField("Pull Request",
                            $"{BrowserUI.FormatLink(task.PullRequestUrl, $"PR #{task.PullRequestNumber}")} ({Markup.Escape(task.PullRequestState ?? "unknown")})");
                    }
                    else if (task.PullRequestNumber is not null)
                    {
                        PanelLayout.RenderField("Pull Request", $"#{task.PullRequestNumber}");
                    }
                    if (isTracked)
                    {
                        PanelLayout.RenderField("Source", "[yellow]Submitted from Tiger[/]");
                    }
                },
                "[blue]O[/]pen PR   [blue]V[/]iew logs   [blue]R[/]efresh   [blue]Esc[/] Back");

            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
            {
                return;
            }

            // Map key to action
            string? action = key.Key switch
            {
                ConsoleKey.O when task.PullRequestUrl is not null => "open_pr",
                ConsoleKey.V => "logs",
                ConsoleKey.R => "refresh",
                _ => null,
            };

            switch (action)
            {
                case "open_pr":
                    var openProcess = new System.Diagnostics.Process();
                    openProcess.StartInfo.FileName = "gh";
                    openProcess.StartInfo.Arguments = $"pr view {task.PullRequestNumber} -R {task.Repository} --web";
                    openProcess.StartInfo.UseShellExecute = false;
                    openProcess.StartInfo.RedirectStandardOutput = true;
                    openProcess.StartInfo.RedirectStandardError = true;
                    openProcess.Start();
                    openProcess.WaitForExit();
                    continue;
                case "logs":
                    ShowLogs(task);
                    continue;
                case "refresh":
                    var refreshed = LoadTaskById(task.Id!);
                    if (refreshed is not null)
                    {
                        task = refreshed;
                    }
                    continue;
            }
        }
    }

    private static void ShowLogs(AgentTaskInfo task)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold]Logs: {Markup.Escape(task.Name ?? "unnamed")}[/]");
        AnsiConsole.WriteLine();

        var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "gh";
        process.StartInfo.Arguments = $"agent-task view {task.Id} --log -R {task.Repository}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
        {
            // Render as markdown since logs may contain structured content
            MarkdownRenderer.Render(output);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error.Trim())}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No logs available.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
        Console.ReadKey(true);
    }

    private static List<AgentTaskInfo>? LoadTasks()
    {
        try
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "gh";
            process.StartInfo.Arguments = "agent-task list --json id,name,state,repository,createdAt,updatedAt,pullRequestNumber,pullRequestUrl,pullRequestState --limit 30";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<List<AgentTaskInfo>>(output, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static AgentTaskInfo? LoadTaskById(string sessionId)
    {
        try
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "gh";
            process.StartInfo.Arguments = $"agent-task view {sessionId} --json id,name,state,repository,createdAt,updatedAt,pullRequestNumber,pullRequestUrl,pullRequestState";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<AgentTaskInfo>(output, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatState(string? state) => state switch
    {
        "completed" => "[green]✓ completed[/]",
        "in_progress" => "[blue]● in progress[/]",
        "cancelled" => "[dim]✕ cancelled[/]",
        "waiting" => "[yellow]◌ waiting[/]",
        "queued" => "[yellow]◌ queued[/]",
        _ => Markup.Escape(state ?? "unknown"),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class AgentTaskInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("state")]
        public string? State { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("repository")]
        public string? Repository { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
        public string? CreatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("pullRequestNumber")]
        public int? PullRequestNumber { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("pullRequestUrl")]
        public string? PullRequestUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("pullRequestState")]
        public string? PullRequestState { get; set; }
    }
}
