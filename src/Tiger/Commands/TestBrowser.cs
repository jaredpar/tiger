using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browsable, filterable view of test failures across builds.
/// Uses BrowserUI for shared components and BuildBrowser for build drill-down.
/// </summary>
public sealed class TestBrowser
{
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly string _configDirectory;
    private readonly TestFilter _filter;

    public TestBrowser(TigerDatabase db, AzdoClientFactory clientFactory, string configDirectory)
    {
        _db = db;
        _clientFactory = clientFactory;
        _configDirectory = configDirectory;
        _filter = TestFilter.Load(configDirectory);
    }

    private void SaveFilter() => _filter.Save(_configDirectory);

    public void Browse()
    {
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold underline]Test Failures[/]");
            if (_filter.IsActive)
                AnsiConsole.MarkupLine($"[dim]Filter: {Markup.Escape(_filter.ToString())}[/]");
            AnsiConsole.MarkupLine(_filter.IsActive
                ? "[dim]E edit filter  F filter menu  C clear  H help  Esc back[/]"
                : "[dim]E edit filter  F filter menu  H help  Esc back[/]");
            AnsiConsole.WriteLine();

            var tests = QueryTests();

            if (tests.Count == 0)
            {
                AnsiConsole.MarkupLine(_filter.IsActive
                    ? "[yellow]No test failures match the current filter.[/]"
                    : "[yellow]No test failures recorded yet.[/]");
                AnsiConsole.MarkupLine("[dim]Press E to edit filter, F for filter menu, Esc to go back...[/]");

                var emptyKey = Console.ReadKey(true);
                if (emptyKey.Key == ConsoleKey.E) { EditFilter(); continue; }
                if (emptyKey.Key == ConsoleKey.F) { ShowFilterMenu(); continue; }
                if (emptyKey.Key == ConsoleKey.H) { ShowFilterHelp(); continue; }
                if (emptyKey.Key == ConsoleKey.C) { _filter.Clear(); SaveFilter(); continue; }
                return;
            }

            AnsiConsole.MarkupLine($"[dim]{tests.Count} failed test(s)[/]");
            AnsiConsole.WriteLine();

            var choices = tests.Select(t =>
            {
                var title = t.TestName.Length > 70 ? t.TestName[..67] + "..." : t.TestName;
                return $"[red]✗[/] {Markup.Escape(title)}  [dim]({t.FailCount} build(s))[/]";
            }).ToList();

            var selected = BrowserUI.SelectWithEscape("Select a test:", choices,
                extraKeys: new Dictionary<ConsoleKey, int> {
                    { ConsoleKey.E, -5 },
                    { ConsoleKey.F, -2 },
                    { ConsoleKey.H, -3 },
                    { ConsoleKey.C, -4 },
                },
                useMarkup: true);

            if (selected == -5) { EditFilter(); continue; }
            if (selected == -2) { ShowFilterMenu(); continue; }
            if (selected == -3) { ShowFilterHelp(); continue; }
            if (selected == -4) { _filter.Clear(); SaveFilter(); continue; }
            if (selected < 0) return;

