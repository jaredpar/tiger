using System.Text.Json;
using Pipeline.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var subcommand = args[0];
var subArgs = args[1..];

return subcommand switch
{
    "helix" => await RunHelixAsync(subArgs),
    "azdo" => await RunAzdoAsync(subArgs),
    _ => PrintUsage(),
};

static async Task<int> RunHelixAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintHelixUsage();
        return 1;
    }

    var action = args[0];
    var actionArgs = args[1..];

    return action switch
    {
        "workitems" => await RunHelixWorkItemsAsync(actionArgs),
        "console" => await RunHelixConsoleAsync(actionArgs),
        "files" => await RunHelixFilesAsync(actionArgs),
        _ => PrintHelixUsage(),
    };
}

static async Task<int> RunHelixWorkItemsAsync(string[] args)
{
    var jobName = GetOption(args, "--job");
    if (jobName is null)
    {
        PrintHelixUsage();
        return 1;
    }

    var helix = HelixClient.Create();

    var workItemName = GetOption(args, "--workitem");
    if (workItemName is not null)
    {
        var workItem = await helix.GetWorkItemAsync(jobName, workItemName);
        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(workItem, options));
    }
    else
    {
        var workItems = await helix.GetWorkItemsAsync(jobName);
        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(workItems, options));
    }

    return 0;
}

static async Task<int> RunHelixConsoleAsync(string[] args)
{
    var jobName = GetOption(args, "--job");
    var workItemName = GetOption(args, "--workitem");

    if (jobName is null || workItemName is null)
    {
        PrintHelixUsage();
        return 1;
    }

    var helix = HelixClient.Create();
    var console = await helix.GetConsoleAsync(jobName, workItemName);
    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(console, options));
    return 0;
}

static async Task<int> RunHelixFilesAsync(string[] args)
{
    var jobName = GetOption(args, "--job");
    var workItemName = GetOption(args, "--workitem");
    var download = HasFlag(args, "--download");
    var downloadDir = GetOption(args, "--download") ?? ".pipeline-triage/files";

    if (jobName is null || workItemName is null)
    {
        PrintHelixUsage();
        return 1;
    }

    var helix = HelixClient.Create();

    if (download)
    {
        await helix.DownloadFilesAsync(jobName, workItemName, downloadDir);
        Console.WriteLine($"Downloaded files to {downloadDir}");
    }
    else
    {
        var files = await helix.GetFilesAsync(jobName, workItemName);
        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(files, options));
    }

    return 0;
}

static async Task<int> RunAzdoAsync(string[] args)
{
    if (args.Length == 0)
    {
        PrintAzdoUsage();
        return 1;
    }

    var action = args[0];
    var actionArgs = args[1..];

    return action switch
    {
        "builds" => await RunAzdoBuildsAsync(actionArgs),
        "tests" => await RunAzdoTestsAsync(actionArgs),
        "test-summary" => await RunAzdoTestSummaryAsync(actionArgs),
        "timeline" => await RunAzdoTimelineAsync(actionArgs),
        "artifacts" => await RunAzdoArtifactsAsync(actionArgs),
        "download" => await RunAzdoDownloadAsync(actionArgs),
        "jobs" => await RunAzdoJobsAsync(actionArgs),
        "pr-builds" => await RunAzdoPrBuildsAsync(actionArgs),
        "repo-builds" => await RunAzdoRepoBuildsAsync(actionArgs),
        _ => PrintAzdoUsage(),
    };
}

static async Task<int> RunAzdoBuildsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var definitionValue = GetOption(args, "--definition");
    var topValue = GetOption(args, "--top");

    int? definitionId = null;
    if (definitionValue is not null)
    {
        if (!int.TryParse(definitionValue, out var id))
        {
            Console.Error.WriteLine($"Error: --definition must be an integer, got '{definitionValue}'");
            return 1;
        }
        definitionId = id;
    }

    var top = 10;
    if (topValue is not null)
    {
        if (!int.TryParse(topValue, out top))
        {
            Console.Error.WriteLine($"Error: --top must be an integer, got '{topValue}'");
            return 1;
        }
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var builds = await client.GetRecentBuildsAsync(definitionId, top);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(builds, options));
    return 0;
}

static async Task<int> RunAzdoTestsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");

    if (buildValue is null)
    {
        Console.Error.WriteLine("Error: --build is required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var failures = await client.GetTestFailuresAsync(buildId);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(failures, options));
    return 0;
}

static string? GetOption(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  pipeline helix ...");
    Console.Error.WriteLine("  pipeline azdo ...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Run 'pipeline <subcommand>' for more details.");
    return 1;
}

static int PrintHelixUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  pipeline helix workitems --job <jobName> [--workitem <name>]");
    Console.Error.WriteLine("  pipeline helix console --job <jobName> --workitem <name>");
    Console.Error.WriteLine("  pipeline helix files --job <jobName> --workitem <name> [--download [dir]]");
    return 1;
}

