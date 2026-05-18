using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser-like navigation for exploring builds, tests, and failures.
/// Supports back/forward navigation like a web browser.
/// </summary>
public sealed class BuildBrowser
{
    private readonly TigerDatabase _db;
    private readonly Func<string, string, AzdoClient> _clientFactory;
    private readonly string _configDirectory;
    private readonly List<Page> _history = [];
    private readonly BuildFilter _filter;
    private int _position = -1;
    private int _selectedBuildIndex;
    private List<BuildRow> _lastBuilds = [];

    public BuildBrowser(TigerDatabase db, Func<string, string, AzdoClient> clientFactory, string configDirectory)
    {
        _db = db;
        _clientFactory = clientFactory;
        _configDirectory = configDirectory;
        _filter = BuildFilter.Load(configDirectory);
    }

    private void SaveFilter() => _filter.Save(_configDirectory);

    /// <summary>
    /// Entry point — shows the build list and enters the navigation loop.
    /// Returns when the user exits back to the dashboard.
    /// </summary>
    public void Browse()
    {
        Push(new BuildListPage());
        RunLoop();
    }

    private void RunLoop()
    {
        while (_position >= 0)
        {
            AnsiConsole.Clear();
            var action = Render(_history[_position]);

            switch (action)
            {
                case NavAction.Push push:
                    Push(push.Page);
                    break;
                case NavAction.Replace replace:
                    _history[_position] = replace.Page;
                    break;
                case NavAction.Back:
                    if (_position > 0)
                        _position--;
                    else
                        return; // exit to dashboard
                    break;
                case NavAction.Forward:
                    if (_position < _history.Count - 1)
                        _position++;
                    break;
            }
        }
    }

    private void Push(Page page)
    {
        // Truncate forward history (browser behavior)
        if (_position < _history.Count - 1)
            _history.RemoveRange(_position + 1, _history.Count - _position - 1);

        _history.Add(page);
        _position = _history.Count - 1;
    }

    private NavAction Render(Page page) => page switch
    {
        BuildListPage => RenderBuildList(),
        BuildDetailPage p => RenderBuildDetail(p),
        TestListPage p => RenderTestList(p),
        TestDetailPage p => RenderTestDetail(p),
        TestBuildsPage p => RenderTestBuilds(p),
        JobListPage p => RenderJobList(p),
        _ => NavAction.Exit,
    };

    // ── Build List ──────────────────────────────────────────────────

