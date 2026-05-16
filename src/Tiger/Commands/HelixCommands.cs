using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;

namespace Tiger.Commands;

public class HelixWorkItemsCommand : AsyncCommand<HelixWorkItemsCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--job")]
        [Description("The Helix job name (correlation ID)")]
        public required string JobName { get; set; }

        [CommandOption("--workitem")]
        [Description("Optional specific work item name")]
        public string? WorkItemName { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var helix = HelixClient.Create();

        if (settings.WorkItemName is not null)
        {
            var workItem = await helix.GetWorkItemAsync(settings.JobName, settings.WorkItemName);
            Console.WriteLine(JsonSerializer.Serialize(workItem, JsonOptions.Indented));
        }
        else
        {
            var workItems = await helix.GetWorkItemsAsync(settings.JobName);
            Console.WriteLine(JsonSerializer.Serialize(workItems, JsonOptions.Indented));
        }

        return 0;
    }
}

public class HelixConsoleCommand : AsyncCommand<HelixConsoleCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--job")]
        [Description("The Helix job name (correlation ID)")]
        public required string JobName { get; set; }

        [CommandOption("--workitem")]
        [Description("The work item name")]
        public required string WorkItemName { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var helix = HelixClient.Create();
        var console = await helix.GetConsoleAsync(settings.JobName, settings.WorkItemName);
        Console.WriteLine(JsonSerializer.Serialize(console, JsonOptions.Indented));
        return 0;
    }
}

public class HelixFilesCommand : AsyncCommand<HelixFilesCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--job")]
        [Description("The Helix job name (correlation ID)")]
        public required string JobName { get; set; }

        [CommandOption("--workitem")]
        [Description("The work item name")]
        public required string WorkItemName { get; set; }

        [CommandOption("--download")]
        [Description("Download files to a directory")]
        public bool Download { get; set; }

        [CommandOption("--output")]
        [Description("Output directory for downloaded files")]
        public string OutputDir { get; set; } = ".tiger/files";
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var helix = HelixClient.Create();

        if (settings.Download)
        {
            await helix.DownloadFilesAsync(settings.JobName, settings.WorkItemName, settings.OutputDir);
            Console.WriteLine($"Downloaded files to {settings.OutputDir}");
        }
        else
        {
            var files = await helix.GetFilesAsync(settings.JobName, settings.WorkItemName);
            Console.WriteLine(JsonSerializer.Serialize(files, JsonOptions.Indented));
        }

        return 0;
    }
}
