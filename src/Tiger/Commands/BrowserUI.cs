using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Shared UI utilities for browser-style CLI views.
/// Contains only rendering, input, and formatting helpers — no DB or navigation logic.
/// </summary>
public static class BrowserUI
{
    /// <summary>
    /// Key-driven selection list that supports Escape to go back.
    /// Returns the selected index, or -1 if the user pressed Escape.
    /// Extra keys can be mapped to return specific negative values.
    /// </summary>
    public static int SelectWithEscape(string title, List<string> items, int pageSize = 20,
        Dictionary<ConsoleKey, int>? extraKeys = null, bool useMarkup = false,
        int startIndex = 0, HashSet<int>? skipIndices = null, string? hotkeys = null)
    {
        if (items.Count == 0) return -1;

        var selected = Math.Clamp(startIndex, 0, items.Count - 1);
        if (skipIndices is not null)
        {
            while (selected < items.Count && skipIndices.Contains(selected))
                selected++;
            if (selected >= items.Count)
            {
                selected = Math.Clamp(startIndex, 0, items.Count - 1);
                while (selected > 0 && skipIndices.Contains(selected))
                    selected--;
            }
        }
        var scrollOffset = Math.Max(0, selected - pageSize + 1);
        var visibleCount = Math.Min(pageSize, items.Count);
        var startTop = Console.CursorTop;

        while (true)
        {
            Console.SetCursorPosition(0, startTop);

            for (var i = 0; i < visibleCount; i++)
            {
                var idx = scrollOffset + i;
                if (idx >= items.Count) break;

                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                var text = useMarkup ? items[idx] : Markup.Escape(items[idx]);
                if (idx == selected)
                {
                    AnsiConsole.MarkupLine(useMarkup ? $"  [blue]>[/] {text}" : $"  [blue]>[/] [bold]{text}[/]");
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
            var builtIn = "[blue]↑↓[/] Navigate  [blue]Enter[/] Select  [blue]Esc[/] Back";
            var footer = hotkeys is not null
                ? $"  {hotkeys}   {builtIn}"
                : $"  {builtIn}";
            AnsiConsole.MarkupLine(footer);

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selected > 0)
                    {
                        selected--;
                        while (selected > 0 && skipIndices is not null && skipIndices.Contains(selected))
                            selected--;
                        if (skipIndices is not null && skipIndices.Contains(selected))
                            selected++;
                        if (selected < scrollOffset)
                            scrollOffset = selected;
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (selected < items.Count - 1)
                    {
                        selected++;
                        while (selected < items.Count - 1 && skipIndices is not null && skipIndices.Contains(selected))
                            selected++;
                        if (skipIndices is not null && skipIndices.Contains(selected))
                            selected--;
                        if (selected >= scrollOffset + visibleCount)
                            scrollOffset = selected - visibleCount + 1;
                    }
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape:
                    return -1;
                case ConsoleKey.B:
                    if (extraKeys is null || !extraKeys.ContainsKey(ConsoleKey.B))
                        return -1;
                    goto default;
                default:
                    if (extraKeys is not null && extraKeys.TryGetValue(key.Key, out var result))
                        return result;
                    break;
            }
        }
    }

    /// <summary>
    /// Prompts for a text pattern with Escape to cancel.
    /// </summary>
    public static string? PromptPattern(string prompt)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(prompt)}[/]");
        AnsiConsole.MarkupLine("[dim]Press Esc to cancel[/]");
        AnsiConsole.Markup("[blue]> [/]");

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
                return null;
            if (key.Key == ConsoleKey.Enter)
            {
                AnsiConsole.WriteLine();
                var result = buffer.ToString().Trim();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    Console.Write("\b \b");
                }
                continue;
            }
            if (key.KeyChar >= 32)
            {
                buffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// Formats an ISO time string to local 12-hour time.
    /// </summary>
    /// <summary>
    /// Selection menu for build kind filter (pr/ci). Returns null if cancelled or "all" selected.
    /// </summary>
    public static string? PromptKindFilter()
    {
        AnsiConsole.WriteLine();
        var choices = new[] { "all", "pr", "ci" };
        var selected = SelectWithEscape("Select build kind:", choices.ToList(), pageSize: 5);
        if (selected < 0) return null;
        return choices[selected] == "all" ? null : choices[selected];
    }

    /// <summary>
    /// Applies a kind filter (pr/ci) to a SQL WHERE clause list.
    /// </summary>
    public static void ApplyKindFilter(string? kindPattern, List<string> where, string prColumnExpr = "b.pr_number")
    {
        if (kindPattern is null) return;
        if (kindPattern.Equals("pr", StringComparison.OrdinalIgnoreCase))
            where.Add($"{prColumnExpr} IS NOT NULL");
        else if (kindPattern.Equals("ci", StringComparison.OrdinalIgnoreCase))
            where.Add($"{prColumnExpr} IS NULL");
    }

    public static string FormatTime(string? isoTime)
    {
        if (isoTime is null) return "—";
        if (DateTime.TryParse(isoTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");
        return isoTime;
    }

    public static string FormatResult(string? result) => result switch
    {
        "succeeded" => "[green]✓ succeeded[/]",
        "failed" => "[red]✗ failed[/]",
        "partiallySucceeded" => "[yellow]⚠ partial[/]",
        "canceled" => "[dim]canceled[/]",
        null => "[dim]—[/]",
        _ => result,
    };

    public static string FormatResultIcon(string? result) => result switch
    {
        "succeeded" => "[green]✓[/]",
        "failed" => "[red]✗[/]",
        "partiallySucceeded" => "[yellow]⚠[/]",
        "canceled" => "[dim]⊘[/]",
        _ => "[dim]—[/]",
    };

    /// <summary>
    /// Formats a build row for display in a selection list.
    /// </summary>
    public static string FormatBuildChoice(int buildId, string definitionName, string? result,
        string? finishTime, int? prNumber, bool pending = false)
    {
        var icon = FormatResultIcon(result);
        var pr = prNumber is not null ? $" PR#{prNumber}" : "";
        var time = FormatTime(finishTime);
        var pendingIcon = pending ? " ⏳" : "";
        return $"{icon} {buildId} {Markup.Escape(definitionName)} {time}{pr}{pendingIcon}";
    }

    /// <summary>
    /// Renders a test failure detail view (error, stack trace, helix info).
    /// </summary>
    public static void RenderTestDetail(TestDetailInfo info)
    {
        AnsiConsole.MarkupLine("[bold underline]Test Failure Detail[/]");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(info.TestName)}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(info.Org)}/{Markup.Escape(info.Project)}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine($"[bold]Last failed in:[/] Build #{info.BuildId}, {Markup.Escape(info.RunName)}");
        AnsiConsole.MarkupLine($"[bold]Failed in:[/] {info.BuildCount} build(s)");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Error:[/]");
        if (!string.IsNullOrWhiteSpace(info.ErrorMessage))
        {
            var errorLines = info.ErrorMessage.ReplaceLineEndings("\n").Split('\n');
            foreach (var line in errorLines.Take(5))
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(line)}[/]");
            if (errorLines.Length > 5)
                AnsiConsole.MarkupLine($"  [dim]... ({errorLines.Length - 5} more lines)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [dim]No error message available[/]");
        }
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Stack Trace:[/]");
        if (!string.IsNullOrWhiteSpace(info.StackTrace))
        {
            var stackLines = info.StackTrace.ReplaceLineEndings("\n").Split('\n');
            foreach (var line in stackLines.Take(10))
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(line)}[/]");
            if (stackLines.Length > 10)
                AnsiConsole.MarkupLine($"  [dim]... ({stackLines.Length - 10} more lines)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [dim]No stack trace available[/]");
        }
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Helix:[/]");
        if (info.HelixJobName is not null)
        {
            AnsiConsole.MarkupLine($"  [bold]Job:[/] {Markup.Escape(info.HelixJobName)}");
            if (info.HelixWorkItemName is not null)
            {
                AnsiConsole.MarkupLine($"  [bold]Work Item:[/] {Markup.Escape(info.HelixWorkItemName)}");
                var consoleUrl = HelixClient.GetConsoleUrl(info.HelixJobName, info.HelixWorkItemName);
                AnsiConsole.MarkupLine($"  [bold]Console:[/] {FormatLink(consoleUrl, "Console Log")}");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [dim]No Helix information available[/]");
        }
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Converts a user pattern to a SQL LIKE pattern or exact match.
    /// Default: contains match (ros → %ros%). Trailing ! means exact match.
    /// * is a wildcard (dotnet/* → dotnet/%).
    /// </summary>
    public static (string Pattern, bool IsExact) ToSqlPattern(string input)
    {
        if (input.EndsWith('!'))
            return (input[..^1], true);

        var pattern = input.Replace("*", "%");
        if (!pattern.Contains('%'))
            pattern = $"%{pattern}%";
        return (pattern, false);
    }

    /// <summary>
    /// Formats a clickable link for terminal display. Uses OSC 8 link markup
    /// for terminals that support it, and blue underline styling so the link
    /// is visually recognizable in all terminals.
    /// </summary>
    public static string FormatLink(string url, string displayText) =>
        $"[link={url}][blue underline]{Markup.Escape(displayText)}[/][/]";

    /// <summary>
    /// Data model for test failure detail rendering.
    /// </summary>
    public record TestDetailInfo(
        string TestName, string Org, string Project,
        int BuildId, string RunName, int BuildCount,
        string? ErrorMessage, string? StackTrace,
        string? HelixJobName, string? HelixWorkItemName);

    /// <summary>
    /// Loads test detail info from the database.
    /// </summary>
    public static TestDetailInfo? LoadTestDetail(TigerDatabase db, string org, string project, string testName)
    {
        var detail = db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT tr.error_message, tr.stack_trace, tr.helix_job_name, tr.helix_work_item_name,
                       r.build_id, r.run_name
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE tr.organization = @org AND tr.project = @proj
                      AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
                ORDER BY r.build_id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@proj", project);
            cmd.Parameters.AddWithValue("@testName", testName);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return (Found: false, ErrorMessage: (string?)null, StackTrace: (string?)null,
                    HelixJob: (string?)null, HelixWorkItem: (string?)null, BuildId: 0, RunName: string.Empty);
            }

            return (
                Found: true,
                ErrorMessage: reader.IsDBNull(0) ? null : reader.GetString(0),
                StackTrace: reader.IsDBNull(1) ? null : reader.GetString(1),
                HelixJob: reader.IsDBNull(2) ? null : reader.GetString(2),
                HelixWorkItem: reader.IsDBNull(3) ? null : reader.GetString(3),
                BuildId: reader.GetInt32(4),
                RunName: reader.GetString(5));
        });

        if (!detail.Found)
        {
            return null;
        }

        var buildCount = db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT COUNT(DISTINCT r.build_id)
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE tr.organization = @org AND tr.project = @proj
                      AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@proj", project);
            cmd.Parameters.AddWithValue("@testName", testName);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        return new TestDetailInfo(testName, org, project, detail.BuildId, detail.RunName, buildCount,
            detail.ErrorMessage, detail.StackTrace, detail.HelixJob, detail.HelixWorkItem);
    }
}