    private NavAction RenderBuildList()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold underline]Builds[/]");
            if (_filter.IsActive)
                AnsiConsole.MarkupLine($"[dim]Filter: {Markup.Escape(_filter.ToString())}[/]");
            AnsiConsole.MarkupLine(_filter.IsActive
                ? "[dim]E edit filter  F filter menu  C clear  H help  Esc back[/]"
                : "[dim]E edit filter  F filter menu  H help  Esc back[/]");
            AnsiConsole.WriteLine();

            var builds = QueryBuilds();

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine(_filter.IsActive
                    ? "[yellow]No builds match the current filter.[/]"
                    : "[yellow]No builds ingested yet.[/]");
                AnsiConsole.MarkupLine("[dim]Press E to edit filter, F for filter menu, Esc to go back...[/]");

                var emptyKey = Console.ReadKey(true);
                if (emptyKey.Key == ConsoleKey.E)
                {
                    EditFilter();
                    continue;
                }
                if (emptyKey.Key == ConsoleKey.F)
                {
                    ShowFilterMenu();
                    continue;
                }
                if (emptyKey.Key == ConsoleKey.H)
                {
                    ShowFilterHelp();
                    continue;
                }
                if (emptyKey.Key == ConsoleKey.C)
                {
                    _filter.Clear();
                    SaveFilter();
                    continue;
                }
                return NavAction.Back.Instance;
            }

            AnsiConsole.MarkupLine($"[dim]{builds.Count} builds[/]");
            AnsiConsole.WriteLine();

            var choices = builds.Select(b =>
            {
                var resultIcon = b.Result switch
                {
                    "succeeded" => "[green]✓[/]",
                    "failed" => "[red]X[/]",
                    "partiallySucceeded" => "[yellow]![/]",
                    "canceled" => "[dim]-[/]",
                    _ => "[dim]-[/]",
                };
                var pr = b.PrNumber is not null ? $" PR#{b.PrNumber}" : "";
                var pending = b.IngestionStatus != "complete" ? " ..." : "";
                var time = FormatTime(b.FinishTime);
                return $"{resultIcon} {b.BuildId} {Markup.Escape(b.DefinitionName)} {time}{pr}{pending}";
            }).ToList();

            _lastBuilds = builds;

            var selected = SelectWithEscape("Select a build:", choices,
                extraKeys: new Dictionary<ConsoleKey, int> {
                    { ConsoleKey.E, -5 },
                    { ConsoleKey.F, -2 },
                    { ConsoleKey.H, -3 },
                    { ConsoleKey.C, -4 },
                },
                useMarkup: true,
                startIndex: _selectedBuildIndex);

            if (selected == -5) // E pressed
            {
                EditFilter();
                continue;
            }
            if (selected == -2) // F pressed
            {
                ShowFilterMenu();
                continue;
            }
            if (selected == -3) // H pressed
            {
                ShowFilterHelp();
                continue;
            }
            if (selected == -4) // C pressed
            {
                _filter.Clear();
                SaveFilter();
                continue;
            }
            if (selected < 0)
                return NavAction.Back.Instance;

            _selectedBuildIndex = selected;
            var b2 = builds[selected];
            return new NavAction.Push(new BuildDetailPage(b2.Org, b2.Project, b2.BuildId));
        }
    }

    private List<BuildRow> QueryBuilds()
    {
        var builds = new List<BuildRow>();
        using var cmd = _db.Connection.CreateCommand();

        var where = new List<string>();
        if (_filter.RepoPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.RepoPattern);
            where.Add(isExact ? "b.repository_name = @repo" : "b.repository_name LIKE @repo");
            cmd.Parameters.AddWithValue("@repo", pattern);
        }
        if (_filter.DefinitionPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.DefinitionPattern);
            where.Add(isExact ? "b.definition_name = @def" : "b.definition_name LIKE @def");
            cmd.Parameters.AddWithValue("@def", pattern);
        }
        if (_filter.ResultPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.ResultPattern);
            where.Add(isExact ? "b.result = @result" : "b.result LIKE @result");
            cmd.Parameters.AddWithValue("@result", pattern);
        }
        if (_filter.IdPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.IdPattern);
            where.Add(isExact ? "CAST(b.build_id AS TEXT) = @bid" : "CAST(b.build_id AS TEXT) LIKE @bid");
            cmd.Parameters.AddWithValue("@bid", pattern);
        }
        if (_filter.KindPattern is not null)
        {
            if (_filter.KindPattern.Equals("pr", StringComparison.OrdinalIgnoreCase))
                where.Add("b.pr_number IS NOT NULL");
            else if (_filter.KindPattern.Equals("ci", StringComparison.OrdinalIgnoreCase))
                where.Add("b.pr_number IS NULL");
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT b.organization, b.project, b.build_id, b.build_number, b.definition_name,
                   b.result, b.source_branch, b.pr_number, b.finish_time,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM build_ingestion_tasks t
                       WHERE t.organization = b.organization AND t.project = b.project
                         AND t.build_id = b.build_id AND t.status != 'complete'
                   ) THEN 'pending' ELSE 'complete' END as ingestion_status,
                   b.definition_id, b.repository_name
            FROM builds b
            {whereClause}
            ORDER BY b.finish_time DESC, b.ingested_at DESC
            LIMIT 50
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            builds.Add(new BuildRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return builds;
    }

    private void EditFilter()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Edit Filter[/]");
        AnsiConsole.MarkupLine("[dim]Syntax: repo:VALUE def:VALUE num:VALUE result:VALUE[/]");
        AnsiConsole.MarkupLine("[dim]Examples: repo:roslyn  result:failed  repo:dotnet/* def:*-CI[/]");
        AnsiConsole.MarkupLine("[dim]Append ! for exact match: repo:dotnet/roslyn![/]");
        AnsiConsole.MarkupLine("[dim]Press Esc to cancel[/]");
        AnsiConsole.WriteLine();
        if (_filter.IsActive)
            AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(_filter.ToString())}[/]");
        AnsiConsole.Markup("[blue]> [/]");

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape)
                return; // keep existing filter
            if (key.Key == ConsoleKey.Enter)
            {
                AnsiConsole.WriteLine();
                var input = buffer.ToString().Trim();
                if (!string.IsNullOrEmpty(input))
                    _filter.ParseExpression(input);
                SaveFilter();
                return;
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

    private void ShowFilterMenu()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Set Filter[/]");
        AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(_filter.ToString())}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [blue]R[/] Filter by repository");
        AnsiConsole.MarkupLine("  [blue]D[/] Filter by definition");
        AnsiConsole.MarkupLine("  [blue]I[/] Filter by build ID");
        AnsiConsole.MarkupLine("  [blue]O[/] Filter by outcome (failed, succeeded, partiallySucceeded)");
        AnsiConsole.MarkupLine("  [blue]K[/] Filter by kind (pr, ci)");
        AnsiConsole.MarkupLine("  [blue]C[/] Clear all filters");
        AnsiConsole.MarkupLine("  [blue]Esc[/] Cancel");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.R:
                _filter.RepoPattern = PromptPattern("Repository pattern (e.g. roslyn, dotnet/*):");
                break;
            case ConsoleKey.D:
                _filter.DefinitionPattern = PromptPattern("Definition pattern (e.g. ci, roslyn-CI*):");
                break;
            case ConsoleKey.I:
                _filter.IdPattern = PromptPattern("Build ID pattern (e.g. 1423*, 142333):");
                break;
            case ConsoleKey.O:
                _filter.ResultPattern = PromptResultFilter();
                break;
            case ConsoleKey.K:
                _filter.KindPattern = PromptKindFilter();
                break;
            case ConsoleKey.C:
                _filter.Clear();
                break;
        }
        SaveFilter();
    }

    /// <summary>
    /// Prompts for a text pattern. Supports Escape to cancel (returns null).
    /// </summary>
    private static string? PromptPattern(string prompt)
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
            if (key.KeyChar >= 32) // printable
            {
                buffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    /// <summary>
    /// Selection menu for outcome filter. Returns null if cancelled.
    /// </summary>
    private static string? PromptResultFilter()
    {
        AnsiConsole.WriteLine();
        var choices = new[] { "all", "failed", "succeeded", "partiallySucceeded" };
        var selected = SelectWithEscape("Select outcome:", choices.ToList(), pageSize: 5);
        if (selected < 0) return null; // cancelled
        return choices[selected] == "all" ? null : choices[selected];
    }

    /// <summary>
    /// Selection menu for kind filter. Returns null if cancelled.
    /// </summary>
    private static string? PromptKindFilter()
    {
        AnsiConsole.WriteLine();
        var choices = new[] { "all", "pr", "ci" };
        var selected = SelectWithEscape("Select build kind:", choices.ToList(), pageSize: 5);
        if (selected < 0) return null; // cancelled
        return choices[selected] == "all" ? null : choices[selected];
    }

    private static void ShowFilterHelp()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Filter Help[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Quick filter (E):[/]");
        AnsiConsole.MarkupLine("  Type an expression like: [blue]repo:roslyn def:ci[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Matching (default: contains / LIKE):[/]");
        AnsiConsole.MarkupLine("  [dim]ros → matches 'dotnet/roslyn', 'roslyn-CI', etc.[/]");
        AnsiConsole.MarkupLine("  [dim]dotnet/* → matches 'dotnet/roslyn', 'dotnet/runtime'[/]");
        AnsiConsole.MarkupLine("  [dim]14* → matches build numbers starting with '14'[/]");
        AnsiConsole.MarkupLine("  [dim]*-CI → matches definition names ending with '-CI'[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Exact match (append !):[/]");
        AnsiConsole.MarkupLine("  [dim]dotnet/roslyn! → matches exactly 'dotnet/roslyn'[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Filter prefixes:[/]");
        AnsiConsole.MarkupLine("  [blue]repo:[/]    Repository name");
        AnsiConsole.MarkupLine("  [blue]def:[/]     Definition/pipeline name");
        AnsiConsole.MarkupLine("  [blue]id:[/]      Build ID (e.g. 1423*, 142333)");
        AnsiConsole.MarkupLine("  [blue]result:[/]  Outcome (failed, succeeded, partiallySucceeded)");
        AnsiConsole.MarkupLine("  [blue]kind:[/]    Build kind (pr, ci)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── Build Detail ────────────────────────────────────────────────

    private NavAction RenderBuildDetail(BuildDetailPage page)
    {
        Console.SetCursorPosition(0, 0);

        // Header info from DB
        using var buildCmd = _db.Connection.CreateCommand();
        buildCmd.CommandText = """
            SELECT build_number, definition_name, result, source_branch, pr_number, finish_time, repository_name
            FROM builds
            WHERE organization = @org AND project = @proj AND build_id = @buildId
            """;
        buildCmd.Parameters.AddWithValue("@org", page.Org);
        buildCmd.Parameters.AddWithValue("@proj", page.Project);
        buildCmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var reader = buildCmd.ExecuteReader();
        if (!reader.Read())
        {
            AnsiConsole.MarkupLine("[red]Build not found.[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var buildNumber = reader.GetString(0);
        var defName = reader.GetString(1);
        var result = reader.IsDBNull(2) ? null : reader.GetString(2);
        var branch = reader.GetString(3);
        var prNumber = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
        var finishTime = reader.IsDBNull(5) ? null : reader.GetString(5);
        var repoName = reader.IsDBNull(6) ? null : reader.GetString(6);
        reader.Close();

        var url = $"https://dev.azure.com/{Uri.EscapeDataString(page.Org)}/{Uri.EscapeDataString(page.Project)}/_build/results?buildId={page.BuildId}";

        // Build header
        var headerTable = new Table().Border(TableBorder.Rounded).Expand();
        headerTable.AddColumn(new TableColumn("").NoWrap());
        headerTable.AddColumn(new TableColumn(""));
        headerTable.HideHeaders();

        headerTable.AddRow("[bold]Build[/]", $"#{page.BuildId} — {defName} {buildNumber}");
        headerTable.AddRow("[bold]Result[/]", FormatResult(result));
        if (prNumber is not null && repoName is not null)
        {
            var prUrl = $"https://github.com/{repoName}/pull/{prNumber}";

            // Try to get cached PR info
            using var prCmd = _db.Connection.CreateCommand();
            prCmd.CommandText = "SELECT title, author FROM pull_requests WHERE repository = @repo AND pr_number = @pr";
            prCmd.Parameters.AddWithValue("@repo", repoName);
            prCmd.Parameters.AddWithValue("@pr", prNumber);
            using var prReader = prCmd.ExecuteReader();
            if (prReader.Read() && !prReader.IsDBNull(0))
            {
                var prTitle = prReader.GetString(0);
                var prAuthor = prReader.IsDBNull(1) ? "" : prReader.GetString(1);
                prReader.Close();

                // Truncate title to fit: "PR Info" col + "#123 author " leaves room for title
                var prefix = $"#{prNumber} {prAuthor} ";
                var maxTitleLen = Math.Max(10, Console.WindowWidth - prefix.Length - 20);
                var truncatedTitle = prTitle.Length > maxTitleLen ? prTitle[..maxTitleLen] + "..." : prTitle;
                headerTable.AddRow("[bold]PR Info[/]", $"#{prNumber} [blue]{Markup.Escape(prAuthor)}[/] {Markup.Escape(truncatedTitle)}");
            }
            else
            {
                prReader.Close();
                headerTable.AddRow("[bold]PR Info[/]", $"#{prNumber}");
            }
            headerTable.AddRow("[bold]PR Url[/]", $"[link={prUrl}]{prUrl}[/]");
        }
        else if (prNumber is not null)
        {
            headerTable.AddRow("[bold]PR Info[/]", $"#{prNumber}");
        }
        else
        {
            headerTable.AddRow("[bold]Branch[/]", branch);
        }
        if (finishTime is not null)
            headerTable.AddRow("[bold]Finished[/]", FormatTime(finishTime));
        headerTable.AddRow("[bold]URL[/]", $"[link={url}]{url}[/]");

        // Ingestion status line: Timeline, Tests, Helix with status icons
        var taskStatuses = GetIngestionTaskStatuses(page.Org, page.Project, page.BuildId);
        var taskStatusMap = taskStatuses.ToDictionary(t => t.TaskType, t => t);
        string TaskIcon(string taskType)
        {
            if (!taskStatusMap.TryGetValue(taskType, out var t))
                return "[yellow]...[/]";
            return t.Status switch
            {
                "complete" => "[green]✓[/]",
                "running" => "[blue]...[/]",
                "failed" => $"[yellow]X retry {t.Attempts}/5[/]",
                "abandoned" => "[red]abandoned[/]",
                _ => "[yellow]...[/]",
            };
        }
        headerTable.AddRow("[bold]Data[/]",
            $"Timeline: {TaskIcon("timeline")}  Tests: {TaskIcon("tests")}  Helix: {TaskIcon("helix")}");

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        var timelineStatus = taskStatusMap.GetValueOrDefault("timeline").Status;
        var testsStatus = taskStatusMap.GetValueOrDefault("tests").Status;

        // Failed jobs section (from DB timeline issues, when timeline is ingested)
        if (timelineStatus == "complete")
        {
            using var jobsCmd = _db.Connection.CreateCommand();
            jobsCmd.CommandText = """
                SELECT DISTINCT parent_name
                FROM build_timeline_issues
                WHERE organization = @org AND project = @proj AND build_id = @buildId
                  AND parent_name IS NOT NULL AND issue_type = 'error'
                ORDER BY parent_name
                """;
            jobsCmd.Parameters.AddWithValue("@org", page.Org);
            jobsCmd.Parameters.AddWithValue("@proj", page.Project);
            jobsCmd.Parameters.AddWithValue("@buildId", page.BuildId);

            using var jobsReader = jobsCmd.ExecuteReader();
            var failedJobNames = new List<string>();
            while (jobsReader.Read())
                failedJobNames.Add(jobsReader.GetString(0));
            jobsReader.Close();

            AnsiConsole.MarkupLine("[bold underline]Failed Jobs[/]");
            if (failedJobNames.Count > 0)
            {
                foreach (var jobName in failedJobNames.Take(15))
                    AnsiConsole.MarkupLine($"  [red]X[/] {Markup.Escape(jobName)}");
            }
            else
            {
                AnsiConsole.MarkupLine("  [green]No failed jobs[/]");
            }
            AnsiConsole.WriteLine();
        }

        // Failed tests section
        AnsiConsole.MarkupLine("[bold underline]Failed Tests[/]");
        if (testsStatus != "complete")
        {
            AnsiConsole.MarkupLine("  [yellow]Tests not available yet[/]");
        }
        else
        {
            using var testsCmd = _db.Connection.CreateCommand();
            testsCmd.CommandText = """
                SELECT r.run_name, tr.test_case_title, tr.error_message
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
                WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                      AND tr.outcome = 'Failed'
                ORDER BY r.run_name, tr.test_case_title
                LIMIT 50
                """;
            testsCmd.Parameters.AddWithValue("@org", page.Org);
            testsCmd.Parameters.AddWithValue("@proj", page.Project);
            testsCmd.Parameters.AddWithValue("@buildId", page.BuildId);

            using var testsReader = testsCmd.ExecuteReader();
            var failedTests = new List<(string RunName, string Title, string Error)>();
            while (testsReader.Read())
            {
                var runName = testsReader.GetString(0);
                var title = testsReader.GetString(1);
                var error = testsReader.IsDBNull(2) ? "" : testsReader.GetString(2);
                failedTests.Add((runName, title, error));
            }
            testsReader.Close();

            if (failedTests.Count == 0)
            {
                AnsiConsole.MarkupLine("  [green]All tests passed[/]");
            }
            else
            {
                foreach (var group in failedTests.GroupBy(t => t.RunName))
                {
                    AnsiConsole.MarkupLine($"  [bold yellow]{Markup.Escape(group.Key)}[/]");
                    var shown = 0;
                    var total = group.Count();
                    foreach (var test in group.Take(5))
                    {
                        var title = test.Title.Length > 68 ? test.Title[..65] + "..." : test.Title;
                        var error = test.Error;
                        if (error.Length > 60) error = error[..57] + "...";
                        error = error.ReplaceLineEndings(" ");
                        AnsiConsole.MarkupLine($"    [red]X[/] {Markup.Escape(title)}");
                        if (!string.IsNullOrWhiteSpace(error))
                            AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(error)}[/]");
                        shown++;
                    }
                    if (total > shown)
                        AnsiConsole.MarkupLine($"    [dim]... {total - shown} more failure(s), press T to see all[/]");
                }
            }
        }

        AnsiConsole.WriteLine();

        // Navigation keys
        var canForward = _position < _history.Count - 1;
        var buildIndex = _lastBuilds.FindIndex(b => b.BuildId == page.BuildId && b.Org == page.Org && b.Project == page.Project);
        var canNext = buildIndex >= 0 && buildIndex < _lastBuilds.Count - 1;
        var canPrev = buildIndex > 0;
        AnsiConsole.MarkupLine("[bold]Navigation:[/]");
        AnsiConsole.MarkupLine("  [blue]T[/] Tests   [blue]J[/] Jobs   [blue]H[/] Helix   [blue]B[/] Back" +
            (canForward ? "   [blue]F[/] Forward" : "") +
            (canNext ? "   [blue]N[/] Next" : "") +
            (canPrev ? "   [blue]P[/] Prev" : ""));

        return ReadNavKey(page);
    }

    // ── Test List (for a build) ─────────────────────────────────────

    private NavAction RenderTestList(TestListPage page)
    {
        AnsiConsole.MarkupLine($"[bold underline]Failed Tests — Build #{page.BuildId}[/]");
        AnsiConsole.MarkupLine("[dim]Select a test to see its failure history, or Escape to go back[/]");
        AnsiConsole.WriteLine();

        var tests = new List<(string RunName, string Title)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_name, tr.test_case_title
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
            ORDER BY r.run_name, tr.test_case_title
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tests.Add((reader.GetString(0), reader.GetString(1)));
        reader.Close();

        if (tests.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No failed tests in this build.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        // Build grouped display: run name headers are non-selectable, tests are selectable
        var choices = new List<string>();
        var selectableIndices = new List<int>(); // maps choice index → tests list index
        var grouped = tests.Select((t, i) => (t, i)).GroupBy(x => x.t.RunName);
        foreach (var group in grouped)
        {
            choices.Add($"[bold yellow]{Markup.Escape(group.Key)}[/]");
            selectableIndices.Add(-1); // header, not selectable

            foreach (var (test, idx) in group)
            {
                var title = test.Title.Length > 76 ? test.Title[..73] + "..." : test.Title;
                choices.Add($"  [red]X[/] {Markup.Escape(title)}");
                selectableIndices.Add(idx);
            }
        }

        var totalFailed = tests.Select(t => t.Title).Distinct().Count();
        var selected = SelectWithEscape($"{totalFailed} failed test(s) across {grouped.Count()} run(s):",
            choices, useMarkup: true, skipIndices: selectableIndices.Select((v, i) => (v, i)).Where(x => x.v == -1).Select(x => x.i).ToHashSet());

        if (selected < 0)
            return NavAction.Back.Instance;

        var testTitle = tests[selectableIndices[selected]].Title;
        return new NavAction.Push(
            new TestDetailPage(page.Org, page.Project, testTitle));
    }

    // ── Test Detail (info + error) ─────────────────────────────────

    private NavAction RenderTestDetail(TestDetailPage page)
    {
        AnsiConsole.MarkupLine("[bold underline]Test Failure Detail[/]");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(page.TestName)}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(page.Org)}/{Markup.Escape(page.Project)}[/]");
        AnsiConsole.WriteLine();

        // Get the most recent failure for this test to show error/stack/helix
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.error_message, tr.stack_trace, tr.helix_job_name, tr.helix_work_item_name,
                   r.build_id, r.run_name
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE tr.organization = @org AND tr.project = @proj
                  AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
            ORDER BY r.build_id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@testName", page.TestName);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var errorMessage = reader.IsDBNull(0) ? null : reader.GetString(0);
            var stackTrace = reader.IsDBNull(1) ? null : reader.GetString(1);
            var helixJob = reader.IsDBNull(2) ? null : reader.GetString(2);
            var helixWorkItem = reader.IsDBNull(3) ? null : reader.GetString(3);
            var buildId = reader.GetInt32(4);
            var runName = reader.GetString(5);

            AnsiConsole.MarkupLine($"[bold]Last failed in:[/] Build #{buildId}, {Markup.Escape(runName)}");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[bold]Error:[/]");
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                var errorLines = errorMessage.ReplaceLineEndings("\n").Split('\n');
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
            if (!string.IsNullOrWhiteSpace(stackTrace))
            {
                var stackLines = stackTrace.ReplaceLineEndings("\n").Split('\n');
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
            if (helixJob is not null)
            {
                AnsiConsole.MarkupLine($"  [bold]Job:[/] {Markup.Escape(helixJob)}");
                if (helixWorkItem is not null)
                {
                    AnsiConsole.MarkupLine($"  [bold]Work Item:[/] {Markup.Escape(helixWorkItem)}");
                    var consoleUrl = $"https://helix.dot.net/api/2019-06-17/jobs/{Uri.EscapeDataString(helixJob)}/workitems/{Uri.EscapeDataString(helixWorkItem)}/console";
                    AnsiConsole.MarkupLine($"  [bold]Console Log:[/] [link={consoleUrl}]{consoleUrl}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [dim]No Helix information available[/]");
            }
            AnsiConsole.WriteLine();
        }
        reader.Close();

        // Count total builds with this failure
        using var countCmd = _db.Connection.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(DISTINCT r.build_id)
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE tr.organization = @org AND tr.project = @proj
                  AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
            """;
        countCmd.Parameters.AddWithValue("@org", page.Org);
        countCmd.Parameters.AddWithValue("@proj", page.Project);
        countCmd.Parameters.AddWithValue("@testName", page.TestName);
        var buildCount = Convert.ToInt32(countCmd.ExecuteScalar());

        AnsiConsole.MarkupLine($"[bold]Failed in {buildCount} build(s)[/] in search range");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Navigation:[/]");
        AnsiConsole.MarkupLine("  [blue]B[/] View builds with this failure   [blue]Esc[/] Back");

        while (true)
        {
            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.B:
                    return new NavAction.Push(new TestBuildsPage(page.Org, page.Project, page.TestName));
                case ConsoleKey.Escape:
                    return NavAction.Back.Instance;
            }
        }
    }

    // ── Test Builds (builds with this failure) ──────────────────────

    private NavAction RenderTestBuilds(TestBuildsPage page)
    {
        var shortTitle = page.TestName.Length > 60 ? page.TestName[..57] + "..." : page.TestName;
        AnsiConsole.MarkupLine($"[bold underline]Builds with failure[/]");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(shortTitle)}[/]");
        AnsiConsole.WriteLine();

        var builds = new List<BuildRow>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT b.organization, b.project, b.build_id, b.build_number,
                   b.definition_name, b.result, b.source_branch, b.pr_number, b.finish_time,
                   b.definition_id, b.repository_name
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            JOIN builds b ON r.organization = b.organization AND r.project = b.project AND r.build_id = b.build_id
            WHERE tr.organization = @org AND tr.project = @proj
                  AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
            ORDER BY b.finish_time DESC
            LIMIT 30
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@testName", page.TestName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            builds.Add(new BuildRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                "complete",
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        reader.Close();

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds found with this test failure.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var choices = builds.Select(b =>
        {
            var resultIcon = b.Result switch
            {
                "succeeded" => "[green]✓[/]",
                "failed" => "[red]X[/]",
                "partiallySucceeded" => "[yellow]![/]",
                _ => "[dim]-[/]",
            };
            var pr = b.PrNumber is not null ? $" PR#{b.PrNumber}" : "";
            var time = FormatTime(b.FinishTime);
            return $"{resultIcon} {b.BuildId} {Markup.Escape(b.DefinitionName)} {time}{pr}";
        }).ToList();

        var selected = SelectWithEscape("Select a build:", choices, useMarkup: true);

        if (selected < 0)
            return NavAction.Back.Instance;

        var b2 = builds[selected];
        return new NavAction.Push(new BuildDetailPage(b2.Org, b2.Project, b2.BuildId));
    }

    // ── Job List (timeline) ─────────────────────────────────────────

    private NavAction RenderJobList(JobListPage page)
    {
        // Read timeline issues from DB
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT parent_name, record_name, record_type, record_result, issue_type, issue_message
            FROM build_timeline_issues
            WHERE organization = @org AND project = @proj AND build_id = @buildId
            ORDER BY parent_name, record_name, issue_type
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@buildId", page.BuildId);

        var jobIssues = new Dictionary<string, List<(string Type, string Message)>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var parentName = reader.IsDBNull(0) ? null : reader.GetString(0);
            var recordName = reader.GetString(1);
            var recordType = reader.GetString(2);
            var issueType = reader.GetString(4);
            var message = reader.GetString(5);

            var jobName = recordType == "Job" ? recordName : (parentName ?? recordName);

            if (!jobIssues.TryGetValue(jobName, out var list))
            {
                list = [];
                jobIssues[jobName] = list;
            }
            list.Add((issueType, message));
        }
        reader.Close();

        if (jobIssues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No timeline issues recorded for this build.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var truncate = true;

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold underline]Failed Jobs — Build #{page.BuildId}[/]");
            AnsiConsole.MarkupLine(truncate
                ? "[dim]T full messages  Esc/B back[/]"
                : "[dim]T truncate  Esc/B back[/]");
            AnsiConsole.WriteLine();

            foreach (var (jobName, issues) in jobIssues)
            {
                var errorCount = issues.Count(i => i.Type == "error");
                var warnCount = issues.Count(i => i.Type == "warning");
                var summary = new List<string>();
                if (errorCount > 0) summary.Add($"[red]{errorCount} error(s)[/]");
                if (warnCount > 0) summary.Add($"[yellow]{warnCount} warning(s)[/]");
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(jobName)}[/]  {string.Join(" ", summary)}");

                foreach (var (type, message) in issues.Take(10))
                {
                    var icon = type == "error" ? "[red]error[/]" : "[yellow]warn[/]";
                    var msg = message.ReplaceLineEndings(" ");
                    if (truncate && msg.Length > 120)
                        msg = msg[..117] + "...";
                    AnsiConsole.MarkupLine($"  {icon}: {Markup.Escape(msg)}");
                }

                if (issues.Count > 10)
                    AnsiConsole.MarkupLine($"  [dim]... and {issues.Count - 10} more[/]");

                AnsiConsole.WriteLine();
            }

            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Escape or ConsoleKey.B)
                return NavAction.Back.Instance;
            if (key.Key == ConsoleKey.T)
                truncate = !truncate;
        }
    }

    // ── Key Navigation (for detail pages) ───────────────────────────

    private NavAction ReadNavKey(BuildDetailPage page)
    {
        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.T:
                    return new NavAction.Push(new TestListPage(page.Org, page.Project, page.BuildId));
                case ConsoleKey.J:
                    return new NavAction.Push(new JobListPage(page.Org, page.Project, page.BuildId));
                case ConsoleKey.H:
                    ShowHelixInfo(page);
                    return new NavAction.Push(page); // re-render after showing helix
                case ConsoleKey.N:
                    var nextIdx = _lastBuilds.FindIndex(b => b.BuildId == page.BuildId && b.Org == page.Org && b.Project == page.Project);
                    if (nextIdx >= 0 && nextIdx < _lastBuilds.Count - 1)
                    {
                        var next = _lastBuilds[nextIdx + 1];
                        _selectedBuildIndex = nextIdx + 1;
                        return new NavAction.Replace(new BuildDetailPage(next.Org, next.Project, next.BuildId));
                    }
                    break;
                case ConsoleKey.P:
                    var prevIdx = _lastBuilds.FindIndex(b => b.BuildId == page.BuildId && b.Org == page.Org && b.Project == page.Project);
                    if (prevIdx > 0)
                    {
                        var prev = _lastBuilds[prevIdx - 1];
                        _selectedBuildIndex = prevIdx - 1;
                        return new NavAction.Replace(new BuildDetailPage(prev.Org, prev.Project, prev.BuildId));
                    }
                    break;
                case ConsoleKey.B:
                case ConsoleKey.Escape:
                    return NavAction.Back.Instance;
                case ConsoleKey.F:
                    return NavAction.Forward.Instance;
            }
        }
    }

    private void ShowHelixInfo(BuildDetailPage page)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold underline]Helix Work Items — Build #{page.BuildId}[/]");
        AnsiConsole.WriteLine();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.test_case_title, tr.helix_job_name, tr.helix_work_item_name
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
                  AND tr.helix_job_name IS NOT NULL
            ORDER BY tr.helix_job_name, tr.helix_work_item_name
            LIMIT 30
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var reader = cmd.ExecuteReader();
        var hasHelix = false;
        while (reader.Read())
        {
            hasHelix = true;
            var title = reader.GetString(0);
            var job = reader.IsDBNull(1) ? "-" : reader.GetString(1);
            var wi = reader.IsDBNull(2) ? "-" : reader.GetString(2);
            if (title.Length > 50) title = title[..47] + "...";
            AnsiConsole.MarkupLine($"  [blue]Job:[/] {Markup.Escape(job)}");
            AnsiConsole.MarkupLine($"  [blue]Work Item:[/] {Markup.Escape(wi)}");
            AnsiConsole.MarkupLine($"  [blue]Test:[/] {Markup.Escape(title)}");
            AnsiConsole.WriteLine();
        }

        if (!hasHelix)
            AnsiConsole.MarkupLine("[yellow]No Helix work items found for failed tests in this build.[/]");

        AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
        Console.ReadKey(true);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Custom selection list that supports Escape/B to go back.
    /// Returns the selected index, or -1 if the user pressed Escape/B.
    /// Extra keys can be mapped to return specific negative values.
    /// </summary>
    private static int SelectWithEscape(string title, List<string> items, int pageSize = 20,
        Dictionary<ConsoleKey, int>? extraKeys = null, bool useMarkup = false, int startIndex = 0,
        HashSet<int>? skipIndices = null)
    {
        if (items.Count == 0) return -1;

        var selected = Math.Clamp(startIndex, 0, items.Count - 1);
        // If starting on a skipped index, move to next selectable
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
            // Move cursor to start position and clear the render area
            Console.SetCursorPosition(0, startTop);

            // Render visible items
            var linesRendered = 0;
            for (var i = 0; i < visibleCount; i++)
            {
                var idx = scrollOffset + i;
                if (idx >= items.Count) break;

                // Clear line then write
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                var text = useMarkup ? items[idx] : Markup.Escape(items[idx]);
                if (idx == selected)
                    AnsiConsole.MarkupLine(useMarkup ? $"  [blue]>[/] {text}" : $"  [blue]>[/] [bold]{text}[/]");
                else
                    AnsiConsole.MarkupLine($"    {text}");
                linesRendered++;
            }

            if (items.Count > visibleCount)
            {
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                AnsiConsole.MarkupLine($"  [dim]({selected + 1}/{items.Count})[/]");
                linesRendered++;
            }

            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, Console.CursorTop);
            AnsiConsole.MarkupLine("[dim]  ↑↓ navigate  Enter select  Esc/B back[/]");
            linesRendered++;

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
                            selected++; // can't go past first selectable
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
                            selected--; // can't go past last selectable
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
                    if (extraKeys is not null && extraKeys.TryGetValue(key.Key, out var result2))
                        return result2;
                    break;
            }
        }
    }

    private List<(string TaskType, string Status, int Attempts)> GetIngestionTaskStatuses(
        string org, string project, int buildId)
    {
        var tasks = new List<(string, string, int)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT task_type, status, attempts
            FROM build_ingestion_tasks
            WHERE organization = @org AND project = @proj AND build_id = @buildId
            ORDER BY task_type
            """;
        cmd.Parameters.AddWithValue("@org", org);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@buildId", buildId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tasks.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return tasks;
    }

    /// <summary>
    private static string FormatTime(string? isoTime) => TigerUtils.FormatLocalTime(isoTime);

    private static string FormatResult(string? result) => result switch
    {
        "succeeded" => "[green]✓ succeeded[/]",
        "failed" => "[red]X failed[/]",
        "partiallySucceeded" => "[yellow]! partial[/]",
        "canceled" => "[dim]canceled[/]",
        null => "[dim]-[/]",
        _ => result,
    };

    private static string FormatResultPlain(string? result) => result switch
    {
        "succeeded" => "✓",
        "failed" => "X",
        "partiallySucceeded" => "!",
        _ => result ?? "-",
    };

    // ── Page and Navigation Types ───────────────────────────────────

    private abstract record Page;
    private record BuildListPage : Page;
    private record BuildDetailPage(string Org, string Project, int BuildId) : Page;
    private record TestListPage(string Org, string Project, int BuildId) : Page;
    private record TestDetailPage(string Org, string Project, string TestName) : Page;
    private record TestBuildsPage(string Org, string Project, string TestName) : Page;
    private record JobListPage(string Org, string Project, int BuildId) : Page;

    /// <summary>
    /// Persistent build filter. Survives across app runs via ~/.tiger/filter.json.
    /// </summary>
    private sealed class BuildFilter
    {
        private const string FileName = "filter.json";

        private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public string? RepoPattern { get; set; }
        public string? DefinitionPattern { get; set; }
        public string? ResultPattern { get; set; }
        public string? IdPattern { get; set; }
        public string? KindPattern { get; set; }

        public bool IsActive => RepoPattern is not null || DefinitionPattern is not null
            || ResultPattern is not null || IdPattern is not null || KindPattern is not null;

        public void Clear()
        {
            RepoPattern = null;
            DefinitionPattern = null;
            ResultPattern = null;
            IdPattern = null;
            KindPattern = null;
        }

        public static BuildFilter Load(string configDirectory)
        {
            var path = Path.Combine(configDirectory, FileName);
            if (!File.Exists(path))
                return new BuildFilter();
            try
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<BuildFilter>(json, s_jsonOptions) ?? new BuildFilter();
            }
            catch
            {
                return new BuildFilter();
            }
        }

        public void Save(string configDirectory)
        {
            var path = Path.Combine(configDirectory, FileName);
            var json = System.Text.Json.JsonSerializer.Serialize(this, s_jsonOptions);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Parses an inline filter expression like "repo:roslyn def:ci num:14*".
        /// Unrecognized tokens are ignored.
        /// </summary>
        public void ParseExpression(string expression)
        {
            // Reset before applying
            Clear();
            var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.StartsWith("repo:", StringComparison.OrdinalIgnoreCase))
                    RepoPattern = part[5..];
                else if (part.StartsWith("def:", StringComparison.OrdinalIgnoreCase))
                    DefinitionPattern = part[4..];
                else if (part.StartsWith("result:", StringComparison.OrdinalIgnoreCase))
                    ResultPattern = part[7..];
                else if (part.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                    IdPattern = part[3..];
                else if (part.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
                    KindPattern = part[5..];
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (ResultPattern is not null) parts.Add($"result:{ResultPattern}");
            if (KindPattern is not null) parts.Add($"kind:{KindPattern}");
            if (IdPattern is not null) parts.Add($"id:{IdPattern}");
            return parts.Count > 0 ? string.Join(" ", parts) : "(none)";
        }

        /// <summary>
        /// Converts a user pattern to a SQL LIKE pattern or exact match.
        /// Default: contains match (ros → %ros%).
        /// Trailing ! means exact match (roslyn! → roslyn).
        /// * is a wildcard (dotnet/* → dotnet/%).
        /// </summary>
        public static (string Pattern, bool IsExact) ToSqlPattern(string input)
        {
            // Exact match: trailing !
            if (input.EndsWith('!'))
            {
                return (input[..^1], true);
            }

            // Convert user * to SQL %, leave % and _ as literals by not escaping
            // (users won't type raw SQL wildcards)
            var pattern = input.Replace("*", "%");
            // If no wildcard was present, do contains match
            if (!pattern.Contains('%'))
                pattern = $"%{pattern}%";
            return (pattern, false);
        }
    }

    private abstract record NavAction
    {
        public record Push(Page Page) : NavAction;
        public record Replace(Page Page) : NavAction;
        public sealed record Back : NavAction
        {
            public static readonly Back Instance = new();
        }
        public sealed record Forward : NavAction
        {
            public static readonly Forward Instance = new();
        }
        public static readonly NavAction Exit = Back.Instance;
    }

    private record BuildRow(
        string Org, string Project, int BuildId, string BuildNumber,
        string DefinitionName, string? Result, string Branch,
        int? PrNumber, string? FinishTime, string IngestionStatus = "pending",
        int DefinitionId = 0, string? RepositoryName = null);
}