            var test = tests[selected];
            ShowTestDetail(test);
        }
    }

    private void ShowTestDetail(TestRow test)
    {
        while (true)
        {
            AnsiConsole.Clear();

            var info = BrowserUI.LoadTestDetail(_db, test.Org, test.Project, test.TestName);
            if (info is null)
            {
                AnsiConsole.MarkupLine("[yellow]No test failure data found.[/]");
                AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
                Console.ReadKey(true);
                return;
            }

            BrowserUI.RenderTestDetail(info);

            AnsiConsole.MarkupLine("[bold]Navigation:[/]");
            AnsiConsole.MarkupLine("  [blue]B[/] View builds with this failure   [blue]Esc[/] Back");

            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                    return;
                if (key.Key == ConsoleKey.B)
                {
                    ShowTestBuilds(test);
                    break; // re-render detail after returning
                }
            }
        }
    }

    private void ShowTestBuilds(TestRow test)
    {
        AnsiConsole.Clear();
        var shortTitle = test.TestName.Length > 60 ? test.TestName[..57] + "..." : test.TestName;
        AnsiConsole.MarkupLine("[bold underline]Builds with failure[/]");
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(shortTitle)}[/]");
        AnsiConsole.WriteLine();

        var builds = QueryTestBuilds(test);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds found.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return;
        }

        var choices = builds.Select(b =>
            BrowserUI.FormatBuildChoice(b.BuildId, b.DefinitionName, b.Result,
                b.FinishTime, b.PrNumber)).ToList();

        var selected = BrowserUI.SelectWithEscape("Select a build:", choices, useMarkup: true);

        if (selected >= 0)
        {
            var b = builds[selected];
            // Navigate into the full BuildBrowser for this build
            var browser = new BuildBrowser(_db, _clientFactory, _configDirectory);
            browser.BrowseBuild(b.Org, b.Project, b.BuildId);
        }
    }

    // ── Queries ──────────────────────────────────────────────────────

    private List<TestRow> QueryTests()
    {
        return _db.WithCommand(cmd =>
        {
            var tests = new List<TestRow>();

            var where = new List<string> { "tr.outcome = 'Failed'" };
            if (_filter.TestNamePattern is not null)
            {
                var (pattern, isExact) = BrowserUI.ToSqlPattern(_filter.TestNamePattern);
                where.Add(isExact ? "tr.test_case_title = @name" : "tr.test_case_title LIKE @name");
                cmd.Parameters.AddWithValue("@name", pattern);
            }
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
            BrowserUI.ApplyKindFilter(_filter.KindPattern, where);

            var whereClause = "WHERE " + string.Join(" AND ", where);

            cmd.CommandText = $"""
                SELECT tr.test_case_title, COUNT(DISTINCT r.build_id) as fail_count,
                       MIN(r.organization) as org, MIN(r.project) as proj
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                JOIN builds b ON r.organization = b.organization AND r.build_id = b.build_id
                {whereClause}
                GROUP BY tr.test_case_title
                ORDER BY fail_count DESC
                LIMIT 50
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tests.Add(new TestRow(
                    reader.GetString(0), reader.GetInt32(1),
                    reader.GetString(2), reader.GetString(3)));
            }
            return tests;
        });
    }

    private List<BuildRow> QueryTestBuilds(TestRow test)
    {
        return _db.WithCommand(cmd =>
        {
            var builds = new List<BuildRow>();
            cmd.CommandText = """
                SELECT DISTINCT b.organization, b.project, b.build_id, b.definition_name,
                       b.result, b.pr_number, b.finish_time
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                JOIN builds b ON r.organization = b.organization AND r.build_id = b.build_id
                WHERE tr.organization = @org AND tr.project = @proj
                      AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
                ORDER BY b.finish_time DESC
                LIMIT 30
                """;
            cmd.Parameters.AddWithValue("@org", test.Org);
            cmd.Parameters.AddWithValue("@proj", test.Project);
            cmd.Parameters.AddWithValue("@testName", test.TestName);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                builds.Add(new BuildRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            return builds;
        });
    }

    // ── Filter UI ────────────────────────────────────────────────────

    private void EditFilter()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Edit Filter[/]");
        AnsiConsole.MarkupLine("[dim]Syntax: test:VALUE repo:VALUE def:VALUE[/]");
        AnsiConsole.MarkupLine("[dim]Examples: test:Serialization  repo:roslyn  def:*-CI[/]");
        AnsiConsole.MarkupLine("[dim]Append ! for exact match. Press Esc to cancel[/]");
        AnsiConsole.WriteLine();
        if (_filter.IsActive)
            AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(_filter.ToString())}[/]");
        AnsiConsole.Markup("[blue]> [/]");

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape) return;
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
                if (buffer.Length > 0) { buffer.Remove(buffer.Length - 1, 1); Console.Write("\b \b"); }
                continue;
            }
            if (key.KeyChar >= 32) { buffer.Append(key.KeyChar); Console.Write(key.KeyChar); }
        }
    }

    private void ShowFilterMenu()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Set Filter[/]");
        AnsiConsole.MarkupLine($"[dim]Current: {Markup.Escape(_filter.ToString())}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [blue]N[/] Filter by test name");
        AnsiConsole.MarkupLine("  [blue]R[/] Filter by repository");
        AnsiConsole.MarkupLine("  [blue]D[/] Filter by definition");
        AnsiConsole.MarkupLine("  [blue]K[/] Filter by kind (pr, ci)");
        AnsiConsole.MarkupLine("  [blue]C[/] Clear all filters");
        AnsiConsole.MarkupLine("  [blue]Esc[/] Cancel");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.N:
                _filter.TestNamePattern = BrowserUI.PromptPattern("Test name pattern (e.g. Serialization, *EditAndContinue*):");
                break;
            case ConsoleKey.R:
                _filter.RepoPattern = BrowserUI.PromptPattern("Repository pattern (e.g. roslyn, dotnet/*):");
                break;
            case ConsoleKey.D:
                _filter.DefinitionPattern = BrowserUI.PromptPattern("Definition pattern (e.g. ci, roslyn-CI*):");
                break;
            case ConsoleKey.K:
                _filter.KindPattern = BrowserUI.PromptKindFilter();
                break;
            case ConsoleKey.C:
                _filter.Clear();
                break;
        }
        SaveFilter();
    }

    private static void ShowFilterHelp()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[bold underline]Test Filter Help[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Quick filter (E):[/]");
        AnsiConsole.MarkupLine("  Type an expression like: [blue]test:Serialization repo:roslyn[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Matching (default: contains / LIKE):[/]");
        AnsiConsole.MarkupLine("  [dim]Serial → matches tests containing 'Serial'[/]");
        AnsiConsole.MarkupLine("  [dim]*EditAndContinue* → matches tests with 'EditAndContinue'[/]");
        AnsiConsole.MarkupLine("  [dim]roslyn → matches repos containing 'roslyn'[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Exact match (append !):[/]");
        AnsiConsole.MarkupLine("  [dim]dotnet/roslyn! → matches exactly 'dotnet/roslyn'[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Filter prefixes:[/]");
        AnsiConsole.MarkupLine("  [blue]test:[/]  Test name");
        AnsiConsole.MarkupLine("  [blue]repo:[/]  Repository name");
        AnsiConsole.MarkupLine("  [blue]def:[/]   Definition/pipeline name");
        AnsiConsole.MarkupLine("  [blue]kind:[/]  Build kind (pr, ci)");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── Types ────────────────────────────────────────────────────────

    private record TestRow(string TestName, int FailCount, string Org, string Project);

    private record BuildRow(string Org, string Project, int BuildId, string DefinitionName,
        string? Result, int? PrNumber, string? FinishTime);

    private sealed class TestFilter
    {
        private const string FileName = "test-filter.json";

        private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public string? TestNamePattern { get; set; }
        public string? RepoPattern { get; set; }
        public string? DefinitionPattern { get; set; }
        public string? KindPattern { get; set; }

        public bool IsActive => TestNamePattern is not null || RepoPattern is not null
            || DefinitionPattern is not null || KindPattern is not null;

        public void Clear()
        {
            TestNamePattern = null;
            RepoPattern = null;
            DefinitionPattern = null;
            KindPattern = null;
        }

        public void ParseExpression(string expression)
        {
            Clear();
            var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.StartsWith("test:", StringComparison.OrdinalIgnoreCase))
                    TestNamePattern = part[5..];
                else if (part.StartsWith("repo:", StringComparison.OrdinalIgnoreCase))
                    RepoPattern = part[5..];
                else if (part.StartsWith("def:", StringComparison.OrdinalIgnoreCase))
                    DefinitionPattern = part[4..];
                else if (part.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
                    KindPattern = part[5..];
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (TestNamePattern is not null) parts.Add($"test:{TestNamePattern}");
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (KindPattern is not null) parts.Add($"kind:{KindPattern}");
            return parts.Count > 0 ? string.Join(" ", parts) : "(none)";
        }

        public static TestFilter Load(string configDirectory)
        {
            var path = Path.Combine(configDirectory, FileName);
            if (!File.Exists(path)) return new TestFilter();
            try
            {
                var json = File.ReadAllText(path);
                return System.Text.Json.JsonSerializer.Deserialize<TestFilter>(json, s_jsonOptions) ?? new TestFilter();
            }
            catch { return new TestFilter(); }
        }

        public void Save(string configDirectory)
        {
            var path = Path.Combine(configDirectory, FileName);
            var json = System.Text.Json.JsonSerializer.Serialize(this, s_jsonOptions);
            File.WriteAllText(path, json);
        }
    }
}
