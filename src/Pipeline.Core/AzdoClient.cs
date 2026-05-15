using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace Pipeline.Core;

public sealed class AzdoClient
{
    public const string DefaultOrganization = "dnceng-public";
    public const string DefaultProject = "public";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient HttpClient { get; }
    private string Organization { get; }
    private string Project { get; }

    private AzdoClient(HttpClient httpClient, string organization, string project)
    {
        HttpClient = httpClient;
        Organization = organization;
        Project = project;
    }

    /// <summary>
    /// Creates a new <see cref="AzdoClient"/>. Authentication is deferred
    /// until the first HTTP request is made.
    /// </summary>
    public static AzdoClient Create(
        TokenCredential tokenCredential,
        string organization = DefaultOrganization,
        string project = DefaultProject)
    {
        var httpClient = new HttpClient(new BearerTokenHandler(tokenCredential))
        {
            BaseAddress = new Uri($"https://dev.azure.com/{organization}/{project}/"),
        };

        return new AzdoClient(httpClient, organization, project);
    }

    /// <inheritdoc cref="Create"/>
    public static Task<AzdoClient> CreateAsync(
        TokenCredential tokenCredential,
        string organization = DefaultOrganization,
        string project = DefaultProject) =>
        Task.FromResult(Create(tokenCredential, organization, project));

    private string GetBuildUri(int buildId) =>
        $"https://dev.azure.com/{Organization}/{Project}/_build/results?buildId={buildId}";

