using System.Text.Json.Serialization;

namespace Tiger;

/// <summary>
/// Summary information about a Helix job.
/// Maps to the JobSummary schema from the Helix REST API.
/// </summary>
public class HelixJob
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("build")]
    public required string Build { get; init; }

    [JsonPropertyName("queueId")]
    public string? QueueId { get; init; }

    [JsonPropertyName("creator")]
    public string? Creator { get; init; }

    [JsonPropertyName("created")]
    public string? Created { get; init; }

    [JsonPropertyName("finished")]
    public string? Finished { get; init; }

    [JsonPropertyName("initialWorkItemCount")]
    public int? InitialWorkItemCount { get; init; }

    [JsonPropertyName("detailsUrl")]
    public required string DetailsUrl { get; init; }

    [JsonPropertyName("waitUrl")]
    public required string WaitUrl { get; init; }

    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Summary of a work item within a Helix job.
/// Maps to the WorkItemSummary schema from the Helix REST API.
/// </summary>
public class HelixWorkItemSummary
{
    [JsonPropertyName("job")]
    public required string Job { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("consoleOutputUri")]
    public string? ConsoleOutputUri { get; init; }

    [JsonPropertyName("detailsUrl")]
    public required string DetailsUrl { get; init; }
}

/// <summary>
/// Detailed information about a single Helix work item.
/// Maps to the WorkItemDetails schema from the Helix REST API.
/// </summary>
public class HelixWorkItem
{
    [JsonPropertyName("job")]
    public required string Job { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("consoleOutputUri")]
    public string? ConsoleOutputUri { get; init; }

    [JsonPropertyName("queued")]
    public DateTime? Queued { get; init; }

    [JsonPropertyName("started")]
    public DateTime? Started { get; init; }

    [JsonPropertyName("finished")]
    public DateTime? Finished { get; init; }

    [JsonPropertyName("logs")]
    public List<HelixWorkItemLog>? Logs { get; init; }

    [JsonPropertyName("files")]
    public List<HelixWorkItemFile>? Files { get; init; }

    [JsonPropertyName("errors")]
    public List<HelixWorkItemError>? Errors { get; init; }

    [JsonPropertyName("warnings")]
    public List<HelixWorkItemError>? Warnings { get; init; }

    /// <summary>
    /// True when the console output URI points to the Helix dead-letter page,
    /// indicating an infrastructure failure rather than a real test failure.
    /// </summary>
    [JsonIgnore]
    public bool IsDeadLetter =>
        ConsoleOutputUri is not null &&
        ConsoleOutputUri.EndsWith("helix-workitem-deadletter.txt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A log uploaded from a Helix work item.
/// </summary>
public class HelixWorkItemLog
{
    [JsonPropertyName("module")]
    public required string Module { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

/// <summary>
/// An error or warning from a Helix work item.
/// </summary>
public class HelixWorkItemError
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("logUri")]
    public string? LogUri { get; init; }
}

/// <summary>
/// A file uploaded from a Helix work item.
/// Maps to the WorkItemFile schema from the Helix REST API.
/// </summary>
public partial class HelixWorkItemFile
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    /// <summary>
    /// Whether this file is the console log (matches pattern "console.[hex].log").
    /// </summary>
    [JsonIgnore]
    public bool IsConsoleLog => ConsoleLogRegex().IsMatch(FileName);

    [System.Text.RegularExpressions.GeneratedRegex(@"^console\.[a-z0-9]+\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ConsoleLogRegex();
}

/// <summary>
/// A file listed via the uploaded files endpoint.
/// Maps to the UploadedFile schema from the Helix REST API.
/// </summary>
public class HelixUploadedFile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("link")]
    public string? Link { get; init; }
}

/// <summary>
/// Console output for a Helix work item.
/// </summary>
public class HelixWorkItemConsole
{
    public required string Job { get; init; }
    public required string WorkItemName { get; init; }
    public required string Text { get; init; }
}
