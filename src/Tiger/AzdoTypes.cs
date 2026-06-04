using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiger;

public class AzdoBuild
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("buildNumber")]
    public required string BuildNumber { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("sourceBranch")]
    public required string SourceBranch { get; init; }

    [JsonPropertyName("definitionName")]
    public required string DefinitionName { get; init; }

    [JsonPropertyName("definitionId")]
    public int DefinitionId { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; init; }

    [JsonPropertyName("prNumber")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTime? FinishTime { get; init; }
}

public class AzdoTimelineIssue
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? Category { get; init; }
}

public class AzdoTimelineRecord
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; init; }
    public required string RecordType { get; init; }
    public int Order { get; init; }
    public string? State { get; init; }
    public string? Result { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? FinishTime { get; init; }
    public List<AzdoTimelineIssue> Issues { get; init; } = [];
    public string? WorkerName { get; init; }
    public string? LogUrl { get; init; }
}

public class AzdoTimeline
{
    public required List<AzdoTimelineRecord> Records { get; init; }

    /// <summary>All issues (errors and warnings) across all records.</summary>
    public List<AzdoTimelineIssue> GetIssues() =>
        Records.SelectMany(r => r.Issues).ToList();

    /// <summary>Names of all Job records.</summary>
    public List<string> GetJobNames() =>
        Records.Where(r => r.RecordType == "Job").Select(r => r.Name).ToList();

    /// <summary>Get direct children of a record (or top-level records if parentId is null).</summary>
    public List<AzdoTimelineRecord> GetChildren(string? parentId) =>
        Records.Where(r => r.ParentId == parentId).OrderBy(r => r.Order).ToList();
}

public class AzdoArtifact
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceData { get; init; }
    public string? ResourceUrl { get; init; }
}

public class ArtifactFileEntry
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public string? DownloadUrl { get; init; }
    public string? BlobId { get; init; }
}

public class AzdoJobTestSummary
{
    public required string JobName { get; init; }
    public int TotalCount { get; init; }
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
}

[JsonConverter(typeof(AzdoTestResultConverter))]
public class AzdoTestResult
{
    public int TestRunId { get; init; }
    public required string TestRunName { get; init; }
    public required int Id { get; init; }
    public required string TestCaseTitle { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? Comment { get; init; }
    public string? HelixJobName { get; init; }
    public string? HelixWorkItemName { get; init; }
    public bool IsHelixWorkItem { get; init; }
    public int SubResultCount { get; init; }
    public string? ResultGroupType { get; init; }
    public List<AzdoTestSubResult> SubResults { get; init; } = [];

    /// <summary>
    /// True if this is a grouped result (e.g. xUnit theory parent) that has
    /// sub-results representing individual invocations.
    /// </summary>
    public bool HasSubResults => ResultGroupType is not null &&
        !ResultGroupType.Equals("none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A sub-result of a test result, e.g. an individual xUnit theory invocation.
/// </summary>
[JsonConverter(typeof(AzdoTestSubResultConverter))]
public class AzdoTestSubResult
{
    public int Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? Comment { get; init; }
    public string? HelixJobName { get; init; }
    public string? HelixWorkItemName { get; init; }
    public bool IsHelixWorkItem { get; init; }

    /// <summary>
    /// Converts this sub-result to a full <see cref="AzdoTestResult"/> by merging
    /// with the parent result's run information.
    /// </summary>
    public AzdoTestResult ToTestResult(int testRunId, string testRunName) => new()
    {
        Id = Id,
        TestCaseTitle = DisplayName,
        Outcome = Outcome,
        ErrorMessage = ErrorMessage,
        StackTrace = StackTrace,
        Comment = Comment,
        HelixJobName = HelixJobName,
        HelixWorkItemName = HelixWorkItemName,
        IsHelixWorkItem = IsHelixWorkItem,
        TestRunId = testRunId,
        TestRunName = testRunName,
        SubResultCount = 0,
    };
}

internal sealed class AzdoTestSubResultConverter : JsonConverter<AzdoTestSubResult>
{
    public override AzdoTestSubResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var comment = root.TryGetProperty("comment", out var c) ? c.GetString() : null;

        string? helixJobName = null;
        string? helixWorkItemName = null;
        if (comment is not null && comment.Contains("HelixJobId", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var commentDoc = JsonDocument.Parse(comment);
                var commentRoot = commentDoc.RootElement;
                if (commentRoot.TryGetProperty("HelixJobId", out var jobId))
                    helixJobName = jobId.GetString();
                if (commentRoot.TryGetProperty("HelixWorkItemName", out var wiName))
                    helixWorkItemName = wiName.GetString();
            }
            catch (JsonException) { }
        }

        var errorMessage = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;

        return new AzdoTestSubResult
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            DisplayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "Unknown" : "Unknown",
            Outcome = root.TryGetProperty("outcome", out var o) ? o.GetString() ?? "Unknown" : "Unknown",
            ErrorMessage = errorMessage,
            StackTrace = root.TryGetProperty("stackTrace", out var st) ? st.GetString() : null,
            Comment = comment,
            HelixJobName = helixJobName,
            HelixWorkItemName = helixWorkItemName,
            IsHelixWorkItem = errorMessage is not null &&
                errorMessage.StartsWith("The Helix Work Item failed.", StringComparison.OrdinalIgnoreCase),
        };
    }

    public override void Write(Utf8JsonWriter writer, AzdoTestSubResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", value.Id);
        writer.WriteString("displayName", value.DisplayName);
        writer.WriteString("outcome", value.Outcome);
        writer.WriteString("errorMessage", value.ErrorMessage);
        writer.WriteString("stackTrace", value.StackTrace);
        writer.WriteString("comment", value.Comment);
        writer.WriteEndObject();
    }
}

internal sealed class AzdoTestResultConverter : JsonConverter<AzdoTestResult>
{
    public override AzdoTestResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        int testRunId = 0;
        string testRunName = "";
        if (root.TryGetProperty("testRun", out var testRun))
        {
            testRunId = testRun.TryGetProperty("id", out var runIdEl) && runIdEl.ValueKind == JsonValueKind.String
                ? int.Parse(runIdEl.GetString()!)
                : testRun.TryGetProperty("id", out var runIdNum) ? runIdNum.GetInt32() : 0;
            testRunName = testRun.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        }