    public async Task<List<AzdoBuild>> GetRecentBuildsAsync(int? definitionId = null, int top = 10)
    {
        var url = $"_apis/build/builds?api-version=7.1&$top={top}";
        if (definitionId is not null)
        {
            url += $"&definitions={definitionId}";
        }

        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoBuild>> GetBuildsForRepositoryAsync(string repository, int top = 10, string? reasonFilter = null)
    {
        var url = $"_apis/build/builds?api-version=7.1&$top={top}&repositoryId={Uri.EscapeDataString(repository)}&repositoryType=GitHub";
        if (reasonFilter is not null)
        {
            url += $"&reasonFilter={Uri.EscapeDataString(reasonFilter)}";
        }

        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoBuild>> GetBuildsForPullRequestAsync(string repository, int prNumber, int top = 10)
    {
        var branchName = $"refs/pull/{prNumber}/merge";
        var url = $"_apis/build/builds?api-version=7.1&$top={top}&branchName={Uri.EscapeDataString(branchName)}&repositoryId={Uri.EscapeDataString(repository)}&repositoryType=GitHub";

        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoTestResult>> GetTestFailuresAsync(int buildId)
    {
        var buildUri = $"vstfs:///Build/Build/{buildId}";
        var runsUrl = $"_apis/test/runs?api-version=7.1&buildUri={Uri.EscapeDataString(buildUri)}";

        var runsResponse = await HttpClient.GetAsync(runsUrl);
        runsResponse.EnsureSuccessStatusCode();

        var runsJson = await runsResponse.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestRun>>(runsJson, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test runs response");

        var failures = new List<AzdoTestResult>();
        foreach (var run in runs.Value)
        {
            var resultsUrl = $"_apis/test/Runs/{run.Id}/results?api-version=7.1&outcomes=Failed";
            var resultsResponse = await HttpClient.GetAsync(resultsUrl);
            resultsResponse.EnsureSuccessStatusCode();

            var resultsJson = await resultsResponse.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestResult>>(resultsJson, s_jsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize test results response");

            failures.AddRange(results.Value);
        }

        return failures;
    }

    public async Task<List<AzdoTestAttachment>> GetTestResultAttachmentsAsync(int runId, int testCaseResultId)
    {
        var url = $"_apis/test/Runs/{runId}/Results/{testCaseResultId}/attachments?api-version=7.2-preview.1";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestAttachment>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test result attachments response");

        return result.Value;
    }

    public async Task<List<AzdoJobTestSummary>> GetTestSummaryByJobAsync(int buildId)
    {
        var buildUri = $"vstfs:///Build/Build/{buildId}";
        var runsUrl = $"_apis/test/runs?api-version=7.1&includeRunDetails=true&buildUri={Uri.EscapeDataString(buildUri)}";

        var response = await HttpClient.GetAsync(runsUrl);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestRun>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test runs response");

        return runs.Value.Select(r => new AzdoJobTestSummary
        {
            JobName = r.Name,
            TotalCount = r.TotalTests,
            PassedCount = r.PassedTests,
            FailedCount = r.TotalTests - r.PassedTests - r.NotApplicableTests,
            SkippedCount = r.NotApplicableTests,
        }).ToList();
    }

    public async Task<AzdoTimeline> GetTimelineAsync(int buildId)
    {
        var url = $"_apis/build/builds/{buildId}/timeline?api-version=7.1";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var raw = JsonSerializer.Deserialize<AzdoTimelineRaw>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize timeline response");

        return new AzdoTimeline
        {
            Records = (raw.Records ?? []).Select(r => new AzdoTimelineRecord
            {
                Id = r.Id,
                ParentId = r.ParentId,
                Name = r.Name,
                RecordType = r.Type,
                Order = r.Order,
                State = r.State,
                Result = r.Result,
                ErrorCount = r.ErrorCount,
                WarningCount = r.WarningCount,
                StartTime = r.StartTime,
                FinishTime = r.FinishTime,
                WorkerName = r.WorkerName,
                LogUrl = r.Log?.Url,
                Issues = (r.Issues ?? []).Select(i => new AzdoTimelineIssue
                {
                    Type = i.Type,
                    Message = i.Message,
                    Category = i.Category,
                }).ToList(),
            }).ToList(),
        };
    }

    public async Task<List<AzdoArtifact>> GetArtifactsAsync(int buildId)
    {
        var url = $"_apis/build/builds/{buildId}/artifacts?api-version=7.1";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var raw = JsonSerializer.Deserialize<AzdoListResponse<AzdoArtifactRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize artifacts response");

        return raw.Value.Select(a => new AzdoArtifact
        {
            Id = a.Id,
            Name = a.Name,
            DownloadUrl = a.Resource?.DownloadUrl,
            ResourceType = a.Resource?.Type,
        }).ToList();
    }

    public async Task DownloadArtifactAsync(int buildId, string artifactName, string outputPath)
    {
        var artifacts = await GetArtifactsAsync(buildId);
        var artifact = artifacts.FirstOrDefault(a => a.Name == artifactName)
            ?? throw new InvalidOperationException($"Artifact '{artifactName}' not found for build {buildId}");

        var downloadUrl = artifact.DownloadUrl
            ?? throw new InvalidOperationException($"Artifact '{artifactName}' has no download URL");

        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using var fileStream = File.Create(outputPath);
        await response.Content.CopyToAsync(fileStream);
    }

    // buildNumber overloads — resolve to buildId via the builds list endpoint, then delegate

    private async Task<int> ResolveIdAsync(string buildNumber)
    {
        var url = $"_apis/build/builds?api-version=7.1&buildNumber={Uri.EscapeDataString(buildNumber)}&$top=1";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        if (result.Value.Count == 0)
            throw new InvalidOperationException($"No build found with buildNumber '{buildNumber}'");

        return result.Value[0].Id;
    }

    public async Task<List<AzdoTestResult>> GetTestFailuresAsync(string buildNumber) =>
        await GetTestFailuresAsync(await ResolveIdAsync(buildNumber));

    public async Task<List<AzdoJobTestSummary>> GetTestSummaryByJobAsync(string buildNumber) =>
        await GetTestSummaryByJobAsync(await ResolveIdAsync(buildNumber));

    public async Task<AzdoTimeline> GetTimelineAsync(string buildNumber) =>
        await GetTimelineAsync(await ResolveIdAsync(buildNumber));

    public async Task<List<AzdoArtifact>> GetArtifactsAsync(string buildNumber) =>
        await GetArtifactsAsync(await ResolveIdAsync(buildNumber));

    public async Task DownloadArtifactAsync(string buildNumber, string artifactName, string outputPath) =>
        await DownloadArtifactAsync(await ResolveIdAsync(buildNumber), artifactName, outputPath);

    // Internal types for JSON deserialization of raw API responses

    private class AzdoListResponse<T>
    {
        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("value")]
        public required List<T> Value { get; init; }
    }

    private class AzdoBuildRaw
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

        [JsonPropertyName("definition")]
        public AzdoBuildDefinition? Definition { get; init; }

        [JsonPropertyName("finishTime")]
        public DateTime? FinishTime { get; init; }
    }

    private class AzdoBuildDefinition
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    private class AzdoTestRun
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("totalTests")]
        public int TotalTests { get; init; }

        [JsonPropertyName("passedTests")]
        public int PassedTests { get; init; }

        [JsonPropertyName("unanalyzedTests")]
        public int UnanalyzedTests { get; init; }

        [JsonPropertyName("notApplicableTests")]
        public int NotApplicableTests { get; init; }
    }

    private class AzdoTimelineRaw
    {
        [JsonPropertyName("records")]
        public List<AzdoTimelineRecordRaw>? Records { get; init; }
    }

    private class AzdoTimelineRecordRaw
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("parentId")]
        public string? ParentId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("order")]
        public int Order { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("errorCount")]
        public int ErrorCount { get; init; }

        [JsonPropertyName("warningCount")]
        public int WarningCount { get; init; }

        [JsonPropertyName("startTime")]
        public DateTime? StartTime { get; init; }

        [JsonPropertyName("finishTime")]
        public DateTime? FinishTime { get; init; }

        [JsonPropertyName("workerName")]
        public string? WorkerName { get; init; }

        [JsonPropertyName("issues")]
        public List<AzdoTimelineIssueRaw>? Issues { get; init; }

        [JsonPropertyName("log")]
        public AzdoBuildLogReference? Log { get; init; }
    }

    private class AzdoTimelineIssueRaw
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }
    }

    private class AzdoBuildLogReference
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    private class AzdoArtifactRaw
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("resource")]
        public AzdoArtifactResourceRaw? Resource { get; init; }
    }

    private class AzdoArtifactResourceRaw
    {
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }

    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly TokenCredential _credential;
        private readonly TokenRequestContext _context = new(["499b84ac-1321-427f-aa17-267ca6975798/.default"]);

        public BearerTokenHandler(TokenCredential credential)
            : base(new HttpClientHandler())
        {
            _credential = credential;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await _credential.GetTokenAsync(_context, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
