using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// The default interactive command center. Starts background services (poller, etc.)
/// and presents a menu of available commands.
/// </summary>
public sealed class DashboardCommand : AsyncCommand
{
    private const string MenuStatus = "Status";
    private const string MenuBuilds = "Recent builds";
    private const string MenuFailures = "Test failures";
    private const string MenuConfig = "Configuration";
    private const string MenuQuit = "Quit";

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();
        var config = tigerContext.Config;

        // Start background poller
        BuildPoller? poller = null;
        if (config.Sources.Count > 0)
        {
            var ingestion = new BuildIngestionService(db);
            poller = new BuildPoller(config, db, (org, proj) =>
                AzdoClient.Create(tigerContext.AzureCredential, org, proj));
            poller.OnNewBuilds = ingestion.IngestBuildsAsync;
            poller.Start();
        }

        try
        {
            RenderBanner(config, tigerContext, poller);
            await RunMenuLoopAsync(tigerContext, db, poller, ct);
        }
        finally
        {
            if (poller is not null)
            {
                AnsiConsole.MarkupLine("[yellow]Stopping services...[/]");
                await poller.StopAsync();
            }
        }

        return 0;
    }

    private static void RenderBanner(TigerConfig config, TigerContext tigerContext, BuildPoller? poller)
    {
        AnsiConsole.Write(new FigletText("tiger").Color(Color.Orange1));
        AnsiConsole.MarkupLine("[dim]CI/CD Infrastructure Management[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Service").AddColumn("Status");
        table.AddRow("Poller", poller?.IsRunning == true ? "[green]Running[/]" : "[red]Stopped[/]");
        table.AddRow("Database", $"[blue]{tigerContext.DatabasePath}[/]");
        table.AddRow("Sources", $"[blue]{config.Sources.Count}[/]");

        if (config.Sources.Count > 0)
        {
            table.AddRow("Poll interval", $"[blue]{config.PollIntervalSeconds}s[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static async Task RunMenuLoopAsync(TigerContext tigerContext, TigerDatabase db, BuildPoller? poller, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .AddChoices(MenuStatus, MenuBuilds, MenuFailures, MenuConfig, MenuQuit));

            switch (choice)
            {
                case MenuStatus:
                    ShowStatus(db, poller);
                    break;
                case MenuBuilds:
                    ShowRecentBuilds(db);
                    break;
                case MenuFailures:
                    ShowTestFailures(db);
                    break;
                case MenuConfig:
                    ShowConfig(tigerContext);
                    break;
                case MenuQuit:
                    return;
            }

            AnsiConsole.WriteLine();
        }
    }

    private static void ShowStatus(TigerDatabase db, BuildPoller? poller)
    {
        AnsiConsole.MarkupLine($"[bold]Poller:[/] {(poller?.IsRunning == true ? "[green]Running[/]" : "[red]Stopped[/]")}");
        AnsiConsole.WriteLine();

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                w.organization,
                w.project,
                w.last_build_id,
                w.last_poll_time,
                (SELECT COUNT(*) FROM builds b WHERE b.organization = w.organization AND b.project = w.project),
                (SELECT COUNT(*) FROM test_results tr
                 JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
                 WHERE r.organization = w.organization AND r.project = w.project AND tr.outcome = 'Failed')
            FROM poll_watermarks w
            ORDER BY w.organization, w.project
            """;

        using var reader = cmd.ExecuteReader();
        var hasRows = false;
        var table = new Table()
            .AddColumn("Org/Project")
            .AddColumn("Last Build")
            .AddColumn("Last Poll")
            .AddColumn("Builds")
            .AddColumn("Failed Tests");

        while (reader.Read())
        {
            hasRows = true;
            table.AddRow(
                $"{reader.GetString(0)}/{reader.GetString(1)}",
                reader.GetInt32(2).ToString(),
                reader.IsDBNull(3) ? "-" : reader.GetString(3),
                reader.GetInt64(4).ToString(),
                reader.GetInt64(5).ToString());
        }

        if (!hasRows)
        {
            AnsiConsole.MarkupLine("[yellow]No polling data yet. The poller will populate this on the next cycle.[/]");
        }
        else
        {
            AnsiConsole.Write(table);
        }
    }

    private static void ShowRecentBuilds(TigerDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT organization, project, build_id, build_number, definition_name, result, finish_time
            FROM builds
            ORDER BY build_id DESC
            LIMIT 15
            """;

        var table = new Table()
            .AddColumn("Org/Project")
            .AddColumn("Build")
            .AddColumn("Definition")
            .AddColumn("Result")
            .AddColumn("Finished");

        using var reader = cmd.ExecuteReader();
        var hasRows = false;
        while (reader.Read())
        {
            hasRows = true;
            var result = reader.IsDBNull(5) ? "-" : reader.GetString(5);
            var resultMarkup = result switch
            {
                "succeeded" => "[green]succeeded[/]",
                "failed" => "[red]failed[/]",
                "partiallySucceeded" => "[yellow]partial[/]",
                _ => result,
            };

            table.AddRow(
                $"{reader.GetString(0)}/{reader.GetString(1)}",
                $"{reader.GetString(3)} (#{reader.GetInt32(2)})",
                reader.GetString(4),
                resultMarkup,
                reader.IsDBNull(6) ? "-" : reader.GetString(6));
        }

        if (!hasRows)
        {
            AnsiConsole.MarkupLine("[yellow]No builds ingested yet.[/]");
        }
        else
        {
            AnsiConsole.Write(table);
        }
    }

    private static void ShowTestFailures(TigerDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.test_case_title, COUNT(*) as fail_count,
                   GROUP_CONCAT(DISTINCT r.organization || '/' || r.project) as sources
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE tr.outcome = 'Failed'
            GROUP BY tr.test_case_title
            ORDER BY fail_count DESC
            LIMIT 20
            """;

        var table = new Table()
            .AddColumn("Test")
            .AddColumn("Failures")
            .AddColumn("Sources");

        using var reader = cmd.ExecuteReader();
        var hasRows = false;
        while (reader.Read())
        {
            hasRows = true;
            var title = reader.GetString(0);
            if (title.Length > 80)
                title = title[..77] + "...";
            table.AddRow(title, reader.GetInt64(1).ToString(), reader.GetString(2));
        }

        if (!hasRows)
        {
            AnsiConsole.MarkupLine("[yellow]No test failures recorded yet.[/]");
        }
        else
        {
            AnsiConsole.Write(table);
        }
    }

    private static void ShowConfig(TigerContext tigerContext)
    {
        var config = tigerContext.Config;
        AnsiConsole.MarkupLine($"[bold]Config path:[/] {TigerConfig.GetConfigPath(tigerContext.ConfigDirectory)}");
        AnsiConsole.MarkupLine($"[bold]Poll interval:[/] {config.PollIntervalSeconds}s");
        AnsiConsole.MarkupLine($"[bold]Sources:[/]");

        foreach (var source in config.Sources)
        {
            AnsiConsole.MarkupLine($"  [green]{source.Organization}[/] / [green]{source.Project}[/]");
            if (source.Repositories.Count > 0)
            {
                foreach (var repo in source.Repositories)
                    AnsiConsole.MarkupLine($"    - {repo}");
            }
            else
            {
                AnsiConsole.MarkupLine("    [dim](all repositories)[/]");
            }
        }
    }
}
