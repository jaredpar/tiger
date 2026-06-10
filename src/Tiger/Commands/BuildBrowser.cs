using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser-like navigation for exploring builds, tests, and failures.
/// Supports back/forward navigation like a web browser.
/// </summary>
public sealed class BuildBrowser
{
    private readonly PanelRenderer _ui = PanelRenderer.Create();

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
                case NavAction.Refresh:
                    // Re-render current page (loop continues)
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
            var builds = QueryBuilds();

            var filterText = _filter.IsActive
                ? $"Filter: {Markup.Escape(_filter.ToString())}"
                : "[dim]Filter: (none)[/]";
            var context = builds.Count > 0
                ? $"{filterText}  [dim]({builds.Count} builds)[/]"
                : filterText;

            if (builds.Count == 0)
            {
                var emptyMsg = _filter.IsActive
                    ? "[yellow]No builds match the current filter.[/]"
                    : "[yellow]No builds ingested yet.[/]";

                _ui.RenderDetailPanel(
                    ["Builds"],
                    context,
                    () => _ui.RenderPanelLine(emptyMsg),
                    PanelRenderer.BuildCommandBarString(new List<CommandBarItem>
                    {
                        new("Edit filter", ConsoleKey.E, -5),
                        new("Filter menu", ConsoleKey.F, -2),
                    }));

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

            var choices = builds.Select(b =>
            {
                var resultIcon = b.Result switch
                {
                    "succeeded" => "[green]+[/]",
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

            var commands = new List<CommandBarItem>
            {
                new("Edit filter", ConsoleKey.E, -5),
                new("Filter menu", ConsoleKey.F, -2),
                new("Help", ConsoleKey.H, -3),
            };
            if (_filter.IsActive)
            {
                commands.Add(new("Clear", ConsoleKey.C, -4));
            }

            var selected = _ui.SelectInPanel(
                ["Builds"],
                context,
                choices,
                commands,
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
            {
                return NavAction.Back.Instance;
            }

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
        var currentValue = _filter.IsActive ? _filter.ToString() : null;
        var result = _ui.PromptInPanel(
            ["Builds", "Edit Filter"],
            "Enter filter expression (e.g. repo:roslyn def:ci result:failed)",
            currentValue);

        if (result is not null)
        {
            _filter.ParseExpression(result);
            SaveFilter();
        }
    }

    private void ShowFilterMenu()
    {
        while (true)
        {
            var commands = new List<CommandBarItem>
            {
                new("Repository", ConsoleKey.R, 1),
                new("Definition", ConsoleKey.D, 2),
                new("Build ID", ConsoleKey.I, 3),
                new("Outcome", ConsoleKey.O, 4),
                new("Kind", ConsoleKey.K, 5),
                new("Branch", ConsoleKey.B, 6),
                new("PR number", ConsoleKey.P, 7),
                new("Clear", ConsoleKey.C, 8),
            };

            _ui.RenderDetailPanel(
                ["Builds", "Filter"],
                null,
                () =>
                {
                    _ui.RenderPanelLine("Filter available builds by repository, definition, kind, branch, etc.");
                    _ui.RenderEmptyLine();

                    if (_filter.IsActive)
                    {
                        _ui.RenderPanelLine($"[bold]Current filter:[/] {Markup.Escape(_filter.ToString())}");
                    }
                    else
                    {
                        _ui.RenderPanelLine("[dim]No filter active[/]");
                    }

                    _ui.RenderEmptyLine();
                    _ui.RenderPanelLine("[dim]Syntax: substring match by default, * for wildcards, ! suffix for exact[/]");
                    _ui.RenderPanelLine("[dim]Example: repo:roslyn  def:*-CI  result:failed  branch:main[/]");
                },
                PanelRenderer.BuildCommandBarString(commands));

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.R:
                    _filter.RepoPattern = PromptFilterField("Repository pattern (e.g. roslyn, dotnet/*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.D:
                    _filter.DefinitionPattern = PromptFilterField("Definition pattern (e.g. ci, roslyn-CI*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.I:
                    _filter.IdPattern = PromptFilterField("Build ID pattern (e.g. 1423*, 142333)");
                    SaveFilter();
                    continue;
                case ConsoleKey.O:
                    _filter.ResultPattern = PromptResultFilter();
                    SaveFilter();
                    continue;
                case ConsoleKey.K:
                    _filter.KindPattern = BrowserUI.PromptKindFilter();
                    SaveFilter();
                    continue;
                case ConsoleKey.B:
                    _filter.BranchPattern = PromptFilterField("Branch pattern (e.g. main, release/*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.P:
                    _filter.PrNumber = PromptPrNumber();
                    SaveFilter();
                    continue;
                case ConsoleKey.C:
                    _filter.Clear();
                    SaveFilter();
                    continue;
                default:
                    return;
            }
        }
    }

    private string? PromptFilterField(string prompt)
    {
        return _ui.PromptInPanel(["Builds", "Filter"], prompt);
    }

    /// <summary>
    /// Selection menu for outcome filter. Returns null if cancelled.
    /// </summary>
    private string? PromptResultFilter()
    {
        var outcomes = new List<string> { "all", "failed", "succeeded", "partiallySucceeded" };
        var commands = new List<CommandBarItem>();
        var selected = _ui.SelectInPanel(
            ["Builds", "Filter", "Outcome"],
            "[dim]Select build outcome to filter on[/]",
            outcomes,
            commands);
        if (selected < 0)
        {
            return null;
        }
        return outcomes[selected] == "all" ? null : outcomes[selected];
    }

    /// <summary>
    /// Prompts the user to enter a PR number. Returns null if cancelled or invalid.
    /// </summary>
    private int? PromptPrNumber()
    {
        var raw = _ui.PromptInPanel(["Builds", "Filter"], "PR number (e.g. 12345)");
        if (raw is null)
        {
            return null;
        }
        return int.TryParse(raw, out var pr) ? pr : null;
    }

    private void ShowFilterHelp()
    {
        _ui.RenderDetailPanel(
            ["Builds", "Filter Help"],
            null,
            () =>
            {
                _ui.RenderPanelLine("[bold]Quick filter (E):[/]");
                _ui.RenderPanelLine("  Type an expression like: [blue]repo:roslyn def:ci[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Matching (default: contains / LIKE):[/]");
                _ui.RenderPanelLine("  [dim]ros - matches 'dotnet/roslyn', 'roslyn-CI', etc.[/]");
                _ui.RenderPanelLine("  [dim]dotnet/* - matches 'dotnet/roslyn', 'dotnet/runtime'[/]");
                _ui.RenderPanelLine("  [dim]*-CI - matches definition names ending with '-CI'[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Exact match (append !):[/]");
                _ui.RenderPanelLine("  [dim]dotnet/roslyn! - matches exactly 'dotnet/roslyn'[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Filter prefixes:[/]");
                _ui.RenderPanelLine("  [blue]repo:[/]    Repository name");
                _ui.RenderPanelLine("  [blue]def:[/]     Definition/pipeline name");
                _ui.RenderPanelLine("  [blue]id:[/]      Build ID");
                _ui.RenderPanelLine("  [blue]result:[/]  Outcome (failed, succeeded, partiallySucceeded)");
                _ui.RenderPanelLine("  [blue]kind:[/]    Build kind (pr, ci)");
                _ui.RenderPanelLine("  [blue]branch:[/]  Source branch");
                _ui.RenderPanelLine("  [blue]pr:[/]      PR number");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Multiple filters combine with AND.[/]");
            },
            "[blue]Esc[/] Back");
        Console.ReadKey(true);
    }

    // ── Build Detail ────────────────────────────────────────────────

    private NavAction RenderBuildDetail(BuildDetailPage page)
    {
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
            _ui.RenderDetailPanel(
                ["Builds", $"#{page.BuildId}"],
                null,
                () => _ui.RenderPanelLine("[red]Build not found.[/]"),
                "[blue]Esc[/] Back");
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

        // Ingestion status
        var taskStatuses = GetIngestionTaskStatuses(page.Org, page.Project, page.BuildId);
        var taskStatusMap = taskStatuses.ToDictionary(t => t.TaskType, t => t);
        string TaskIcon(string taskType)
        {
            if (!taskStatusMap.TryGetValue(taskType, out var t))
            {
                return "[yellow]...[/]";
            }
            return t.Status switch
            {
                "complete" => "[green]+[/]",
                "running" => "[blue]...[/]",
                "failed" => $"[yellow]X retry {t.Attempts}/5[/]",
                "abandoned" => "[red]abandoned[/]",
                _ => "[yellow]...[/]",
            };
        }

        var timelineStatus = taskStatusMap.GetValueOrDefault("timeline").Status;
        var testsStatus = taskStatusMap.GetValueOrDefault("tests").Status;

        var canForward = _position < _history.Count - 1;
        var buildIndex = _lastBuilds.FindIndex(b => b.BuildId == page.BuildId && b.Org == page.Org && b.Project == page.Project);
        var canNext = buildIndex >= 0 && buildIndex < _lastBuilds.Count - 1;
        var canPrev = buildIndex > 0;

        var detailCommands = new List<CommandBarItem>
        {
            new("Tests", ConsoleKey.T, -10),
            new("Jobs", ConsoleKey.J, -11),
            new("Helix", ConsoleKey.H, -12),
            new("Analysis", ConsoleKey.A, -13),
        };
        if (canForward)
        {
            detailCommands.Add(new("Forward", ConsoleKey.F, -14));
        }
        if (canNext)
        {
            detailCommands.Add(new("Next", ConsoleKey.N, -15));
        }
        if (canPrev)
        {
            detailCommands.Add(new("Prev", ConsoleKey.P, -16));
        }

        _ui.RenderDetailPanel(
            ["Builds", $"#{page.BuildId} {defName}"],
            $"{BrowserUI.FormatResult(result)}  {BrowserUI.FormatTime(finishTime)}",
            () =>
            {
                // Build info fields
                _ui.RenderField("Build", $"#{page.BuildId} — {defName} {buildNumber}");
                _ui.RenderField("Result", BrowserUI.FormatResult(result));
                if (prNumber is not null && repoName is not null)
                {
                    var prUrl = $"https://github.com/{repoName}/pull/{prNumber}";
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
                        var prefix = $"#{prNumber} {prInfo.Author} ";
                        var maxTitleLen = Math.Max(10, _ui.ContentWidth - prefix.Length - 20);
                        var truncatedTitle = prInfo.Title.Length > maxTitleLen ? prInfo.Title[..maxTitleLen] + "..." : prInfo.Title;
                        _ui.RenderField("PR", $"#{prNumber} [blue]{Markup.Escape(prInfo.Author)}[/] {Markup.Escape(truncatedTitle)}");
                    }
                    else
                    {
                        _ui.RenderField("PR", $"#{prNumber}");
                    }
                    _ui.RenderField("PR URL", BrowserUI.FormatLink(prUrl, $"PR #{prNumber}"));
                }
                else if (prNumber is not null)
                {
                    _ui.RenderField("PR", $"#{prNumber}");
                }
                else
                {
                    _ui.RenderField("Branch", branch);
                }
                if (finishTime is not null)
                {
                    _ui.RenderField("Finished", BrowserUI.FormatTime(finishTime));
                }
                _ui.RenderField("URL", BrowserUI.FormatLink(url, url));
                _ui.RenderField("Data", $"Timeline: {TaskIcon("timeline")}  Tests: {TaskIcon("tests")}  Helix: {TaskIcon("helix")}");
                _ui.RenderEmptyLine();

                // Failed jobs section
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

                        var names = new List<string>();
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            names.Add(reader.GetString(0));
                        }
                        return names;
                    });

                    _ui.RenderSectionTitle("Failed Jobs");
                    if (failedJobNames.Count > 0)
                    {
                        foreach (var jobName in failedJobNames.Take(15))
                        {
                            _ui.RenderPanelLine($"  [red]X[/] {Markup.Escape(jobName)}");
                        }
                    }
                    else
                    {
                        _ui.RenderPanelLine("  [green]No failed jobs[/]");
                    }
                    _ui.RenderEmptyLine();
                }

                // Failed tests section
                _ui.RenderSectionTitle("Failed Tests");
                if (testsStatus != "complete")
                {
                    _ui.RenderPanelLine("  [yellow]Tests not available yet[/]");
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

                        var tests = new List<(string RunName, string Title, string Error)>();
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            tests.Add((reader.GetString(0), reader.GetString(1),
                                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
                        }
                        return tests;
                    });

                    if (failedTests.Count == 0)
                    {
                        _ui.RenderPanelLine("  [green]All tests passed[/]");
                    }
                    else
                    {
                        foreach (var group in failedTests.GroupBy(t => t.RunName))
                        {
                            _ui.RenderPanelLine($"  [bold yellow]{Markup.Escape(group.Key)}[/]");
                            var shown = 0;
                            var total = group.Count();
                            foreach (var test in group.Take(5))
                            {
                                var title = test.Title.Length > 68 ? test.Title[..65] + "..." : test.Title;
                                var error = test.Error;
                                if (error.Length > 60)
                                {
                                    error = error[..57] + "...";
                                }
                                error = error.ReplaceLineEndings(" ");
                                _ui.RenderPanelLine($"    [red]X[/] {Markup.Escape(title)}");
                                if (!string.IsNullOrWhiteSpace(error))
                                {
                                    _ui.RenderPanelLine($"      [dim]{Markup.Escape(error)}[/]");
                                }
                                shown++;
                            }
                            if (total > shown)
                            {
                                _ui.RenderPanelLine($"    [dim]... {total - shown} more failure(s), press T to see all[/]");
                            }
                        }
                    }
                }

                // Helix work items
                var helixItems = _db.WithCommand(cmd =>
                {
                    cmd.CommandText = """
                        SELECT DISTINCT tr.helix_job_name, tr.helix_work_item_name, hw.state, hw.exit_code, hw.is_deadletter
                        FROM test_results tr
                        JOIN test_runs trn ON tr.organization = trn.organization
                            AND tr.project = trn.project AND tr.run_id = trn.run_id
                        LEFT JOIN helix_work_items hw ON tr.helix_job_name = hw.job_name
                            AND tr.helix_work_item_name = hw.work_item_name
                        WHERE trn.organization = @org AND trn.project = @proj AND trn.build_id = @buildId
                          AND tr.outcome = 'Failed'
                          AND tr.helix_job_name IS NOT NULL
                        ORDER BY tr.helix_job_name, tr.helix_work_item_name
                        LIMIT 15
                        """;
                    cmd.Parameters.AddWithValue("@org", page.Org);
                    cmd.Parameters.AddWithValue("@proj", page.Project);
                    cmd.Parameters.AddWithValue("@buildId", page.BuildId);

                    var items = new List<(string Job, string Wi, string? State, int? ExitCode, bool IsDeadletter)>();
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        items.Add((
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                            !reader.IsDBNull(4) && reader.GetInt32(4) != 0));
                    }
                    return items;
                });

                if (helixItems.Count > 0)
                {
                    _ui.RenderEmptyLine();
                    _ui.RenderSectionTitle($"Helix Work Items ({helixItems.Count})");
                    foreach (var (job, wi, state, exitCode, isDeadletter) in helixItems)
                    {
                        var exitInfo = exitCode is not null ? $" exit {exitCode}" : "";
                        var extra = isDeadletter ? " [red]deadletter[/]" : "";
                        var color = (exitCode ?? 1) == 0 ? "green" : "red";
                        _ui.RenderPanelLine($"  [{color}]X[/] {Markup.Escape(wi)}  [dim]{Markup.Escape(job)}[/]{exitInfo}{extra}");
                    }
                }
            },
            PanelRenderer.BuildCommandBarString(detailCommands));

