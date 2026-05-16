using System.ComponentModel;
using System.Text.Json;
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
        Console.WriteLine(JsonSerializer.Serialize(summaries, JsonOptions.Indented));
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

public class AzdoArtifactsCommand : AsyncCommand<AzdoBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AzdoBuildSettings settings, CancellationToken ct)
    {
        var client = settings.CreateClient();
        var artifacts = await client.GetArtifactsAsync(settings.BuildId);
        Console.WriteLine(JsonSerializer.Serialize(artifacts, JsonOptions.Indented));
        return 0;
    }
}

public class AzdoJobsCommand : AsyncCommand<AzdoBuildSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, AzdoBuildSettings settings, CancellationToken ct)
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
