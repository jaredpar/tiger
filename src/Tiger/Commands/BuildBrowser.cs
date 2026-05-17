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
                var result = FormatResultPlain(b.Result);
                var pr = b.PrNumber is not null ? $" PR#{b.PrNumber}" : "";
                return $"#{b.BuildId} {b.DefinitionName} {b.BuildNumber}{pr} {result}";
            }).ToList();

            var selected = SelectWithEscape("Select a build:", choices,
                extraKeys: new Dictionary<ConsoleKey, int> {
                    { ConsoleKey.E, -5 },
                    { ConsoleKey.F, -2 },
                    { ConsoleKey.H, -3 },
                    { ConsoleKey.C, -4 },
                });

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
            where.Add(isExact ? "repository_name = @repo" : "repository_name LIKE @repo");
            cmd.Parameters.AddWithValue("@repo", pattern);
        }
        if (_filter.DefinitionPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.DefinitionPattern);
            where.Add(isExact ? "definition_name = @def" : "definition_name LIKE @def");
            cmd.Parameters.AddWithValue("@def", pattern);
        }
        if (_filter.NumberPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.NumberPattern);
            where.Add(isExact ? "build_number = @num" : "build_number LIKE @num");
            cmd.Parameters.AddWithValue("@num", pattern);
        }
        if (_filter.ResultPattern is not null)
        {
            var (pattern, isExact) = BuildFilter.ToSqlPattern(_filter.ResultPattern);
            where.Add(isExact ? "result = @result" : "result LIKE @result");
            cmd.Parameters.AddWithValue("@result", pattern);
        }

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT organization, project, build_id, build_number, definition_name,
                   result, source_branch, pr_number, finish_time
            FROM builds
            {whereClause}
            ORDER BY finish_time DESC, ingested_at DESC
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
                reader.IsDBNull(8) ? null : reader.GetString(8)));
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
        AnsiConsole.MarkupLine("  [blue]N[/] Filter by build number");
        AnsiConsole.MarkupLine("  [blue]O[/] Filter by outcome (failed, succeeded, partiallySucceeded)");
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
            case ConsoleKey.N:
                _filter.NumberPattern = PromptPattern("Build number pattern (e.g. 14*, 20250517*):");
                break;
            case ConsoleKey.O:
                _filter.ResultPattern = PromptResultFilter();
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
        var choices = new[] { "failed", "succeeded", "partiallySucceeded" };
        var selected = SelectWithEscape("Select outcome:", choices.ToList(), pageSize: 5);
        return selected >= 0 ? choices[selected] : null;
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
        AnsiConsole.MarkupLine("  [blue]num:[/]     Build number");
        AnsiConsole.MarkupLine("  [blue]result:[/]  Outcome (failed, succeeded, partiallySucceeded)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── Build Detail ────────────────────────────────────────────────

    private NavAction RenderBuildDetail(BuildDetailPage page)
    {
        // Header info from DB
        using var buildCmd = _db.Connection.CreateCommand();
        buildCmd.CommandText = """
            SELECT build_number, definition_name, result, source_branch, pr_number, finish_time
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
        reader.Close();

        var url = $"https://dev.azure.com/{Uri.EscapeDataString(page.Org)}/{Uri.EscapeDataString(page.Project)}/_build/results?buildId={page.BuildId}";

        // Build header
        var headerTable = new Table().Border(TableBorder.Rounded).Expand();
        headerTable.AddColumn(new TableColumn("").NoWrap());
        headerTable.AddColumn(new TableColumn(""));
        headerTable.HideHeaders();

        headerTable.AddRow("[bold]Build[/]", $"#{page.BuildId} — {defName} {buildNumber}");
        headerTable.AddRow("[bold]Result[/]", FormatResult(result));
        headerTable.AddRow("[bold]Branch[/]", branch);
        if (prNumber is not null)
            headerTable.AddRow("[bold]PR[/]", $"#{prNumber}");
        if (finishTime is not null)
            headerTable.AddRow("[bold]Finished[/]", finishTime);
        headerTable.AddRow("[bold]URL[/]", $"[link={url}]{url}[/]");

        // Pass/fail counts from DB
        using var countCmd = _db.Connection.CreateCommand();
        countCmd.CommandText = """
            SELECT
                COALESCE(SUM(total_tests), 0),
                COALESCE(SUM(passed_tests), 0),
                COALESCE(SUM(failed_tests), 0),
                COALESCE(SUM(skipped_tests), 0)
            FROM test_runs
            WHERE organization = @org AND project = @proj AND build_id = @buildId
            """;
        countCmd.Parameters.AddWithValue("@org", page.Org);
        countCmd.Parameters.AddWithValue("@proj", page.Project);
        countCmd.Parameters.AddWithValue("@buildId", page.BuildId);
        using var countReader = countCmd.ExecuteReader();
        if (countReader.Read())
        {
            var total = countReader.GetInt64(0);
            var passed = countReader.GetInt64(1);
            var failed = countReader.GetInt64(2);
            var skipped = countReader.GetInt64(3);
            headerTable.AddRow("[bold]Tests[/]",
                $"[green]{passed} passed[/] / [red]{failed} failed[/] / [dim]{skipped} skipped[/] ({total} total)");
        }
        countReader.Close();

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Failed tests section (from DB)
        AnsiConsole.MarkupLine("[bold underline]Failed Tests[/]");
        using var testsCmd = _db.Connection.CreateCommand();
        testsCmd.CommandText = """
            SELECT tr.test_case_title, tr.error_message
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
            ORDER BY tr.test_case_title
            LIMIT 20
            """;
        testsCmd.Parameters.AddWithValue("@org", page.Org);
        testsCmd.Parameters.AddWithValue("@proj", page.Project);
        testsCmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var testsReader = testsCmd.ExecuteReader();
        var hasFailedTests = false;
        while (testsReader.Read())
        {
            hasFailedTests = true;
            var title = testsReader.GetString(0);
            var error = testsReader.IsDBNull(1) ? "" : testsReader.GetString(1);
            if (title.Length > 70) title = title[..67] + "...";
            if (error.Length > 60) error = error[..57] + "...";
            error = error.ReplaceLineEndings(" ");
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(title)}");
            if (!string.IsNullOrWhiteSpace(error))
                AnsiConsole.MarkupLine($"    [dim]{Markup.Escape(error)}[/]");
        }
        if (!hasFailedTests)
            AnsiConsole.MarkupLine("  [green]All tests passed[/]");
        testsReader.Close();

        AnsiConsole.WriteLine();

        // Navigation keys
        var canForward = _position < _history.Count - 1;
        AnsiConsole.MarkupLine("[bold]Navigation:[/]");
        AnsiConsole.MarkupLine("  [blue]T[/] Tests   [blue]J[/] Jobs   [blue]H[/] Helix   [blue]B[/] Back" +
            (canForward ? "   [blue]F[/] Forward" : ""));

        return ReadNavKey(page);
    }

    // ── Test List (for a build) ─────────────────────────────────────

    private NavAction RenderTestList(TestListPage page)
    {
        AnsiConsole.MarkupLine($"[bold underline]Failed Tests — Build #{page.BuildId}[/]");
        AnsiConsole.MarkupLine("[dim]Select a test to see its failure history, or Escape to go back[/]");
        AnsiConsole.WriteLine();

        var tests = new List<(string Title, int Count)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.test_case_title, COUNT(*) as cnt
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE r.organization = @org AND r.project = @proj AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
            GROUP BY tr.test_case_title
            ORDER BY tr.test_case_title
            """;
        cmd.Parameters.AddWithValue("@org", page.Org);
        cmd.Parameters.AddWithValue("@proj", page.Project);
        cmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tests.Add((reader.GetString(0), reader.GetInt32(1)));
        reader.Close();

        if (tests.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No failed tests in this build.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        var choices = tests.Select(t =>
        {
            var title = t.Title.Length > 80 ? t.Title[..77] + "..." : t.Title;
            return title;
        }).ToList();

        var selected = SelectWithEscape($"{tests.Count} failed test(s):", choices);

        if (selected < 0)
            return NavAction.Back.Instance;

        return new NavAction.Push(
            new TestDetailPage(page.Org, page.Project, tests[selected].Title));
    }

    // ── Test Detail (across builds) ─────────────────────────────────

    private NavAction RenderTestDetail(TestDetailPage page)
    {
        var shortTitle = page.TestName.Length > 60 ? page.TestName[..57] + "..." : page.TestName;
        AnsiConsole.MarkupLine($"[bold underline]Test Failure History[/]");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(shortTitle)}[/]");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(page.Org)}/{Markup.Escape(page.Project)}[/]");
        AnsiConsole.WriteLine();

        var builds = new List<BuildRow>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT b.organization, b.project, b.build_id, b.build_number,
                   b.definition_name, b.result, b.source_branch, b.pr_number, b.finish_time
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
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        reader.Close();

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds found with this test failure.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return NavAction.Back.Instance;
        }

        AnsiConsole.MarkupLine($"[bold]Failed in {builds.Count} build(s):[/]");
        AnsiConsole.WriteLine();

        var choices = builds.Select(b =>
        {
            var result = FormatResultPlain(b.Result);
            var pr = b.PrNumber is not null ? $" PR#{b.PrNumber}" : "";
            var time = b.FinishTime ?? "";
            return $"#{b.BuildId} {b.DefinitionName} {b.BuildNumber}{pr} [{result}] {time}";
        }).ToList();

        var selected = SelectWithEscape("Select a build to view details:", choices);

        if (selected < 0)
            return NavAction.Back.Instance;

        var b2 = builds[selected];
        return new NavAction.Push(new BuildDetailPage(b2.Org, b2.Project, b2.BuildId));
    }

    // ── Job List (timeline) ─────────────────────────────────────────

    private NavAction RenderJobList(JobListPage page)
    {
        AnsiConsole.MarkupLine($"[bold underline]Failed Jobs — Build #{page.BuildId}[/]");
        AnsiConsole.WriteLine();

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

        // Group by job (parent_name for task issues, record_name for job-level issues)
        var jobIssues = new Dictionary<string, List<(string Type, string Message)>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var parentName = reader.IsDBNull(0) ? null : reader.GetString(0);
            var recordName = reader.GetString(1);
            var recordType = reader.GetString(2);
            var issueType = reader.GetString(4);
            var message = reader.GetString(5);

            // Use parent_name as the job key for Task records, record_name for Job records
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
                if (msg.Length > 120) msg = msg[..117] + "...";
                AnsiConsole.MarkupLine($"  {icon}: {Markup.Escape(msg)}");
            }

            if (issues.Count > 10)
                AnsiConsole.MarkupLine($"  [dim]... and {issues.Count - 10} more[/]");

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[dim]Press Esc/B to go back...[/]");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Escape or ConsoleKey.B)
                return NavAction.Back.Instance;
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
        Dictionary<ConsoleKey, int>? extraKeys = null)
    {
        if (items.Count == 0) return -1;

        var selected = 0;
        var scrollOffset = 0;
        var visibleCount = Math.Min(pageSize, items.Count);

        while (true)
        {
            // Clear and render
            AnsiConsole.Cursor.SetPosition(0, Console.CursorTop);

            // Render visible items
            for (var i = 0; i < visibleCount; i++)
            {
                var idx = scrollOffset + i;
                if (idx >= items.Count) break;

                if (idx == selected)
                    AnsiConsole.MarkupLine($"  [blue]>[/] [bold]{Markup.Escape(items[idx])}[/]");
                else
                    AnsiConsole.MarkupLine($"    {Markup.Escape(items[idx])}");
            }

            if (items.Count > visibleCount)
                AnsiConsole.MarkupLine($"  [dim]({selected + 1}/{items.Count})[/]");

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

            // Move cursor back up to re-render
            var linesToClear = visibleCount + (items.Count > visibleCount ? 2 : 1);
            for (var i = 0; i < linesToClear; i++)
            {
                AnsiConsole.Cursor.SetPosition(0, Console.CursorTop - 1);
                AnsiConsole.Write(new string(' ', Console.WindowWidth));
                AnsiConsole.Cursor.SetPosition(0, Console.CursorTop);
            }
        }
    }

    private static string FormatResult(string? result) => result switch
    {
        "succeeded" => "[green]✓ succeeded[/]",
        "failed" => "[red]✗ failed[/]",
        "partiallySucceeded" => "[yellow]⚠ partial[/]",
        "canceled" => "[dim]canceled[/]",
        null => "[dim]—[/]",
        _ => result,
    };

    private static string FormatResultPlain(string? result) => result switch
    {
        "succeeded" => "✓",
        "failed" => "✗",
        "partiallySucceeded" => "⚠",
        _ => result ?? "—",
    };

    // ── Page and Navigation Types ───────────────────────────────────

    private abstract record Page;
    private record BuildListPage : Page;
    private record BuildDetailPage(string Org, string Project, int BuildId) : Page;
    private record TestListPage(string Org, string Project, int BuildId) : Page;
    private record TestDetailPage(string Org, string Project, string TestName) : Page;
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
        public string? NumberPattern { get; set; }
        public string? ResultPattern { get; set; }

        public bool IsActive => RepoPattern is not null || DefinitionPattern is not null
            || NumberPattern is not null || ResultPattern is not null;

        public void Clear()
        {
            RepoPattern = null;
            DefinitionPattern = null;
            NumberPattern = null;
            ResultPattern = null;
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
                else if (part.StartsWith("num:", StringComparison.OrdinalIgnoreCase))
                    NumberPattern = part[4..];
                else if (part.StartsWith("result:", StringComparison.OrdinalIgnoreCase))
                    ResultPattern = part[7..];
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (NumberPattern is not null) parts.Add($"num:{NumberPattern}");
            if (ResultPattern is not null) parts.Add($"result:{ResultPattern}");
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

            // Escape SQL LIKE special chars, then convert * to %
            var escaped = input.Replace("%", "[%]").Replace("_", "[_]");
            var pattern = escaped.Replace("*", "%");
            // If no wildcard was present, do contains match
            if (!pattern.Contains('%'))
                pattern = $"%{pattern}%";
            return (pattern, false);
        }
    }

    private abstract record NavAction
    {
        public record Push(Page Page) : NavAction;
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
        int? PrNumber, string? FinishTime);
}
