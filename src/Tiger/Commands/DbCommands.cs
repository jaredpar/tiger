using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

public class DbDeleteBuildCommand : Command<DbDeleteBuildCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<organization>")]
        [Description("AzDO organization (e.g. dnceng-public)")]
        public required string Organization { get; set; }

        [CommandArgument(1, "<build-id>")]
        [Description("Build ID to delete. Use 'all' to delete all builds, or a comma-separated list.")]
        public required string BuildId { get; set; }

        [CommandOption("--older-than")]
        [Description("Delete builds older than this many days")]
        public int? OlderThanDays { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        var configDir = TigerUtils.GetConfigDirectory();
        var dbPath = Path.Combine(configDir, "tiger.db");
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLine("[red]Database not found at {0}[/]", Markup.Escape(dbPath));
            return 1;
        }

        using var db = TigerDatabase.Open(dbPath);

        if (settings.OlderThanDays.HasValue)
        {
            return DeleteOlderThan(db, settings.Organization, settings.OlderThanDays.Value);
        }

        if (settings.BuildId.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return DeleteAll(db, settings.Organization);
        }

        // Parse comma-separated build IDs
        var buildIds = settings.BuildId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var count = 0;
        foreach (var idStr in buildIds)
        {
            if (!int.TryParse(idStr, out var buildId))
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping invalid build ID: {Markup.Escape(idStr)}[/]");
                continue;
            }
            db.DeleteBuild(settings.Organization, buildId);
            count++;
        }

        AnsiConsole.MarkupLine($"[green]Deleted {count} build(s) and all associated data.[/]");
        return 0;
    }

    private static int DeleteOlderThan(TigerDatabase db, string organization, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT build_id FROM builds
            WHERE organization = @org AND finish_time < @cutoff
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var buildIds = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            buildIds.Add(reader.GetInt32(0));
        }
        reader.Close();

        foreach (var buildId in buildIds)
        {
            db.DeleteBuild(organization, buildId);
        }

        AnsiConsole.MarkupLine($"[green]Deleted {buildIds.Count} build(s) older than {days} days.[/]");
        return 0;
    }

    private static int DeleteAll(TigerDatabase db, string organization)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT build_id FROM builds
            WHERE organization = @org
            """;
        cmd.Parameters.AddWithValue("@org", organization);

        var buildIds = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            buildIds.Add(reader.GetInt32(0));
        }
        reader.Close();

        foreach (var buildId in buildIds)
        {
            db.DeleteBuild(organization, buildId);
        }

        AnsiConsole.MarkupLine($"[green]Deleted all {buildIds.Count} build(s) for {Markup.Escape(organization)}.[/]");
        return 0;
    }
}
