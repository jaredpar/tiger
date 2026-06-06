using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser-like navigation for exploring builds, tests, and failures.
/// Supports back/forward navigation like a web browser.
/// </summary>
public sealed class BuildBrowser
{
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly BuildAnalysisService? _analysisService;
    private readonly string _configDirectory;
    private readonly List<Page> _history = [];
    private readonly BuildFilter _filter;
    private int _position = -1;
    private int _selectedBuildIndex;
    private List<BuildRow> _lastBuilds = [];

    public BuildBrowser(TigerDatabase db, AzdoClientFactory clientFactory, string configDirectory, BuildAnalysisService? analysisService = null)
    {
        _db = db;
        _clientFactory = clientFactory;
        _configDirectory = configDirectory;
        _analysisService = analysisService;
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

    /// <summary>
    /// Entry point for drilling into a specific build from another browser.
    /// Returns when the user backs out.
    /// </summary>
    public void BrowseBuild(string org, string project, int buildId)
    {
        Push(new BuildDetailPage(org, project, buildId));
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
                AnsiConsole.MarkupLine($"Filter: {Markup.Escape(_filter.ToString())}");
            AnsiConsole.WriteLine();

            var builds = QueryBuilds();

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine(_filter.IsActive
                    ? "[yellow]No builds match the current filter.[/]"
                    : "[yellow]No builds ingested yet.[/]");
                AnsiConsole.MarkupLine("  [blue]E[/] Edit filter   [blue]F[/] Filter menu   [blue]Esc[/] Back");

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
                var time = BrowserUI.FormatTime(b.FinishTime);
                var branch = $"[dim]{Markup.Escape(SimplifyBranch(b.Branch))}[/]";
                return $"{resultIcon} {b.BuildId} {Markup.Escape(b.DefinitionName)} {branch} {time}{pr}{pending}";
            }).ToList();

            _lastBuilds = builds;

            var hotkeys = _filter.IsActive
                ? "[blue]E[/] Edit filter   [blue]F[/] Filter menu   [blue]C[/] Clear   [blue]H[/] Help"
                : "[blue]E[/] Edit filter   [blue]F[/] Filter menu   [blue]H[/] Help";