        var comment = root.TryGetProperty("comment", out var c) ? c.GetString() : null;

        string? helixJobName = null;
        string? helixWorkItemName = null;
        if (comment is not null && comment.Contains("HelixJobId", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var commentDoc = JsonDocument.Parse(comment);
                var commentRoot = commentDoc.RootElement;
                if (commentRoot.TryGetProperty("HelixJobId", out var jobId))
                    helixJobName = jobId.GetString();
                if (commentRoot.TryGetProperty("HelixWorkItemName", out var wiName))
                    helixWorkItemName = wiName.GetString();
            }
            catch (JsonException)
            {
                // Comment is not JSON, ignore
            }
        }

        var errorMessage = root.TryGetProperty("errorMessage", out var em) ? em.GetString() : null;
        var subResultCount = root.TryGetProperty("subResultCount", out var src) ? src.GetInt32() : 0;
        var resultGroupType = root.TryGetProperty("resultGroupType", out var rgt) ? rgt.GetString() : null;

        var subResults = new List<AzdoTestSubResult>();
        if (root.TryGetProperty("subResults", out var subArray))
        {
            foreach (var subEl in subArray.EnumerateArray())
            {
                var sub = JsonSerializer.Deserialize<AzdoTestSubResult>(subEl.GetRawText(), options);
                if (sub is not null)
                    subResults.Add(sub);
            }
        }

        return new AzdoTestResult
        {
            Id = root.GetProperty("id").GetInt32(),
            TestCaseTitle = root.GetProperty("testCaseTitle").GetString()!,
            Outcome = root.GetProperty("outcome").GetString()!,
            ErrorMessage = errorMessage,
            StackTrace = root.TryGetProperty("stackTrace", out var st) ? st.GetString() : null,
            Comment = comment,
            TestRunId = testRunId,
            TestRunName = testRunName,
            HelixJobName = helixJobName,
            HelixWorkItemName = helixWorkItemName,
            IsHelixWorkItem = errorMessage is not null && errorMessage.StartsWith("The Helix Work Item failed.", StringComparison.OrdinalIgnoreCase),
            SubResultCount = subResultCount,
            ResultGroupType = resultGroupType,
            SubResults = subResults,
        };
    }

    public override void Write(Utf8JsonWriter writer, AzdoTestResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", value.Id);
        writer.WriteString("testCaseTitle", value.TestCaseTitle);
        writer.WriteString("outcome", value.Outcome);
        writer.WriteString("errorMessage", value.ErrorMessage);
        writer.WriteString("stackTrace", value.StackTrace);
        writer.WriteString("comment", value.Comment);
        writer.WriteNumber("testRunId", value.TestRunId);
        writer.WriteString("testRunName", value.TestRunName);
        writer.WriteString("helixJobName", value.HelixJobName);
        writer.WriteString("helixWorkItemName", value.HelixWorkItemName);
        writer.WriteBoolean("isHelixWorkItem", value.IsHelixWorkItem);
        writer.WriteNumber("subResultCount", value.SubResultCount);
        writer.WriteEndObject();
    }
}

public class AzdoTestAttachment
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("createdDate")]
    public DateTime? CreatedDate { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("attachmentType")]
    public string? AttachmentType { get; init; }
}
