using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Browsable, filterable view of test failures across builds.
/// Uses BrowserUI for shared components and BuildBrowser for build drill-down.
/// </summary>
public sealed class TestBrowser
{
    private readonly PanelRenderer _ui = PanelRenderer.Create();

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
            var tests = QueryTests();

            var filterText = _filter.IsActive
                ? $"Filter: {Markup.Escape(_filter.ToString())}"
                : "[dim]Filter: (none)[/]";
            var context = tests.Count > 0
                ? $"{filterText}  [dim]({tests.Count} failed test(s))[/]"
                : filterText;

            if (tests.Count == 0)
            {
                var emptyMsg = _filter.IsActive
                    ? "[yellow]No test failures match the current filter.[/]"
                    : "[yellow]No test failures recorded yet.[/]";

                _ui.RenderDetailPanel(
                    ["Tests"],
                    context,
                    () => _ui.RenderPanelLine(emptyMsg),
                    PanelRenderer.BuildCommandBarString(new List<CommandBarItem>
                    {
                        new("Edit filter", ConsoleKey.E, -5),
                        new("Filter menu", ConsoleKey.F, -2),
                    }));

                var emptyKey = Console.ReadKey(true);
                if (emptyKey.Key == ConsoleKey.E) { EditFilter(); continue; }
                if (emptyKey.Key == ConsoleKey.F) { ShowFilterMenu(); continue; }
                if (emptyKey.Key == ConsoleKey.H) { ShowFilterHelp(); continue; }
                if (emptyKey.Key == ConsoleKey.C) { _filter.Clear(); SaveFilter(); continue; }
                return;
            }