        return ReadNavKey(page);
    }

    // ── Test List (for a build) ─────────────────────────────────────

    private NavAction RenderTestList(TestListPage page)
    {
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
            _ui.RenderDetailPanel(
                ["Builds", $"#{page.BuildId}", "Tests"],
                null,
                () => _ui.RenderPanelLine("[green]No failed tests in this build.[/]"),
                "[blue]Esc[/] Back");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        // Build grouped display: run name headers are non-selectable, tests are selectable
        var choices = new List<string>();
        var selectableIndices = new List<int>(); // maps choice index -> tests list index
        var skipIndices = new HashSet<int>();
        var grouped = tests.Select((t, i) => (t, i)).GroupBy(x => x.t.RunName);
        foreach (var group in grouped)
        {
            skipIndices.Add(choices.Count);
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
        var commands = new List<CommandBarItem>();

        var selected = _ui.SelectInPanel(
            ["Builds", $"#{page.BuildId}", "Tests"],
            $"[dim]{totalFailed} failed test(s) across {grouped.Count()} run(s)[/]",
            choices,
            commands,
            skipIndices: skipIndices);

        if (selected < 0)
        {
            return NavAction.Back.Instance;
        }

        var testTitle = tests[selectableIndices[selected]].Title;
        return new NavAction.Push(
            new TestDetailPage(page.Org, page.Project, testTitle));
    }

    // ── Test Detail (info + error) ─────────────────────────────────

    private NavAction RenderTestDetail(TestDetailPage page)
    {
        var info = BrowserUI.LoadTestDetail(_db, page.Org, page.Project, page.TestName);
        if (info is null)
        {
            _ui.RenderDetailPanel(
                ["Builds", "Tests", "Detail"],
                null,
                () => _ui.RenderPanelLine("[yellow]No test failure data found.[/]"),
                "[blue]Esc[/] Back");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var shortTitle = page.TestName.Length > 60 ? page.TestName[..57] + "..." : page.TestName;
        var commands = new List<CommandBarItem>
        {
            new("Builds with failure", ConsoleKey.B, -2),
            new("Agent task", ConsoleKey.A, -3),
        };
        if (info.HelixJobName is not null)
        {
            commands.Add(new("Helix", ConsoleKey.H, -4));
        }

        _ui.RenderDetailPanel(
            ["Builds", "Tests", Markup.Escape(shortTitle)],
            null,
            () => BrowserUI.RenderTestDetailInPanel(_ui, info),
            PanelRenderer.BuildCommandBarString(commands));

        while (true)
        {
            var key = Console.ReadKey(true);
            if (_ui.HandleDetailScroll(key)) continue;
            switch (key.Key)
            {
                case ConsoleKey.B:
                    return new NavAction.Push(new TestBuildsPage(page.Org, page.Project, page.TestName));
                case ConsoleKey.A:
                    BrowserUI.CreateAgentTask(_db, info);
                    return NavAction.Refresh.Instance;
                case ConsoleKey.H when info.HelixJobName is not null:
                    ShowHelixWorkItemDetail(info);
                    return NavAction.Refresh.Instance;
                case ConsoleKey.Escape:
                    return NavAction.Back.Instance;
            }
        }
    }

    private void ShowHelixWorkItemDetail(BrowserUI.TestDetailInfo info)
    {
        var commands = new List<CommandBarItem>();
        _ui.RenderDetailPanel(
            ["Tests", "Helix Work Item"],
            null,
            () =>
            {
                if (info.IsHelixDeadletter)
                {
                    _ui.RenderPanelLine("[bold red on yellow] !! HELIX DEAD LETTER — Infrastructure failure [/]");
                    _ui.RenderEmptyLine();
                }
                _ui.RenderField("Job", Markup.Escape(info.HelixJobName!));
                if (info.HelixWorkItemName is not null)
                {
                    _ui.RenderField("Work Item", Markup.Escape(info.HelixWorkItemName));
                    var url = HelixClient.GetConsoleUrl(info.HelixJobName!, info.HelixWorkItemName);
                    _ui.RenderField("Console", BrowserUI.FormatLink(url, "Console Log"));
                }
                if (info.HelixFiles is { Count: > 0 })
                {
                    _ui.RenderEmptyLine();
                    _ui.RenderSectionTitle($"Files ({info.HelixFiles.Count})");
                    foreach (var (name, uri) in info.HelixFiles)
                    {
                        if (uri is not null)
                        {
                            _ui.RenderPanelLine($"  {BrowserUI.FormatLink(uri, name)}");
                        }
                        else
                        {
                            _ui.RenderPanelLine($"  {Markup.Escape(name)}");
                        }
                    }
                }
            },
            "[blue]Esc[/] Back");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (_ui.HandleDetailScroll(key)) continue;
            if (key.Key == ConsoleKey.Escape) return;
        }
    }

    // ── Test Builds (builds with this failure) ──────────────────────

    private NavAction RenderTestBuilds(TestBuildsPage page)
    {
        var shortTitle = page.TestName.Length > 60 ? page.TestName[..57] + "..." : page.TestName;

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
            _ui.RenderDetailPanel(
                ["Builds", "Tests", "Builds with failure"],
                null,
                () => _ui.RenderPanelLine("[yellow]No builds found with this test failure.[/]"),
                "[blue]Esc[/] Back");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var choices = builds.Select(b =>
        {
            var resultIcon = b.Result switch
            {
                "succeeded" => "[green]+[/]",
                "failed" => "[red]X[/]",
                "partiallySucceeded" => "[yellow]![/]",
                _ => "[dim]-[/]",
            };
            var pr = b.PrNumber is not null ? $" PR#{b.PrNumber}" : "";
            var time = BrowserUI.FormatTime(b.FinishTime);
            return $"{resultIcon} {b.BuildId} {Markup.Escape(b.DefinitionName)} {time}{pr}";
        }).ToList();

        var commands = new List<CommandBarItem>();
        var selected = _ui.SelectInPanel(
            ["Builds", "Tests", Markup.Escape(shortTitle), "Builds"],
            $"[dim]{builds.Count} build(s) with this failure[/]",
            choices,
            commands);

        if (selected < 0)
        {
            return NavAction.Back.Instance;
        }

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
            _ui.RenderDetailPanel(
                ["Builds", $"#{page.BuildId}", "Jobs"],
                null,
                () => _ui.RenderPanelLine("[green]No timeline issues recorded for this build.[/]"),
                "[blue]Esc[/] Back");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var truncate = true;
        var errorsOnly = false;

        while (true)
        {
            var commands = new List<CommandBarItem>
            {
                new(errorsOnly ? "Errors: showing" : "Errors only", ConsoleKey.E, -2),
                new(truncate ? "Truncate: off" : "Truncate: on", ConsoleKey.T, -3),
            };

            _ui.RenderDetailPanel(
                ["Builds", $"#{page.BuildId}", "Jobs"],
                errorsOnly ? "[dim]Showing errors only[/]" : null,
                () =>
                {
                    foreach (var (jobName, issues) in jobIssues)
                    {
                        var filtered = errorsOnly
                            ? issues.Where(i => i.Type == "error").ToList()
                            : issues;

                        if (filtered.Count == 0)
                        {
                            continue;
                        }

                        var errorCount = issues.Count(i => i.Type == "error");
                        var warnCount = issues.Count(i => i.Type == "warning");
                        var summary = new List<string>();
                        if (errorCount > 0)
                        {
                            summary.Add($"[red]{errorCount} error(s)[/]");
                        }
                        if (warnCount > 0)
                        {
                            summary.Add($"[yellow]{warnCount} warning(s)[/]");
                        }
                        _ui.RenderPanelLine($"[bold]{Markup.Escape(jobName)}[/]  {string.Join(" ", summary)}");

                        foreach (var (type, message) in filtered.Take(10))
                        {
                            var icon = type == "error" ? "[red]error[/]" : "[yellow]warn[/]";
                            var msg = message.ReplaceLineEndings(" ");
                            if (truncate && msg.Length > 120)
                            {
                                msg = msg[..117] + "...";
                            }
                            _ui.RenderPanelLine($"  {icon}: {Markup.Escape(msg)}");
                        }

                        if (filtered.Count > 10)
                        {
                            _ui.RenderPanelLine($"  [dim]... and {filtered.Count - 10} more[/]");
                        }

                        _ui.RenderEmptyLine();
                    }
                },
                PanelRenderer.BuildCommandBarString(commands));

            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Escape or ConsoleKey.B)
            {
                return NavAction.Back.Instance;
            }
            if (key.Key == ConsoleKey.T)
            {
                truncate = !truncate;
            }
            if (key.Key == ConsoleKey.E)
            {
                errorsOnly = !errorsOnly;
            }
        }
    }

    // ── Key Navigation (for detail pages) ───────────────────────────

    private NavAction ReadNavKey(BuildDetailPage page)
    {
        while (true)
        {
            var key = Console.ReadKey(true);
            if (_ui.HandleDetailScroll(key)) continue;

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
        var helixItems = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT DISTINCT tr.helix_job_name, tr.helix_work_item_name, hw.state, hw.exit_code, hw.console_output_uri, hw.is_deadletter
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

            var helixItems = new List<(string Job, string WorkItem, string? State, int? ExitCode, string? ConsoleUri, bool IsDeadletter)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                helixItems.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    !reader.IsDBNull(5) && reader.GetInt32(5) != 0));
            }
            return helixItems;
        });

        _ui.RenderDetailPanel(
            ["Builds", $"#{page.BuildId}", "Helix Work Items"],
            $"[dim]{helixItems.Count} work item(s)[/]",
            () =>
            {
                if (helixItems.Count == 0)
                {
                    _ui.RenderPanelLine("[yellow]No Helix work items found for failed tests in this build.[/]");
                    return;
                }

                foreach (var (job, wi, state, exitCode, consoleUri, isDeadletter) in helixItems)
                {
                    if (isDeadletter)
                    {
                        _ui.RenderPanelLine($"  [bold red]!! DEAD LETTER[/] [bold]{Markup.Escape(wi)}[/]");
                    }
                    else
                    {
                        var stateInfo = state is not null ? $" [{(exitCode == 0 ? "green" : "red")}]{state} (exit {exitCode})[/]" : "";
                        _ui.RenderPanelLine($"  [bold]{Markup.Escape(wi)}[/]{stateInfo}");
                    }

                    var url = consoleUri ?? HelixClient.GetConsoleUrl(job, wi);
                    _ui.RenderPanelLine($"    {BrowserUI.FormatLink(url, "Console Log")}");
                }
            },
            "[blue]Esc[/] Back");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (_ui.HandleDetailScroll(key)) continue;
            if (key.Key == ConsoleKey.Escape) return;
        }
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

        var analysisBrowser = new AnalysisBrowser(_db, _analysisService, _clientFactory, _configDirectory);
        analysisBrowser.ShowAnalysisDetail(analysis);
    }

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
        public sealed record Refresh : NavAction
        {
            public static readonly Refresh Instance = new();
        }
        public static readonly NavAction Exit = Back.Instance;
    }

    private record BuildRow(
        string Org, string Project, int BuildId, string BuildNumber,
        string DefinitionName, string? Result, string Branch,
        int? PrNumber, string? FinishTime, string IngestionStatus = "pending",
        int DefinitionId = 0, string? RepositoryName = null);
}


