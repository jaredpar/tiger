using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

/// <summary>
/// Starts the build poller as a long-running foreground process.
/// Polls configured AzDO sources and ingests completed builds into SQLite.
/// </summary>
public sealed class PollStartCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();
        var config = tigerContext.Config;

        if (config.Sources.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No AzDO sources configured. Run 'tiger config init' first.[/]");
            return 1;
        }

        var ingestion = new BuildIngestionService(db);
        var poller = new BuildPoller(config, db, (org, proj) =>
            AzdoClient.Create(tigerContext.AzureCredential, org, proj));

        poller.OnNewBuilds = ingestion.IngestBuildsAsync;

        AnsiConsole.MarkupLine("[green]Starting poller...[/]");
        foreach (var source in config.Sources)
        {
            AnsiConsole.MarkupLine($"  Monitoring [blue]{source.Organization}/{source.Project}[/]");
        }
        AnsiConsole.MarkupLine($"  Poll interval: [blue]{config.PollIntervalSeconds}s[/]");
        AnsiConsole.MarkupLine($"  Database: [blue]{tigerContext.DatabasePath}[/]");
        AnsiConsole.MarkupLine("  Press Ctrl+C to stop.");
        AnsiConsole.WriteLine();

        poller.Start();

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }

        AnsiConsole.MarkupLine("[yellow]Stopping poller...[/]");
        await poller.StopAsync();
        AnsiConsole.MarkupLine("[green]Poller stopped.[/]");

        return 0;
    }
}

/// <summary>
/// Shows status of the poller: watermarks, build counts, and recent activity.
/// </summary>
public sealed class StatusCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken ct)
    {
        var tigerContext = TigerUtils.CreateContext();
        var db = tigerContext.GetDatabase();

        // Show watermarks
        var table = new Table()
            .AddColumn("Organization")
            .AddColumn("Project")
            .AddColumn("Last Build ID")
            .AddColumn("Last Poll Time")
            .AddColumn("Total Builds")
            .AddColumn("Failed Tests");

        var hasRows = false;
        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT
                    w.organization,
                    w.project,
                    w.last_build_id,
                    w.last_poll_time,
                    (SELECT COUNT(*) FROM builds b WHERE b.organization = w.organization),
                    (SELECT COUNT(*) FROM test_results tr
                     JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                     WHERE r.organization = w.organization AND tr.outcome = 'Failed')
                FROM poll_watermarks w
                ORDER BY w.organization, w.project
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hasRows = true;
                table.AddRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2).ToString(),
                    reader.IsDBNull(3) ? "-" : reader.GetString(3),
                    reader.GetInt64(4).ToString(),
                    reader.GetInt64(5).ToString());
            }
        });

        if (!hasRows)
        {
            AnsiConsole.MarkupLine("[yellow]No polling data yet. Run 'tiger poll start' to begin polling.[/]");
            return 0;
        }

        AnsiConsole.Write(table);

        // Show recent builds
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Recent builds:[/]");

        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT organization, project, build_id, build_number, definition_name, result, finish_time
                FROM builds
                ORDER BY build_id DESC
                LIMIT 10
                """;

            var recentTable = new Table()
                .AddColumn("Org/Project")
                .AddColumn("Build")
                .AddColumn("Definition")
                .AddColumn("Result")
                .AddColumn("Finished");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var result = reader.IsDBNull(5) ? "-" : reader.GetString(5);
                var resultMarkup = result switch
                {
                    "succeeded" => "[green]succeeded[/]",
                    "failed" => "[red]failed[/]",
                    "partiallySucceeded" => "[yellow]partial[/]",
                    _ => result,
                };

                recentTable.AddRow(
                    $"{reader.GetString(0)}/{reader.GetString(1)}",
                    $"{reader.GetString(3)} (#{reader.GetInt32(2)})",
                    reader.GetString(4),
                    resultMarkup,
                    reader.IsDBNull(6) ? "-" : reader.GetString(6));
            }

            AnsiConsole.Write(recentTable);
        });

        await Task.CompletedTask;
        return 0;
    }
}