            var choices = tests.Select(t =>
            {
                var title = t.TestName.Length > 70 ? t.TestName[..67] + "..." : t.TestName;
                return $"[red]X[/] {Markup.Escape(title)}  [dim]({t.FailCount} build(s))[/]";
            }).ToList();

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
                ["Tests"],
                context,
                choices,
                commands);

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
            var info = BrowserUI.LoadTestDetail(_db, test.Org, test.Project, test.TestName);
            if (info is null)
            {
                _ui.RenderDetailPanel(
                    ["Tests", "Detail"],
                    null,
                    () => _ui.RenderPanelLine("[yellow]No test failure data found.[/]"),
                    "[blue]Esc[/] Back");
                Console.ReadKey(true);
                return;
            }

            var shortTitle = test.TestName.Length > 60 ? test.TestName[..57] + "..." : test.TestName;
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
                ["Tests", Markup.Escape(shortTitle)],
                null,
                () => BrowserUI.RenderTestDetailInPanel(_ui, info),
                PanelRenderer.BuildCommandBarString(commands));

            while (true)
            {
                var key = Console.ReadKey(true);
                if (_ui.HandleDetailScroll(key)) continue;
                if (key.Key == ConsoleKey.Escape)
                {
                    return;
                }
                if (key.Key == ConsoleKey.B)
                {
                    ShowTestBuilds(test);
                    break; // re-render detail after returning
                }
                if (key.Key == ConsoleKey.A)
                {
                    BrowserUI.CreateAgentTask(_db, info);
                    break; // re-render detail after returning
                }
                if (key.Key == ConsoleKey.H && info.HelixJobName is not null)
                {
                    ShowHelixWorkItemDetail(info);
                    break; // re-render detail after returning
                }
            }
        }
    }

    private void ShowHelixWorkItemDetail(BrowserUI.TestDetailInfo info)
    {
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

    private void ShowTestBuilds(TestRow test)
    {
        var shortTitle = test.TestName.Length > 60 ? test.TestName[..57] + "..." : test.TestName;
        var builds = QueryTestBuilds(test);

        if (builds.Count == 0)
        {
            _ui.RenderDetailPanel(
                ["Tests", Markup.Escape(shortTitle), "Builds"],
                null,
                () => _ui.RenderPanelLine("[yellow]No builds found.[/]"),
                "[blue]Esc[/] Back");
            Console.ReadKey(true);
            return;
        }

        var choices = builds.Select(b =>
            BrowserUI.FormatBuildChoice(b.BuildId, b.DefinitionName, b.Result,
                b.FinishTime, b.PrNumber)).ToList();

        var commands = new List<CommandBarItem>();
        var selected = _ui.SelectInPanel(
            ["Tests", Markup.Escape(shortTitle), "Builds"],
            $"[dim]{builds.Count} build(s) with this failure[/]",
            choices,
            commands);

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
            if (_filter.PrNumber is not null)
            {
                where.Add("b.pr_number = @pr");
                cmd.Parameters.AddWithValue("@pr", _filter.PrNumber.Value);
            }

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
        var currentValue = _filter.IsActive ? _filter.ToString() : null;
        var result = _ui.PromptInPanel(
            ["Tests", "Edit Filter"],
            "Enter filter expression (e.g. test:Serialization repo:roslyn def:*-CI)",
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
                new("Name", ConsoleKey.N, 1),
                new("Repository", ConsoleKey.R, 2),
                new("Definition", ConsoleKey.D, 3),
                new("Kind", ConsoleKey.K, 4),
                new("PR number", ConsoleKey.P, 5),
                new("Clear", ConsoleKey.C, 6),
            };

            _ui.RenderDetailPanel(
                ["Tests", "Filter"],
                null,
                () =>
                {
                    _ui.RenderPanelLine("Filter test failures by name, repository, definition, kind, etc.");
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
                },
                PanelRenderer.BuildCommandBarString(commands));

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.N:
                    _filter.TestNamePattern = _ui.PromptInPanel(["Tests", "Filter"], "Test name pattern (e.g. Serialization, *EditAndContinue*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.R:
                    _filter.RepoPattern = _ui.PromptInPanel(["Tests", "Filter"], "Repository pattern (e.g. roslyn, dotnet/*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.D:
                    _filter.DefinitionPattern = _ui.PromptInPanel(["Tests", "Filter"], "Definition pattern (e.g. ci, roslyn-CI*)");
                    SaveFilter();
                    continue;
                case ConsoleKey.K:
                    _filter.KindPattern = BrowserUI.PromptKindFilter();
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

    /// <summary>
    /// Prompts the user to enter a PR number. Returns null if cancelled or invalid.
    /// </summary>
    private int? PromptPrNumber()
    {
        var raw = _ui.PromptInPanel(["Tests", "Filter"], "PR number (e.g. 12345)");
        if (raw is null)
        {
            return null;
        }
        return int.TryParse(raw, out var pr) ? pr : null;
    }

    private void ShowFilterHelp()
    {
        _ui.RenderDetailPanel(
            ["Tests", "Filter Help"],
            null,
            () =>
            {
                _ui.RenderPanelLine("[bold]Quick filter (E):[/]");
                _ui.RenderPanelLine("  Type an expression like: [blue]test:Serialization repo:roslyn[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Matching (default: contains / LIKE):[/]");
                _ui.RenderPanelLine("  [dim]Serial - matches tests containing 'Serial'[/]");
                _ui.RenderPanelLine("  [dim]*EditAndContinue* - matches tests with 'EditAndContinue'[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Exact match (append !):[/]");
                _ui.RenderPanelLine("  [dim]dotnet/roslyn! - matches exactly 'dotnet/roslyn'[/]");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Filter prefixes:[/]");
                _ui.RenderPanelLine("  [blue]test:[/]  Test name");
                _ui.RenderPanelLine("  [blue]repo:[/]  Repository name");
                _ui.RenderPanelLine("  [blue]def:[/]   Definition/pipeline name");
                _ui.RenderPanelLine("  [blue]kind:[/]  Build kind (pr, ci)");
                _ui.RenderPanelLine("  [blue]pr:[/]    PR number");
                _ui.RenderEmptyLine();
                _ui.RenderPanelLine("[bold]Multiple filters combine with AND.[/]");
            },
            "[blue]Esc[/] Back");
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
        public int? PrNumber { get; set; }

        public bool IsActive => TestNamePattern is not null || RepoPattern is not null
            || DefinitionPattern is not null || KindPattern is not null || PrNumber is not null;

        public void Clear()
        {
            TestNamePattern = null;
            RepoPattern = null;
            DefinitionPattern = null;
            KindPattern = null;
            PrNumber = null;
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
                else if (part.StartsWith("pr:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(part[3..], out var pr))
                    {
                        PrNumber = pr;
                    }
                }            }
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (TestNamePattern is not null) parts.Add($"test:{TestNamePattern}");
            if (RepoPattern is not null) parts.Add($"repo:{RepoPattern}");
            if (DefinitionPattern is not null) parts.Add($"def:{DefinitionPattern}");
            if (KindPattern is not null) parts.Add($"kind:{KindPattern}");
            if (PrNumber is not null) parts.Add($"pr:{PrNumber}");
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


