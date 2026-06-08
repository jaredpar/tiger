using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

public class AzdoBuildsCommand : AsyncCommand<AzdoBuildsCommand.Settings>
{
    public class Settings : AzdoSettings
    {
        [CommandOption("--definition")]
        [Description("Pipeline definition ID to filter by")]
        public int? DefinitionId { get; set; }

        [CommandOption("--top")]
        [Description("Maximum number of builds to return")]
        public int Top { get; set; } = 10;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var builds = await client.GetRecentBuildsAsync(settings.DefinitionId, settings.Top);
        Console.WriteLine(JsonSerializer.Serialize(builds, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoTestsCommand : AsyncCommand<AzdoBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AzdoBuildSettings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var failures = await client.GetTestFailuresAsync(settings.BuildId);
        Console.WriteLine(JsonSerializer.Serialize(failures, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoTestSummaryCommand : AsyncCommand<AzdoBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AzdoBuildSettings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var summaries = await client.GetTestSummaryByJobAsync(settings.BuildId);

        if (summaries.Count == 0)
        {
            Console.WriteLine("No test runs found.");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Run ID");
        table.AddColumn("Job Name");
        table.AddColumn(new TableColumn("Total").RightAligned());
        table.AddColumn(new TableColumn("Passed").RightAligned());
        table.AddColumn(new TableColumn("Failed").RightAligned());
        table.AddColumn(new TableColumn("Skipped").RightAligned());
        table.AddColumn(new TableColumn("Duration").RightAligned());

        foreach (var s in summaries)
        {
            var duration = s.Duration is not null
                ? s.Duration.Value.TotalSeconds < 60
                    ? $"{s.Duration.Value.TotalSeconds:F1}s"
                    : $"{(int)s.Duration.Value.TotalMinutes}m {s.Duration.Value.Seconds}s"
                : "-";

            table.AddRow(
                s.RunId.ToString(),
                s.JobName,
                s.TotalCount.ToString(),
                s.PassedCount.ToString(),
                s.FailedCount.ToString(),
                s.SkippedCount.ToString(),
                duration);
        }

        AnsiConsole.Write(table);

        var totalTests = summaries.Sum(s => s.TotalCount);
        var totalPassed = summaries.Sum(s => s.PassedCount);
        var totalFailed = summaries.Sum(s => s.FailedCount);
        Console.WriteLine($"\n{summaries.Count} run(s), {totalTests} total tests ({totalPassed} passed, {totalFailed} failed)");

        return 0;
    }
}

public class AzdoTimelineCommand : AsyncCommand<AzdoBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AzdoBuildSettings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var timeline = await client.GetTimelineAsync(settings.BuildId);
        Console.WriteLine(JsonSerializer.Serialize(timeline, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoArtifactsCommand : AsyncCommand<AzdoArtifactsCommand.Settings>
{
    public class Settings : AzdoBuildSettings
    {
        [CommandOption("--files")]
        [Description("List individual files within artifacts")]
        public bool ListFiles { get; set; }

        [CommandOption("--artifact")]
        [Description("Filter to a specific artifact name (substring match)")]
        public string? ArtifactFilter { get; set; }

        [CommandOption("--filter")]
        [Description("Filter files by path pattern (substring match, e.g. '.dmp')")]
        public string? FileFilter { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var artifacts = await client.GetArtifactsAsync(settings.BuildId);

        if (settings.ArtifactFilter is not null)
        {
            artifacts = artifacts
                .Where(a => a.Name.Contains(settings.ArtifactFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!settings.ListFiles)
        {
            Console.WriteLine(JsonSerializer.Serialize(artifacts, JsonOptions.Indented));
            return 0;
        }

        foreach (var artifact in artifacts)
        {
            List<ArtifactFileEntry> files;
            try
            {
                files = await client.GetArtifactFilesAsync(settings.BuildId, artifact);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Warning: could not list files in '{artifact.Name}': {ex.Message}");
                continue;
            }

            if (settings.FileFilter is not null)
            {
                files = files
                    .Where(f => f.Path.Contains(settings.FileFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (files.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"{artifact.Name} ({artifact.ResourceType}, {files.Count} file(s)):");
            foreach (var file in files)
            {
                Console.WriteLine($"  {file.Path} ({file.Size:N0} bytes)");
            }
            Console.WriteLine();
        }

        return 0;
    }
}

public class AzdoJobsCommand : AsyncCommand<AzdoJobsCommand.Settings>
{
    public class Settings : AzdoBuildSettings
    {
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var timeline = await client.GetTimelineAsync(settings.BuildId);
        var jobs = timeline.Records
            .Where(r => r.RecordType == "Job")
            .OrderBy(r => r.Order)
            .ToList();

        Console.WriteLine(JsonSerializer.Serialize(jobs, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoDownloadCommand : AsyncCommand<AzdoDownloadCommand.Settings>
{
    public class Settings : AzdoBuildSettings
    {
        [CommandOption("--artifact")]
        [Description("Artifact name to download")]
        public required string ArtifactName { get; set; }

        [CommandOption("--output")]
        [Description("Output file path")]
        public required string OutputPath { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        await client.DownloadArtifactAsync(settings.BuildId, settings.ArtifactName, settings.OutputPath);
        Console.WriteLine($"Artifact '{settings.ArtifactName}' downloaded to {settings.OutputPath}");
        return 0;
    }
}

public class AzdoDownloadDumpsCommand : AsyncCommand<AzdoDownloadDumpsCommand.Settings>
{
    public class Settings : AzdoBuildSettings
    {
        [CommandOption("--run-id")]
        [Description("Optional test run ID to filter which artifacts to search")]
        public int? RunId { get; set; }

        [CommandOption("--output")]
        [Description("Output directory (default: current directory)")]
        public string OutputDirectory { get; set; } = ".";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var artifacts = await client.GetArtifactsAsync(settings.BuildId);

        // If a run ID is specified, find the matching test run name to filter artifacts
        string? runNameFilter = null;
        if (settings.RunId is not null)
        {
            var dbPath = Path.Combine(TigerUtils.GetConfigDirectory(), "tiger.db");
            using var db = TigerDatabase.Open(dbPath);
            runNameFilter = db.WithCommand(cmd =>
            {
                cmd.CommandText = "SELECT run_name FROM test_runs WHERE run_id = @runId";
                cmd.Parameters.AddWithValue("@runId", settings.RunId.Value);
                return cmd.ExecuteScalar() as string;
            });

            if (runNameFilter is null)
            {
                Console.Error.WriteLine($"Test run {settings.RunId} not found in the database.");
                return 1;
            }

            Console.WriteLine($"Filtering to test run: {runNameFilter}");
        }

        var downloaded = 0;
        foreach (var artifact in artifacts)
        {
            // If filtering by run name, only check artifacts that match
            if (runNameFilter is not null)
            {
                var normalizedRunName = runNameFilter.Replace(' ', '_');
                if (!artifact.Name.StartsWith(normalizedRunName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            List<ArtifactFileEntry> files;
            try
            {
                files = await client.GetArtifactFilesAsync(settings.BuildId, artifact);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Warning: could not list files in '{artifact.Name}': {ex.Message}");
                continue;
            }

            var dumpFiles = files.Where(f => f.Path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)).ToList();
            if (dumpFiles.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"Found {dumpFiles.Count} dump(s) in artifact '{artifact.Name}':");
            foreach (var dumpFile in dumpFiles)
            {
                var fileName = Path.GetFileName(dumpFile.Path);
                var outputPath = Path.Combine(settings.OutputDirectory, fileName);
                Console.WriteLine($"  Downloading {fileName} ({dumpFile.Size:N0} bytes)...");

                try
                {
                    await client.DownloadArtifactFileAsync(settings.BuildId, artifact, dumpFile, outputPath);
                    Console.WriteLine($"  Saved to {outputPath}");
                    downloaded++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Error downloading {fileName}: {ex.Message}");
                }
            }
        }

        if (downloaded == 0)
        {
            Console.WriteLine("No dump files found in build artifacts.");
        }
        else
        {
            Console.WriteLine($"Downloaded {downloaded} dump file(s).");
        }

        return 0;
    }
}

public class AzdoPrBuildsCommand : AsyncCommand<AzdoPrBuildsCommand.Settings>
{
    public class Settings : AzdoSettings
    {
        [CommandOption("--repo")]
        [Description("GitHub repository in owner/repo format")]
        public required string Repository { get; set; }

        [CommandOption("--pr")]
        [Description("Pull request number")]
        public int PrNumber { get; set; }

        [CommandOption("--top")]
        [Description("Maximum number of builds to return")]
        public int Top { get; set; } = 10;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var builds = await client.GetBuildsForPullRequestAsync(settings.Repository, settings.PrNumber, settings.Top);
        Console.WriteLine(JsonSerializer.Serialize(builds, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoRepoBuildsCommand : AsyncCommand<AzdoRepoBuildsCommand.Settings>
{
    public class Settings : AzdoSettings
    {
        [CommandOption("--repo")]
        [Description("GitHub repository in owner/repo format")]
        public required string Repository { get; set; }

        [CommandOption("--pr")]
        [Description("Filter to PR builds only")]
        public bool PrOnly { get; set; }

        [CommandOption("--ci")]
        [Description("Filter to CI builds only")]
        public bool CiOnly { get; set; }

        [CommandOption("--top")]
        [Description("Maximum number of builds to return")]
        public int Top { get; set; } = 10;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        string? reasonFilter = (settings.PrOnly, settings.CiOnly) switch
        {
            (true, false) => "pullRequest",
            (false, true) => "individualCI,batchedCI",
            _ => null,
        };

        var client = settings.CreateClient();
        var builds = await client.GetBuildsForRepositoryAsync(settings.Repository, settings.Top, reasonFilter);
        Console.WriteLine(JsonSerializer.Serialize(builds, JsonOptions.Indented));
        return 0;
    }
}
