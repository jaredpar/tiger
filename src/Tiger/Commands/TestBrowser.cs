using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browsable, filterable view of test failures across builds.
/// Follows the same patterns as BuildBrowser.
/// </summary>
public sealed class TestBrowser
{
    private readonly TigerDatabase _db;
    private readonly string _configDirectory;
    private readonly TestFilter _filter;

    public TestBrowser(TigerDatabase db, string configDirectory)
    {
        _db = db;
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

            var selected = SelectWithEscape("Select a test:", choices,
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
        // Show detail, then optionally navigate to builds list
        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold underline]Test Failure Detail[/]");
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(test.TestName)}[/]");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(test.Org)}/{Markup.Escape(test.Project)}[/]");
            AnsiConsole.WriteLine();

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
            cmd.Parameters.AddWithValue("@org", test.Org);
            cmd.Parameters.AddWithValue("@proj", test.Project);
            cmd.Parameters.AddWithValue("@testName", test.TestName);

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
                AnsiConsole.MarkupLine($"[bold]Failed in:[/] {test.FailCount} build(s)");
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
                    break; // re-render detail after returning from builds
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

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT b.build_id, b.build_number, b.definition_name, b.result,
                   b.source_branch, b.pr_number, b.finish_time
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            JOIN builds b ON r.organization = b.organization AND r.project = b.project AND r.build_id = b.build_id
            WHERE tr.organization = @org AND tr.project = @proj
                  AND tr.test_case_title = @testName AND tr.outcome = 'Failed'
            ORDER BY b.finish_time DESC
            LIMIT 30
            """;
        cmd.Parameters.AddWithValue("@org", test.Org);
        cmd.Parameters.AddWithValue("@proj", test.Project);
        cmd.Parameters.AddWithValue("@testName", test.TestName);

        var builds = new List<(int Id, string Number, string Def, string? Result, string Branch, int? Pr, string? Time)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            builds.Add((
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        reader.Close();

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds found.[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
            return;
        }

        var choices = builds.Select(b =>
        {
            var icon = b.Result switch
            {
                "succeeded" => "[green]✓[/]",
                "failed" => "[red]✗[/]",
                "partiallySucceeded" => "[yellow]⚠[/]",
                _ => "[dim]—[/]",
            };
            var pr = b.Pr is not null ? $" PR#{b.Pr}" : "";
            var time = b.Time ?? "";
            return $"{icon} #{b.Id} {Markup.Escape(b.Def)} {Markup.Escape(b.Number)}{pr} {time}";
        }).ToList();

        var selected = SelectWithEscape("Select a build:", choices, useMarkup: true);
        // For now just show the build ID — full build browser integration can come later
        if (selected >= 0)
        {
            var b = builds[selected];
            AnsiConsole.Clear();
            var url = $"https://dev.azure.com/{Uri.EscapeDataString(test.Org)}/{Uri.EscapeDataString(test.Project)}/_build/results?buildId={b.Id}";
            AnsiConsole.MarkupLine($"[bold]Build #{b.Id}[/] — {Markup.Escape(b.Def)} {Markup.Escape(b.Number)}");
            AnsiConsole.MarkupLine($"[bold]URL:[/] [link={url}]{url}[/]");
            AnsiConsole.MarkupLine("[dim]Press any key to go back...[/]");
            Console.ReadKey(true);
        }
    }

    // ── Query ────────────────────────────────────────────────────────

    private List<TestRow> QueryTests()
    {
        var tests = new List<TestRow>();
        using var cmd = _db.Connection.CreateCommand();

        var where = new List<string> { "tr.outcome = 'Failed'" };
        if (_filter.TestNamePattern is not null)
        {
            var (pattern, isExact) = TestFilter.ToSqlPattern(_filter.TestNamePattern);
            where.Add(isExact ? "tr.test_case_title = @name" : "tr.test_case_title LIKE @name");
            cmd.Parameters.AddWithValue("@name", pattern);
        }
        if (_filter.RepoPattern is not null)
        {
            var (pattern, isExact) = TestFilter.ToSqlPattern(_filter.RepoPattern);
            where.Add(isExact ? "b.repository_name = @repo" : "b.repository_name LIKE @repo");
            cmd.Parameters.AddWithValue("@repo", pattern);
        }
        if (_filter.DefinitionPattern is not null)
        {
            var (pattern, isExact) = TestFilter.ToSqlPattern(_filter.DefinitionPattern);
            where.Add(isExact ? "b.definition_name = @def" : "b.definition_name LIKE @def");
            cmd.Parameters.AddWithValue("@def", pattern);
        }

        var whereClause = "WHERE " + string.Join(" AND ", where);

        cmd.CommandText = $"""
            SELECT tr.test_case_title, COUNT(DISTINCT r.build_id) as fail_count,
                   MIN(r.organization) as org, MIN(r.project) as proj
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            JOIN builds b ON r.organization = b.organization AND r.project = b.project AND r.build_id = b.build_id
            {whereClause}
            GROUP BY tr.test_case_title
            ORDER BY fail_count DESC
            LIMIT 50
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tests.Add(new TestRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return tests;
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
        AnsiConsole.MarkupLine("  [blue]T[/] Filter by test name");
        AnsiConsole.MarkupLine("  [blue]R[/] Filter by repository");
        AnsiConsole.MarkupLine("  [blue]D[/] Filter by definition");
        AnsiConsole.MarkupLine("  [blue]C[/] Clear all filters");
        AnsiConsole.MarkupLine("  [blue]Esc[/] Cancel");

        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.T:
                _filter.TestNamePattern = PromptPattern("Test name pattern (e.g. Serialization, *EditAndContinue*):");
                break;
            case ConsoleKey.R:
                _filter.RepoPattern = PromptPattern("Repository pattern (e.g. roslyn, dotnet/*):");
                break;
            case ConsoleKey.D:
                _filter.DefinitionPattern = PromptPattern("Definition pattern (e.g. ci, roslyn-CI*):");
                break;
            case ConsoleKey.C:
                _filter.Clear();
                break;
        }
        SaveFilter();
    }

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
            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Enter)
            {
                AnsiConsole.WriteLine();
                var result = buffer.ToString().Trim();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) { buffer.Remove(buffer.Length - 1, 1); Console.Write("\b \b"); }
                continue;
            }
            if (key.KeyChar >= 32) { buffer.Append(key.KeyChar); Console.Write(key.KeyChar); }
        }
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
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Multiple filters combine with AND.[/]");
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    // ── SelectWithEscape (same as BuildBrowser) ─────────────────────

    private static int SelectWithEscape(string title, List<string> items, int pageSize = 20,
        Dictionary<ConsoleKey, int>? extraKeys = null, bool useMarkup = false)
    {
        if (items.Count == 0) return -1;

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
                if (idx >= items.Count) break;

                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                var text = useMarkup ? items[idx] : Markup.Escape(items[idx]);
                if (idx == selected)
                    AnsiConsole.MarkupLine($"  [blue]>[/] [bold]{text}[/]");
                else
                    AnsiConsole.MarkupLine($"    {text}");
            }

            if (items.Count > visibleCount)
            {
                Console.Write(new string(' ', Console.WindowWidth));
                Console.SetCursorPosition(0, Console.CursorTop);
                AnsiConsole.MarkupLine($"  [dim]({selected + 1}/{items.Count})[/]");
            }

            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, Console.CursorTop);
            AnsiConsole.MarkupLine("[dim]  ↑↓ navigate  Enter select  Esc back[/]");

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selected > 0)
                    {
                        selected--;
                        if (selected < scrollOffset) scrollOffset = selected;
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
                default:
                    if (extraKeys is not null && extraKeys.TryGetValue(key.Key, out var result))
                        return result;
                    break;
            }
        }
    }

    // ── Types ────────────────────────────────────────────────────────

    private record TestRow(string TestName, int FailCount, string Org, string Project);

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

        public bool IsActive => TestNamePattern is not null || RepoPattern is not null
            || DefinitionPattern is not null;

        public void Clear()
        {
            TestNamePattern = null;
            RepoPattern = null;
            DefinitionPattern = null;
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
            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (TestNamePattern is not null) parts.Add($"test:{TestNamePattern}");
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
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

        public static (string Pattern, bool IsExact) ToSqlPattern(string input)
        {
            if (input.EndsWith('!'))
                return (input[..^1], true);
            var pattern = input.Replace("*", "%");
            if (!pattern.Contains('%'))
                pattern = $"%{pattern}%";
            return (pattern, false);
        }
    }
}