            var selected = BrowserUI.SelectWithEscape("Select a build:", choices,
                extraKeys: new Dictionary<ConsoleKey, int> {
                    { ConsoleKey.E, -5 },
                    { ConsoleKey.F, -2 },
                    { ConsoleKey.H, -3 },
                    { ConsoleKey.C, -4 },
                },
                useMarkup: true,
                startIndex: _selectedBuildIndex,
                hotkeys: hotkeys);

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
        return _db.WithCommand(cmd =>
        {
            var builds = new List<BuildRow>();

            var where = new List<string>();
            if (_filter.RepoPattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.RepoPattern);
                where.Add(isExact ? "b.repository_name = @repo" : "b.repository_name LIKE @repo");
                cmd.Parameters.AddWithValue("@repo", pattern);
            }
            if (_filter.DefinitionPattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.DefinitionPattern);
                where.Add(isExact ? "b.definition_name = @def" : "b.definition_name LIKE @def");
                cmd.Parameters.AddWithValue("@def", pattern);
            }
            if (_filter.ResultPattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.ResultPattern);
                where.Add(isExact ? "b.result = @result" : "b.result LIKE @result");
                cmd.Parameters.AddWithValue("@result", pattern);
            }
            if (_filter.IdPattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.IdPattern);
                where.Add(isExact ? "CAST(b.build_id AS TEXT) = @bid" : "CAST(b.build_id AS TEXT) LIKE @bid");
                cmd.Parameters.AddWithValue("@bid", pattern);
            }
            if (_filter.KindPattern is not null)
            {
                BrowserUI.ApplyKindFilter(_filter.KindPattern, where);
            }
            if (_filter.BranchPattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.BranchPattern);
                where.Add(isExact ? "b.source_branch = @branch" : "b.source_branch LIKE @branch");
                cmd.Parameters.AddWithValue("@branch", pattern);
            }
            if (_filter.PrNumber is not null)
            {
                where.Add("b.pr_number = @pr");
                cmd.Parameters.AddWithValue("@pr", _filter.PrNumber.Value);
            }

            var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

            cmd.CommandText = $"""
                SELECT b.organization, b.project, b.build_id, b.build_number, b.definition_name,
                       b.result, b.source_branch, b.pr_number, b.finish_time,
                       CASE WHEN b.ingestion_tasks_complete = 1 THEN 'complete' ELSE 'pending' END as ingestion_status,
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
        });
    }

    private void EditFilter()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Edit Filter[/]");
        AnsiConsole.MarkupLine("[dim]Syntax: repo:VALUE def:VALUE num:VALUE result:VALUE pr:NUMBER[/]");
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
        AnsiConsole.MarkupLine("  [blue]B[/] Filter by branch");
        AnsiConsole.MarkupLine("  [blue]P[/] Filter by PR number");
        AnsiConsole.MarkupLine("  [blue]C[/] Clear all filters");
        AnsiConsole.MarkupLine("  [blue]Esc[/] Cancel");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.R:
                _filter.RepoPattern = BrowserUI.PromptPattern("Repository pattern (e.g. roslyn, dotnet/*):");
                break;
            case ConsoleKey.D:
                _filter.DefinitionPattern = BrowserUI.PromptPattern("Definition pattern (e.g. ci, roslyn-CI*):");
                break;
            case ConsoleKey.I:
                _filter.IdPattern = BrowserUI.PromptPattern("Build ID pattern (e.g. 1423*, 142333):");
                break;
            case ConsoleKey.O:
                _filter.ResultPattern = PromptResultFilter();
                break;
            case ConsoleKey.K:
                _filter.KindPattern = BrowserUI.PromptKindFilter();
                break;
            case ConsoleKey.B:
                _filter.BranchPattern = BrowserUI.PromptPattern("Branch pattern (e.g. main, release/*):");
                break;
            case ConsoleKey.P:
                _filter.PrNumber = PromptPrNumber();
                break;
            case ConsoleKey.C:
                _filter.Clear();
                break;
        }
        SaveFilter();
    }

    /// <summary>
    /// Selection menu for outcome filter. Returns null if cancelled.
    /// </summary>
    private static string? PromptResultFilter()
    {
        AnsiConsole.WriteLine();
        var choices = new[] { "all", "failed", "succeeded", "partiallySucceeded" };
        var selected = BrowserUI.SelectWithEscape("Select outcome:", choices.ToList(), pageSize: 5);
        if (selected < 0) return null; // cancelled
        return choices[selected] == "all" ? null : choices[selected];
    }

    /// <summary>
    /// Prompts the user to enter a PR number. Returns null if cancelled or invalid.
    /// </summary>
    private static int? PromptPrNumber()
    {
        AnsiConsole.WriteLine();
        var raw = BrowserUI.PromptPattern("PR number (e.g. 12345):");
        if (raw is null)
        {
            return null;
        }
        return int.TryParse(raw, out var pr) ? pr : null;
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
        AnsiConsole.MarkupLine("  [blue]branch:[/]  Source branch (e.g. main, release/*)");
        AnsiConsole.MarkupLine("  [blue]pr:[/]      PR number (e.g. 12345)");        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── Build Detail ────────────────────────────────────────────────

    private NavAction RenderBuildDetail(BuildDetailPage page)
    {
        Console.SetCursorPosition(0, 0);

        // Header info from DB
        var buildInfo = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT build_number, definition_name, result, source_branch, pr_number, finish_time, repository_name
                FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@buildId", page.BuildId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return (Found: false, BuildNumber: string.Empty, DefName: string.Empty, Result: (string?)null,
                    Branch: string.Empty, PrNumber: (int?)null, FinishTime: (string?)null, RepoName: (string?)null);
            }

            return (
                Found: true,
                BuildNumber: reader.GetString(0),
                DefName: reader.GetString(1),
                Result: reader.IsDBNull(2) ? null : reader.GetString(2),
                Branch: reader.GetString(3),
                PrNumber: reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                FinishTime: reader.IsDBNull(5) ? null : reader.GetString(5),
                RepoName: reader.IsDBNull(6) ? null : reader.GetString(6));
        });

        if (!buildInfo.Found)
        {
            AnsiConsole.MarkupLine("[red]Build not found.[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var buildNumber = buildInfo.BuildNumber;
        var defName = buildInfo.DefName;
        var result = buildInfo.Result;
        var branch = buildInfo.Branch;
        var prNumber = buildInfo.PrNumber;
        var finishTime = buildInfo.FinishTime;
        var repoName = buildInfo.RepoName;

        var url = $"https://dev.azure.com/{Uri.EscapeDataString(page.Org)}/{Uri.EscapeDataString(page.Project)}/_build/results?buildId={page.BuildId}";

        // Build header
        var headerTable = new Table().Border(TableBorder.Rounded).Expand();
        headerTable.AddColumn(new TableColumn("").NoWrap());
        headerTable.AddColumn(new TableColumn(""));
        headerTable.HideHeaders();

        headerTable.AddRow("[bold]Build[/]", $"#{page.BuildId} — {defName} {buildNumber}");
        headerTable.AddRow("[bold]Result[/]", BrowserUI.FormatResult(result));
        if (prNumber is not null && repoName is not null)
        {
            var prUrl = $"https://github.com/{repoName}/pull/{prNumber}";

            // Try to get cached PR info
            var prInfo = _db.WithCommand(cmd =>
            {
                cmd.CommandText = "SELECT title, author FROM pull_requests WHERE repository = @repo AND pr_number = @pr";
                cmd.Parameters.AddWithValue("@repo", repoName);
                cmd.Parameters.AddWithValue("@pr", prNumber);
                using var reader = cmd.ExecuteReader();
                if (reader.Read() && !reader.IsDBNull(0))
                {
                    return (Found: true, Title: reader.GetString(0), Author: reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
                }

                return (Found: false, Title: string.Empty, Author: string.Empty);
            });

            if (prInfo.Found)
            {
                // Truncate title to fit: "PR Info" col + "#123 author " leaves room for title
                var prefix = $"#{prNumber} {prInfo.Author} ";
                var maxTitleLen = Math.Max(10, Console.WindowWidth - prefix.Length - 20);
                var truncatedTitle = prInfo.Title.Length > maxTitleLen ? prInfo.Title[..maxTitleLen] + "..." : prInfo.Title;
                headerTable.AddRow("[bold]PR Info[/]", $"#{prNumber} [blue]{Markup.Escape(prInfo.Author)}[/] {Markup.Escape(truncatedTitle)}");
            }
            else
            {
                headerTable.AddRow("[bold]PR Info[/]", $"#{prNumber}");
            }
            headerTable.AddRow("[bold]PR Url[/]", BrowserUI.FormatLink(prUrl, $"PR #{prNumber}"));
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
            headerTable.AddRow("[bold]Finished[/]", BrowserUI.FormatTime(finishTime));
        headerTable.AddRow("[bold]URL[/]", BrowserUI.FormatLink(url, url));

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
            var failedJobNames = _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    SELECT DISTINCT parent_name
                    FROM build_timeline_issues
                    WHERE organization = @org AND build_id = @buildId
                      AND parent_name IS NOT NULL AND issue_type = 'error'
                    ORDER BY parent_name
                    """;
                cmd.Parameters.AddWithValue("@org", page.Org);
                cmd.Parameters.AddWithValue("@proj", page.Project);
                cmd.Parameters.AddWithValue("@buildId", page.BuildId);

                var failedJobNames = new List<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    failedJobNames.Add(reader.GetString(0));
                }
                return failedJobNames;
            });

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
            var failedTests = _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    SELECT r.run_name, tr.test_case_title, tr.error_message
                    FROM test_results tr
                    JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                    WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                          AND tr.outcome = 'Failed'
                    ORDER BY r.run_name, tr.test_case_title
                    LIMIT 50
                    """;
                cmd.Parameters.AddWithValue("@org", page.Org);
                cmd.Parameters.AddWithValue("@proj", page.Project);
                cmd.Parameters.AddWithValue("@buildId", page.BuildId);

                var failedTests = new List<(string RunName, string Title, string Error)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var runName = reader.GetString(0);
                    var title = reader.GetString(1);
                    var error = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    failedTests.Add((runName, title, error));
                }
                return failedTests;
            });

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

        // Helix work items count
        var helixCount = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT COUNT(DISTINCT hw.job_name || '/' || hw.work_item_name)
                FROM test_results tr
                JOIN test_runs trn ON tr.organization = trn.organization
                    AND tr.project = trn.project AND tr.run_id = trn.run_id
                JOIN helix_work_items hw ON tr.helix_job_name = hw.job_name
                    AND tr.helix_work_item_name = hw.work_item_name
                WHERE trn.organization = @org AND trn.project = @proj AND trn.build_id = @buildId
                  AND tr.outcome = 'Failed'
                """;
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@buildId", page.BuildId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
        if (helixCount > 0)
        {
            AnsiConsole.MarkupLine($"  [bold]Helix Work Items:[/] {helixCount}");
        }

        AnsiConsole.WriteLine();
        var canForward = _position < _history.Count - 1;
        var buildIndex = _lastBuilds.FindIndex(b => b.BuildId == page.BuildId && b.Org == page.Org && b.Project == page.Project);
        var canNext = buildIndex >= 0 && buildIndex < _lastBuilds.Count - 1;
        var canPrev = buildIndex > 0;
        AnsiConsole.MarkupLine("[bold]Navigation:[/]");
        AnsiConsole.MarkupLine("  [blue]T[/] Tests   [blue]J[/] Jobs   [blue]H[/] Helix   [blue]A[/] Analysis   [blue]B[/] Back" +
            (canForward ? "   [blue]F[/] Forward" : "") +
            (canNext ? "   [blue]N[/] Next" : "") +
            (canPrev ? "   [blue]P[/] Prev" : ""));

        return ReadNavKey(page);
    }

    // ── Test List (for a build) ─────────────────────────────────────

    private NavAction RenderTestList(TestListPage page)
    {
        AnsiConsole.MarkupLine($"[bold underline]Failed Tests — Build #{page.BuildId}[/]");
        AnsiConsole.WriteLine();

        var tests = _db.WithCommand(cmd =>
        {
            var tests = new List<(string RunName, string Title)>();
            cmd.CommandText = """
                SELECT r.run_name, tr.test_case_title
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                      AND tr.outcome = 'Failed'
                ORDER BY r.run_name, tr.test_case_title
                """;
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@buildId", page.BuildId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tests.Add((reader.GetString(0), reader.GetString(1)));
            }
            return tests;
        });

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
        var selected = BrowserUI.SelectWithEscape($"{totalFailed} failed test(s) across {grouped.Count()} run(s):",
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
        // Get the most recent failure for this test to show error/stack/helix
        var testInfo = _db.WithCommand(cmd =>
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
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@testName", page.TestName);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (
                    ErrorMessage: reader.IsDBNull(0) ? null : reader.GetString(0),
                    StackTrace: reader.IsDBNull(1) ? null : reader.GetString(1),
                    HelixJob: reader.IsDBNull(2) ? null : reader.GetString(2),
                    HelixWorkItem: reader.IsDBNull(3) ? null : reader.GetString(3),
                    BuildId: (int?)reader.GetInt32(4),
                    RunName: (string?)reader.GetString(5));
            }

            return (ErrorMessage: (string?)null, StackTrace: (string?)null, HelixJob: (string?)null,
                HelixWorkItem: (string?)null, BuildId: (int?)null, RunName: (string?)null);
        });

        var errorMessage = testInfo.ErrorMessage;
        var stackTrace = testInfo.StackTrace;
        var helixJob = testInfo.HelixJob;
        var helixWorkItem = testInfo.HelixWorkItem;
        var buildId = testInfo.BuildId;
        var runName = testInfo.RunName;

        // Count total builds with this failure
        var buildCount = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT COUNT(DISTINCT r.build_id)
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE tr.organization = @org AND tr.project = @proj
                      AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
                """;
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@testName", page.TestName);
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

        // Header box
        var headerTable = new Table().Border(TableBorder.Rounded);
        headerTable.AddColumn(new TableColumn("").NoWrap());
        headerTable.AddColumn(new TableColumn(""));
        headerTable.HideHeaders();
        headerTable.AddRow("[bold]Test Name[/]", Markup.Escape(page.TestName));
        if (buildId is not null)
        {
            var buildUrl = $"https://dev.azure.com/{Uri.EscapeDataString(page.Org)}/{Uri.EscapeDataString(page.Project)}/_build/results?buildId={buildId}";
            headerTable.AddRow("[bold]Last Failed Build[/]", BrowserUI.FormatLink(buildUrl, $"Build #{buildId}"));
        }
        headerTable.AddRow("[bold]Failed In[/]", $"{buildCount} build(s)");
        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        if (buildId is not null)
        {
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
                    var consoleUrl = HelixClient.GetConsoleUrl(helixJob, helixWorkItem);
                    AnsiConsole.MarkupLine($"  [bold]Console:[/] {BrowserUI.FormatLink(consoleUrl, "Console Log")}");

                    // Show attached files if available
                    var filesJson = _db.WithCommand(cmd =>
                    {
                        cmd.CommandText = """
                            SELECT files FROM helix_work_items
                            WHERE job_name = @job AND work_item_name = @wi
                            """;
                        cmd.Parameters.AddWithValue("@job", helixJob);
                        cmd.Parameters.AddWithValue("@wi", helixWorkItem);
                        return cmd.ExecuteScalar() as string;
                    });

                    if (!string.IsNullOrWhiteSpace(filesJson))
                    {
                        var files = System.Text.Json.JsonSerializer.Deserialize<List<HelixFileEntry>>(filesJson);
                        if (files is { Count: > 0 })
                        {
                            AnsiConsole.MarkupLine($"  [bold]Files ({files.Count}):[/]");
                            foreach (var f in files)
                            {
                                var name = f.FileName ?? "unknown";
                                if (f.Uri is not null)
                                {
                                    AnsiConsole.MarkupLine($"    {BrowserUI.FormatLink(f.Uri, name)}");
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"    {Markup.Escape(name)}");
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [dim]No Helix information available[/]");
            }
            AnsiConsole.WriteLine();
        }

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

        var builds = _db.WithCommand(cmd =>
        {
            var builds = new List<BuildRow>();
            cmd.CommandText = """
                SELECT DISTINCT b.organization, b.project, b.build_id, b.build_number,
                       b.definition_name, b.result, b.source_branch, b.pr_number, b.finish_time,
                       b.definition_id, b.repository_name
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                JOIN builds b ON r.organization = b.organization AND r.build_id = b.build_id
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
            return builds;
        });

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
            var time = BrowserUI.FormatTime(b.FinishTime);
            return $"{resultIcon} {b.BuildId} {Markup.Escape(b.DefinitionName)} {time}{pr}";
        }).ToList();

        var selected = BrowserUI.SelectWithEscape("Select a build:", choices, useMarkup: true);

        if (selected < 0)
            return NavAction.Back.Instance;

        var b2 = builds[selected];
        return new NavAction.Push(new BuildDetailPage(b2.Org, b2.Project, b2.BuildId));
    }

    // ── Job List (timeline) ─────────────────────────────────────────

    private NavAction RenderJobList(JobListPage page)
    {
        // Read timeline issues from DB
        var jobIssues = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT parent_name, record_name, record_type, record_result, issue_type, issue_message
                FROM build_timeline_issues
                WHERE organization = @org AND build_id = @buildId
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
            return jobIssues;
        });

        if (jobIssues.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No timeline issues recorded for this build.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var truncate = true;
        var errorsOnly = false;

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[bold underline]Failed Jobs — Build #{page.BuildId}[/]");
            if (errorsOnly)
                AnsiConsole.MarkupLine("[dim]Showing errors only[/]");
            AnsiConsole.WriteLine();

            foreach (var (jobName, issues) in jobIssues)
            {
                var filtered = errorsOnly
                    ? issues.Where(i => i.Type == "error").ToList()
                    : issues;

                if (filtered.Count == 0)
                    continue;

                var errorCount = issues.Count(i => i.Type == "error");
                var warnCount = issues.Count(i => i.Type == "warning");
                var summary = new List<string>();
                if (errorCount > 0) summary.Add($"[red]{errorCount} error(s)[/]");
                if (warnCount > 0) summary.Add($"[yellow]{warnCount} warning(s)[/]");
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(jobName)}[/]  {string.Join(" ", summary)}");

                foreach (var (type, message) in filtered.Take(10))
                {
                    var icon = type == "error" ? "[red]error[/]" : "[yellow]warn[/]";
                    var msg = message.ReplaceLineEndings(" ");
                    if (truncate && msg.Length > 120)
                        msg = msg[..117] + "...";
                    AnsiConsole.MarkupLine($"  {icon}: {Markup.Escape(msg)}");
                }

                if (filtered.Count > 10)
                    AnsiConsole.MarkupLine($"  [dim]... and {filtered.Count - 10} more[/]");

                AnsiConsole.WriteLine();
            }

            // Hotkey menu at the bottom
            var errorsLabel = errorsOnly ? "[blue]E[/] Show all" : "[blue]E[/] Errors only";
            var truncateLabel = truncate ? "[blue]T[/] Full messages" : "[blue]T[/] Truncate";
            AnsiConsole.MarkupLine($"  {errorsLabel}   {truncateLabel}   [blue]Esc[/] Back");

            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Escape or ConsoleKey.B)
                return NavAction.Back.Instance;
            if (key.Key == ConsoleKey.T)
                truncate = !truncate;
            if (key.Key == ConsoleKey.E)
                errorsOnly = !errorsOnly;
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
                case ConsoleKey.A:
                    ShowAnalysis(page);
                    return new NavAction.Push(page); // re-render after showing analysis
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

        var helixItems = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT DISTINCT tr.helix_job_name, tr.helix_work_item_name, hw.state, hw.exit_code, hw.console_output_uri
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                LEFT JOIN helix_work_items hw ON tr.helix_job_name = hw.job_name
                    AND tr.helix_work_item_name = hw.work_item_name
                WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                      AND tr.outcome = 'Failed'
                      AND tr.helix_job_name IS NOT NULL
                ORDER BY tr.helix_job_name, tr.helix_work_item_name
                LIMIT 30
                """;
            cmd.Parameters.AddWithValue("@org", page.Org);
            cmd.Parameters.AddWithValue("@proj", page.Project);
            cmd.Parameters.AddWithValue("@buildId", page.BuildId);

            var helixItems = new List<(string Job, string WorkItem, string? State, int? ExitCode, string? ConsoleUri)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                helixItems.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return helixItems;
        });

        var hasHelix = false;
        foreach (var (job, wi, state, exitCode, consoleUri) in helixItems)
        {
            hasHelix = true;
            var stateInfo = state is not null ? $" [{(exitCode == 0 ? "green" : "red")}]{state} (exit {exitCode})[/]" : "";
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(wi)}[/]{stateInfo}");

            var url = consoleUri ?? HelixClient.GetConsoleUrl(job, wi);
            AnsiConsole.MarkupLine($"    {BrowserUI.FormatLink(url, "Console Log")}");
        }

        if (!hasHelix)
        {
            AnsiConsole.MarkupLine("[yellow]No Helix work items found for failed tests in this build.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
        Console.ReadKey(true);
    }

    private void ShowAnalysis(BuildDetailPage page)
    {
        var analysis = _db.GetBuildAnalysis(page.Org, page.BuildId);

        if (analysis is null)
        {
            // No analysis exists — offer to queue one
            if (_analysisService is null)
            {
                AnsiConsole.MarkupLine("[yellow]No analysis available and analysis service is not running.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
                Console.ReadKey(true);
                return;
            }

            AnsiConsole.MarkupLine("[dim]No analysis exists for this build.[/]");
            if (AnsiConsole.Confirm("Queue analysis for this build?", defaultValue: true))
            {
                _analysisService.RequestAnalysis(page.Org, page.BuildId);
                AnsiConsole.MarkupLine("[green]Analysis queued.[/]");
            }
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return;
        }

        // Show analysis detail inline
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold underline]Analysis — Build #{page.BuildId}[/]");
        AnsiConsole.WriteLine();

        var table = new Table().NoBorder().HideHeaders().AddColumn("Key").AddColumn("Value");
        table.AddRow("[bold]Status[/]", FormatAnalysisStatus(analysis.Status));
        if (analysis.Category is not null)
        {
            table.AddRow("[bold]Category[/]", Markup.Escape(analysis.Category));
        }
        if (analysis.Confidence is not null)
        {
            table.AddRow("[bold]Confidence[/]", Markup.Escape(analysis.Confidence));
        }
        if (analysis.CompletedAt is not null)
        {
            table.AddRow("[bold]Completed[/]", Markup.Escape(analysis.CompletedAt));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (analysis.DiagnosisSummary is not null)
        {
            AnsiConsole.MarkupLine("[bold]Diagnosis:[/]");
            AnsiConsole.WriteLine(analysis.DiagnosisSummary);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
        Console.ReadKey(true);
    }

    private static string FormatAnalysisStatus(string status) => status switch
    {
        "complete" => "[green]Complete[/]",
        "running" => "[yellow]Running[/]",
        "pending" => "[dim]Pending[/]",
        "skipped" => "[blue]Skipped[/]",
        "failed" => "[red]Failed[/]",
        _ => Markup.Escape(status),
    };

    // ── Helpers ──────────────────────────────────────────────────────

    private static string SimplifyBranch(string branch)
    {
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            return branch["refs/heads/".Length..];
        if (branch.StartsWith("refs/pull/", StringComparison.Ordinal))
            return branch["refs/pull/".Length..];
        return branch;
    }

    private List<(string TaskType, string Status, int Attempts)> GetIngestionTaskStatuses(
        string org, string project, int buildId)
    {
        return _db.WithCommand(cmd =>
        {
            var tasks = new List<(string, string, int)>();
            cmd.CommandText = """
                SELECT task_type, status, attempts
                FROM build_ingestion_tasks
                WHERE organization = @org AND build_id = @buildId
                ORDER BY task_type
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }
            return tasks;
        });
    }

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
        public string? BranchPattern { get; set; }
        public int? PrNumber { get; set; }

        public bool IsActive => RepoPattern is not null || DefinitionPattern is not null
            || ResultPattern is not null || IdPattern is not null || KindPattern is not null
            || BranchPattern is not null || PrNumber is not null;

        public void Clear()
        {
            RepoPattern = null;
            DefinitionPattern = null;
            ResultPattern = null;
            IdPattern = null;
            KindPattern = null;
            BranchPattern = null;
            PrNumber = null;
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
                else if (part.StartsWith("branch:", StringComparison.OrdinalIgnoreCase))
                    BranchPattern = part[7..];
                else if (part.StartsWith("pr:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(part[3..], out var pr))
                    {
                        PrNumber = pr;
                    }
                }
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (ResultPattern is not null) parts.Add($"result:{ResultPattern}");
            if (KindPattern is not null) parts.Add($"kind:{KindPattern}");
            if (BranchPattern is not null) parts.Add($"branch:{BranchPattern}");
            if (PrNumber is not null) parts.Add($"pr:{PrNumber}");
            if (IdPattern is not null) parts.Add($"id:{IdPattern}");
            return parts.Count > 0 ? string.Join(" ", parts) : "(none)";
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

    private sealed class HelixFileEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("fileName")]
        public string? FileName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("uri")]
        public string? Uri { get; set; }
    }
}
