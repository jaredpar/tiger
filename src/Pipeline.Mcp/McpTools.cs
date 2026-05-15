using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Pipeline.Core;

namespace Pipeline.Mcp;

[McpServerToolType]
public sealed class McpTools
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    // Configuration

    [McpServerTool(Name = "pl_config"), Description("Report pipeline configuration status including config directory path, Helix token status, and links to obtain credentials.")]
    public static string GetConfigStatus(PipelineContext context)
    {
        var helixTokenPath = Path.Combine(context.ConfigDirectory, "helix.txt");
        var hasHelixToken = context.HelixToken is not null;

        return JsonSerializer.Serialize(new
        {
            configDirectory = context.ConfigDirectory,
            helixTokenPath,
            hasHelixToken,
            helixTokenUrl = "https://helix.dot.net/Account/Tokens",
        }, s_jsonOptions);
    }

    // AzDO tools

    [McpServerTool(Name = "pl_azdo_builds_for_repo"), Description("Get AzDO builds for a GitHub repository. Returns both PR and CI builds by default.")]
    public static async Task<string> GetBuildsForRepository(
        AzdoClient azdoClient,
        [Description("The GitHub repository in owner/repo format (e.g. dotnet/roslyn)")] string repository,
        [Description("Maximum number of builds to return (default 10)")] int top = 10,
        [Description("Filter builds: 'pr' for pull request builds only, 'ci' for post-merge builds only, 'all' for both (default)")] string filter = "all")
    {
        string? reasonFilter = filter.ToLowerInvariant() switch
        {
            "pr" => "pullRequest",
            "ci" => "individualCI,batchedCI",
            _ => null,
        };

        var builds = await azdoClient.GetBuildsForRepositoryAsync(repository, top, reasonFilter);
        return JsonSerializer.Serialize(builds, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_recent_builds"), Description("Get recent AzDO builds, optionally filtered by pipeline definition ID.")]
    public static async Task<string> GetRecentBuilds(
        AzdoClient azdoClient,
        [Description("Optional pipeline definition ID to filter by")] int? definitionId = null,
        [Description("Maximum number of builds to return (default 10)")] int top = 10)
    {
        var builds = await azdoClient.GetRecentBuildsAsync(definitionId, top);
        return JsonSerializer.Serialize(builds, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_pr_builds"), Description("Get AzDO builds for a specific pull request.")]
    public static async Task<string> GetBuildsForPullRequest(
        AzdoClient azdoClient,
        [Description("The GitHub repository in owner/repo format (e.g. dotnet/roslyn)")] string repository,
        [Description("The pull request number")] int prNumber,
        [Description("Maximum number of builds to return (default 10)")] int top = 10)
    {
        var builds = await azdoClient.GetBuildsForPullRequestAsync(repository, prNumber, top);
        return JsonSerializer.Serialize(builds, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_test_failures"), Description("Get test failures for an AzDO build.")]
    public static async Task<string> GetTestFailures(
        AzdoClient azdoClient,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId)
    {
        var failures = int.TryParse(buildId, out var id)
            ? await azdoClient.GetTestFailuresAsync(id)
            : await azdoClient.GetTestFailuresAsync(buildId);
        return JsonSerializer.Serialize(failures, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_test_summary"), Description("Get test counts for each job (test run) in an AzDO build. Returns an array where each entry has job name, total test count, passed count, failed count, and skipped count.")]
    public static async Task<string> GetTestSummary(
        AzdoClient azdoClient,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId)
    {
        var summaries = int.TryParse(buildId, out var id)
            ? await azdoClient.GetTestSummaryByJobAsync(id)
            : await azdoClient.GetTestSummaryByJobAsync(buildId);
        return JsonSerializer.Serialize(summaries, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_timeline"), Description("Get the timeline (all records) for an AzDO build.")]
    public static async Task<string> GetTimeline(
        AzdoClient azdoClient,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId)
    {
        var timeline = int.TryParse(buildId, out var id)
            ? await azdoClient.GetTimelineAsync(id)
            : await azdoClient.GetTimelineAsync(buildId);
        return JsonSerializer.Serialize(timeline, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_artifacts"), Description("Get build artifacts for an AzDO build.")]
    public static async Task<string> GetArtifacts(
        AzdoClient azdoClient,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId)
    {
        var artifacts = int.TryParse(buildId, out var id)
            ? await azdoClient.GetArtifactsAsync(id)
            : await azdoClient.GetArtifactsAsync(buildId);
        return JsonSerializer.Serialize(artifacts, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_azdo_jobs"), Description("Get job records from an AzDO build timeline.")]
    public static async Task<string> GetJobs(
        AzdoClient azdoClient,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId)
    {
        var timeline = int.TryParse(buildId, out var id)
            ? await azdoClient.GetTimelineAsync(id)
            : await azdoClient.GetTimelineAsync(buildId);
        var jobs = timeline.Records
            .Where(r => r.RecordType == "Job")
            .OrderBy(r => r.Order)
            .ToList();
        return JsonSerializer.Serialize(jobs, s_jsonOptions);
    }

    // Helix tools

    [McpServerTool(Name = "pl_helix_work_items"), Description("List all work items for a Helix job by job name.")]
    public static async Task<string> GetHelixWorkItems(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName)
    {
        var items = await helix.GetWorkItemsAsync(jobName);
        return JsonSerializer.Serialize(items, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_helix_work_item_details"), Description("Get detailed information about a specific Helix work item including logs, files, errors, exit code, and machine name.")]
    public static async Task<string> GetHelixWorkItemDetails(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var workItem = await helix.GetWorkItemAsync(jobName, workItemName);
        return JsonSerializer.Serialize(workItem, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_helix_console"), Description("Get console output for a specific Helix work item.")]
    public static async Task<string> GetHelixConsole(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var console = await helix.GetConsoleAsync(jobName, workItemName);
        return JsonSerializer.Serialize(console, s_jsonOptions);
    }

    [McpServerTool(Name = "pl_helix_files"), Description("List files uploaded from a specific Helix work item.")]
    public static async Task<string> GetHelixFiles(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var files = await helix.GetFilesAsync(jobName, workItemName);
        return JsonSerializer.Serialize(files, s_jsonOptions);
    }
}
