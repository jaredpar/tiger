using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browser-like navigation for exploring builds, tests, and failures.
/// Supports back/forward navigation like a web browser.
/// </summary>
public sealed class BuildBrowser
{
    private readonly TigerDatabase _db;
    private readonly List<Page> _history = [];
    private readonly BuildFilter _filter = new();
    private int _position = -1;

    public BuildBrowser(TigerDatabase db)
    {
        _db = db;
    }

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
                ? "[dim]F filter  C clear  H help  Esc back[/]"
                : "[dim]F filter  H help  Esc back[/]");
            AnsiConsole.WriteLine();

            var builds = QueryBuilds();

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine(_filter.IsActive
                    ? "[yellow]No builds match the current filter.[/]"
                    : "[yellow]No builds ingested yet.[/]");
                AnsiConsole.MarkupLine("[dim]Press F to change filter, Esc to go back...[/]");

                var emptyKey = Console.ReadKey(true);
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
                    { ConsoleKey.F, -2 },
                    { ConsoleKey.H, -3 },
                    { ConsoleKey.C, -4 },
                });

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
            where.Add("repository_name LIKE @repo");
            cmd.Parameters.AddWithValue("@repo", BuildFilter.ToLikePattern(_filter.RepoPattern));
        }
        if (_filter.DefinitionPattern is not null)
        {
            where.Add("definition_name LIKE @def");
            cmd.Parameters.AddWithValue("@def", BuildFilter.ToLikePattern(_filter.DefinitionPattern));
        }
        if (_filter.NumberPattern is not null)
        {
            where.Add("build_number LIKE @num");
            cmd.Parameters.AddWithValue("@num", BuildFilter.ToLikePattern(_filter.NumberPattern));
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

    private void ShowFilterMenu()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Set Filter[/]");
        AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(_filter.ToString())}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [blue]R[/] Filter by repository");
        AnsiConsole.MarkupLine("  [blue]D[/] Filter by definition");
        AnsiConsole.MarkupLine("  [blue]N[/] Filter by build number");
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
            case ConsoleKey.C:
                _filter.Clear();
                break;
        }
    }

    private static string? PromptPattern(string prompt)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(prompt)}[/]");
        AnsiConsole.Markup("[blue]> [/]");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? null : input;
    }

    private static void ShowFilterHelp()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Filter Help[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Matching:[/]");
        AnsiConsole.MarkupLine("  Plain text does a [blue]contains[/] match:");
        AnsiConsole.MarkupLine("    [dim]roslyn → matches 'dotnet/roslyn', 'roslyn-CI', etc.[/]");
        AnsiConsole.MarkupLine("  Use [blue]*[/] as a wildcard:");
        AnsiConsole.MarkupLine("    [dim]dotnet/* → matches 'dotnet/roslyn', 'dotnet/runtime', etc.[/]");
        AnsiConsole.MarkupLine("    [dim]14* → matches build numbers starting with '14'[/]");
        AnsiConsole.MarkupLine("    [dim]*-CI → matches definition names ending with '-CI'[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Filter types:[/]");
        AnsiConsole.MarkupLine("  [blue]R[/]  Repository name (e.g. roslyn, dotnet/runtime)");
        AnsiConsole.MarkupLine("  [blue]D[/]  Definition/pipeline name (e.g. ci, roslyn-CI)");
        AnsiConsole.MarkupLine("  [blue]N[/]  Build number (e.g. 14*, 20250517*)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── Build Detail ────────────────────────────────────────────────

    private NavAction RenderBuildDetail(BuildDetailPage page)
    {
        // Header info
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

        // Pass/fail counts
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

        // Failed test runs section
        AnsiConsole.MarkupLine("[bold underline]Failed Test Runs[/]");
        using var runsCmd = _db.Connection.CreateCommand();
        runsCmd.CommandText = """
            SELECT run_name, failed_tests, total_tests
            FROM test_runs
            WHERE organization = @org AND project = @proj AND build_id = @buildId
                  AND failed_tests > 0
            ORDER BY failed_tests DESC
            LIMIT 15
            """;
        runsCmd.Parameters.AddWithValue("@org", page.Org);
        runsCmd.Parameters.AddWithValue("@proj", page.Project);
        runsCmd.Parameters.AddWithValue("@buildId", page.BuildId);

        using var runsReader = runsCmd.ExecuteReader();
        var hasFailedRuns = false;
        while (runsReader.Read())
        {
            hasFailedRuns = true;
            var name = runsReader.GetString(0);
            var failed = runsReader.GetInt32(1);
            var total = runsReader.GetInt32(2);
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(name)}  [red]{failed}[/]/{total} failed");
        }
        if (!hasFailedRuns)
            AnsiConsole.MarkupLine("  [green]No failed test runs[/]");
        runsReader.Close();

        AnsiConsole.WriteLine();

        // Failed tests section
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
            // Replace newlines in error for single-line display
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
                    // Jobs drill-down — show failed test runs (same as test runs section, interactive)
                    return new NavAction.Push(new TestListPage(page.Org, page.Project, page.BuildId));
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

    /// <summary>
    /// Persistent build filter. Survives navigation within the browser session.
    /// </summary>
    private sealed class BuildFilter
    {
        public string? RepoPattern { get; set; }
        public string? DefinitionPattern { get; set; }
        public string? NumberPattern { get; set; }

        public bool IsActive => RepoPattern is not null || DefinitionPattern is not null || NumberPattern is not null;

        public void Clear()
        {
            RepoPattern = null;
            DefinitionPattern = null;
            NumberPattern = null;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (NumberPattern is not null) parts.Add($"num:{NumberPattern}");
            return parts.Count > 0 ? string.Join(" ", parts) : "(none)";
        }

        /// <summary>
        /// Converts a user glob pattern (with *) to a SQL LIKE pattern.
        /// If no wildcard present, wraps in %...% for contains matching.
        /// </summary>
        public static string ToLikePattern(string input)
        {
            // Escape SQL LIKE special chars (% and _) in user input, then convert * to %
            var escaped = input.Replace("%", "[%]").Replace("_", "[_]");
            var pattern = escaped.Replace("*", "%");
            // If no wildcard was present, do contains match
            if (!pattern.Contains('%'))
                pattern = $"%{pattern}%";
            return pattern;
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