static int PrintAzdoUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  pipeline azdo builds [--definition <id>] [--top <n>] [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo tests --build <id> [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo test-summary --build <id> [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo timeline --build <id> [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo artifacts --build <id> [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo jobs --build <id> [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo pr-builds --repo <owner/repo> --pr <number> [--top <n>] [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo repo-builds --repo <owner/repo> [--pr] [--ci] [--top <n>] [--org <org>] [--project <project>]");
    Console.Error.WriteLine("  pipeline azdo download --build <id> --artifact <name> --output <path> [--org <org>] [--project <project>]");
    return 1;
}

static async Task<int> RunAzdoTestSummaryAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");

    if (buildValue is null)
    {
        Console.Error.WriteLine("Error: --build is required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var summaries = await client.GetTestSummaryByJobAsync(buildId);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(summaries, options));
    return 0;
}

static async Task<int> RunAzdoTimelineAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");

    if (buildValue is null)
    {
        Console.Error.WriteLine("Error: --build is required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var timeline = await client.GetTimelineAsync(buildId);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(timeline, options));
    return 0;
}

static async Task<int> RunAzdoArtifactsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");

    if (buildValue is null)
    {
        Console.Error.WriteLine("Error: --build is required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var artifacts = await client.GetArtifactsAsync(buildId);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(artifacts, options));
    return 0;
}

static async Task<int> RunAzdoDownloadAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");
    var artifactName = GetOption(args, "--artifact");
    var outputPath = GetOption(args, "--output");

    if (buildValue is null || artifactName is null || outputPath is null)
    {
        Console.Error.WriteLine("Error: --build, --artifact, and --output are required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    await client.DownloadArtifactAsync(buildId, artifactName, outputPath);

    Console.WriteLine($"Artifact '{artifactName}' downloaded to {outputPath}");
    return 0;
}

static async Task<int> RunAzdoJobsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var buildValue = GetOption(args, "--build");

    if (buildValue is null)
    {
        Console.Error.WriteLine("Error: --build is required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(buildValue, out var buildId))
    {
        Console.Error.WriteLine($"Error: --build must be an integer, got '{buildValue}'");
        return 1;
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var timeline = await client.GetTimelineAsync(buildId);
    var jobs = timeline.Records
        .Where(r => r.RecordType == "Job")
        .OrderBy(r => r.Order)
        .ToList();

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(jobs, options));
    return 0;
}

static async Task<int> RunAzdoPrBuildsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var repo = GetOption(args, "--repo");
    var prValue = GetOption(args, "--pr");
    var topValue = GetOption(args, "--top");

    if (repo is null || prValue is null)
    {
        Console.Error.WriteLine("Error: --repo and --pr are required");
        PrintAzdoUsage();
        return 1;
    }

    if (!int.TryParse(prValue, out var prNumber))
    {
        Console.Error.WriteLine($"Error: --pr must be an integer, got '{prValue}'");
        return 1;
    }

    var top = 10;
    if (topValue is not null)
    {
        if (!int.TryParse(topValue, out top))
        {
            Console.Error.WriteLine($"Error: --top must be an integer, got '{topValue}'");
            return 1;
        }
    }

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var builds = await client.GetBuildsForPullRequestAsync(repo, prNumber, top);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(builds, options));
    return 0;
}

static bool HasFlag(string[] args, string name) =>
    args.Any(a => a == name);

static async Task<int> RunAzdoRepoBuildsAsync(string[] args)
{
    var org = GetOption(args, "--org") ?? AzdoClient.DefaultOrganization;
    var project = GetOption(args, "--project") ?? AzdoClient.DefaultProject;
    var repo = GetOption(args, "--repo");
    var topValue = GetOption(args, "--top");
    var filterPr = HasFlag(args, "--pr");
    var filterCi = HasFlag(args, "--ci");

    if (repo is null)
    {
        Console.Error.WriteLine("Error: --repo is required");
        PrintAzdoUsage();
        return 1;
    }

    var top = 10;
    if (topValue is not null)
    {
        if (!int.TryParse(topValue, out top))
        {
            Console.Error.WriteLine($"Error: --top must be an integer, got '{topValue}'");
            return 1;
        }
    }

    string? reasonFilter = (filterPr, filterCi) switch
    {
        (true, false) => "pullRequest",
        (false, true) => "individualCI,batchedCI",
        _ => null,
    };

    var credential = PipelineUtils.CreateCredential();
    var client = await AzdoClient.CreateAsync(credential, org, project);
    var builds = await client.GetBuildsForRepositoryAsync(repo, top, reasonFilter);

    var options = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(builds, options));
    return 0;
}
