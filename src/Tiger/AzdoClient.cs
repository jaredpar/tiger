using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace Tiger;

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

    /// <summary>
    /// Shared rate-limit state for this organization. Updated after every HTTP response.
    /// </summary>
    public AzdoRateLimitState AzdoRateLimitState { get; }

    private AzdoClient(HttpClient httpClient, string organization, string project, AzdoRateLimitState rateLimitState)
    {
        HttpClient = httpClient;
        Organization = organization;
        Project = project;
        AzdoRateLimitState = rateLimitState;
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
        return Create(tokenCredential, organization, project, new AzdoRateLimitState());
    }

    internal static AzdoClient Create(
        TokenCredential tokenCredential,
        string organization,
        string project,
        AzdoRateLimitState rateLimitState)
    {
        var httpClient = new HttpClient(new BearerTokenHandler(tokenCredential, rateLimitState))
        {
            BaseAddress = new Uri($"https://dev.azure.com/{organization}/{project}/"),
        };

        return new AzdoClient(httpClient, organization, project, rateLimitState);
    }

    /// <inheritdoc cref="Create"/>
    public static Task<AzdoClient> CreateAsync(
        TokenCredential tokenCredential,
        string organization = DefaultOrganization,
        string project = DefaultProject) =>
        Task.FromResult(Create(tokenCredential, organization, project));

    private string GetBuildUri(int buildId) =>
        $"https://dev.azure.com/{Organization}/{Project}/_build/results?buildId={buildId}";

    private AzdoBuild MapBuild(AzdoBuildRaw b) => new()
    {
        Id = b.Id,
        BuildNumber = b.BuildNumber,
        Status = b.Status,
        Result = b.Result,
        Uri = GetBuildUri(b.Id),
        SourceBranch = b.SourceBranch,
        DefinitionName = b.Definition?.Name ?? "unknown",
        DefinitionId = b.Definition?.Id ?? 0,
        SourceVersion = b.SourceVersion,
        RepositoryName = b.Repository?.Id ?? b.Repository?.Name,
        PrNumber = ExtractPrNumber(b.SourceBranch),
        FinishTime = b.FinishTime,
    };

    private static int? ExtractPrNumber(string sourceBranch)
    {
        // refs/pull/42/merge → 42
        if (sourceBranch.StartsWith("refs/pull/", StringComparison.Ordinal))
        {
            var parts = sourceBranch.Split('/');
            if (parts.Length >= 3 && int.TryParse(parts[2], out var pr))
                return pr;
        }
        return null;
    }

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

        return result.Value.Select(MapBuild).ToList();
    }

    /// <summary>
    /// Fetches all completed builds since <paramref name="minFinishTime"/>, paginating
    /// through the AzDO API using continuation tokens. Returns builds ordered oldest-first.
    /// Optionally filters to a specific repository.
    /// </summary>
    public async Task<List<AzdoBuild>> GetCompletedBuildsSinceAsync(
        DateTime minFinishTime,
        string? repositoryId = null,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        var all = new List<AzdoBuild>();
        string? continuationToken = null;
        var minTimeStr = minFinishTime.ToUniversalTime().ToString("o");

        do
        {
            ct.ThrowIfCancellationRequested();

            var url = $"_apis/build/builds?api-version=7.1&statusFilter=completed&minTime={Uri.EscapeDataString(minTimeStr)}&$top={pageSize}&queryOrder=finishTimeAscending";
            if (repositoryId is not null)
                url += $"&repositoryId={Uri.EscapeDataString(repositoryId)}&repositoryType=GitHub";
            if (continuationToken is not null)
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";

            var response = await HttpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            // AzDO returns continuation token in the x-ms-continuationtoken header
            continuationToken = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize builds response");

            all.AddRange(result.Value.Select(MapBuild));
        }
        while (continuationToken is not null);

        return all;
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

        return result.Value.Select(MapBuild).ToList();
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

        return result.Value.Select(MapBuild).ToList();
    }

    /// <summary>
    /// Fetches test failures for a build.
    /// </summary>
    /// <param name="buildId">The build ID.</param>
    /// <param name="subResultCount">Controls sub-result fetching for grouped results (e.g. xUnit theories):
    /// null = do not fetch sub-results, positive = fetch up to that many, -1 = no limit.</param>
    public async Task<List<AzdoTestResult>> GetTestFailuresAsync(int buildId, int? subResultCount = null)
    {
        var buildUri = $"vstfs:///Build/Build/{buildId}";
        var runsUrl = $"_apis/test/runs?api-version=7.1&buildUri={Uri.EscapeDataString(buildUri)}";

        var runsResponse = await HttpClient.GetAsync(runsUrl);
        runsResponse.EnsureSuccessStatusCode();

        var runsJson = await runsResponse.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestRun>>(runsJson, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test runs response");

        var failures = new List<AzdoTestResult>();
        var subResultsFetched = 0;

        foreach (var run in runs.Value)
        {
            var resultsUrl = $"_apis/test/Runs/{run.Id}/results?api-version=7.1&outcomes=Failed";
            var resultsResponse = await HttpClient.GetAsync(resultsUrl);
            resultsResponse.EnsureSuccessStatusCode();

            var resultsJson = await resultsResponse.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestResult>>(resultsJson, s_jsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize test results response");

            foreach (var result in results.Value)
            {
                var canFetchSubs = subResultCount is not null &&
                    (subResultCount == -1 || subResultsFetched < subResultCount);

                if (result.HasSubResults && canFetchSubs)
                {
                    var detailed = await GetTestResultWithSubResultsAsync(run.Id, result.Id);
                    if (detailed is not null && detailed.SubResults.Count > 0)
                    {
                        var failedSubs = detailed.SubResults
                            .Where(s => s.Outcome == "Failed")
                            .Select(s => s.ToTestResult(result.TestRunId, result.TestRunName))
                            .ToList();

                        if (failedSubs.Count > 0)
                        {
                            failures.AddRange(failedSubs);
                            subResultsFetched++;
                            continue;
                        }
                    }
                }

                failures.Add(result);
            }
        }

        return failures;
    }

    /// <summary>
    /// Fetches a single test result with sub-results included.
    /// </summary>
    private async Task<AzdoTestResult?> GetTestResultWithSubResultsAsync(int runId, int resultId)
    {
        var url = $"_apis/test/Runs/{runId}/results/{resultId}?api-version=7.1&detailsToInclude=SubResults";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AzdoTestResult>(json, s_jsonOptions);
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
            RunId = r.Id,
            JobName = r.Name,
            TotalCount = r.TotalTests,
            PassedCount = r.PassedTests,
            FailedCount = r.TotalTests - r.PassedTests - r.NotApplicableTests - r.UnanalyzedTests,
            SkippedCount = r.NotApplicableTests,
            Duration = r.StartedDate is not null && r.CompletedDate is not null
                ? r.CompletedDate.Value - r.StartedDate.Value
                : null,
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
            ResourceData = a.Resource?.Data,
            ResourceUrl = a.Resource?.Url,
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

    /// <summary>
    /// Lists files within an artifact. Supports both Container and PipelineArtifact types.
    /// </summary>
    public async Task<List<ArtifactFileEntry>> GetArtifactFilesAsync(int buildId, AzdoArtifact artifact)
    {
        if (artifact.ResourceType == "Container")
        {
            return await GetContainerFilesAsync(artifact);
        }
        else if (artifact.ResourceType == "PipelineArtifact")
        {
            return await GetPipelineArtifactFilesAsync(buildId, artifact);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported artifact resource type '{artifact.ResourceType}' for artifact '{artifact.Name}'");
        }
    }

    /// <summary>
    /// Downloads a single file from an artifact to the specified output path.
    /// </summary>
    public async Task DownloadArtifactFileAsync(int buildId, AzdoArtifact artifact, ArtifactFileEntry file, string outputPath)
    {
        string downloadUrl;
        if (artifact.ResourceType == "Container")
        {
            downloadUrl = file.DownloadUrl
                ?? throw new InvalidOperationException($"No download URL for file '{file.Path}'");
        }
        else
        {
            // PipelineArtifact — use the file ID to download
            downloadUrl = $"_apis/build/builds/{buildId}/artifacts?artifactName={Uri.EscapeDataString(artifact.Name)}&fileId={file.BlobId}&fileName={Uri.EscapeDataString(Path.GetFileName(file.Path))}&api-version=7.1";
        }

        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = File.Create(outputPath);
        await response.Content.CopyToAsync(fileStream);
    }

    private async Task<List<ArtifactFileEntry>> GetContainerFilesAsync(AzdoArtifact artifact)
    {
        // resource.data is "#/{containerId}/{artifactName}"
        var data = artifact.ResourceData
            ?? throw new InvalidOperationException($"Artifact '{artifact.Name}' has no resource data");

        // Use the resource URL directly — it's a pre-built container query URL
        var url = artifact.ResourceUrl
            ?? throw new InvalidOperationException($"Artifact '{artifact.Name}' has no resource URL");

        // The resource URL points to the container. Append $format=json if not present
        var listUrl = url.Contains("?") ? $"{url}&$format=json" : $"{url}?$format=json";

        using var response = await HttpClient.GetAsync(listUrl);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var container = JsonSerializer.Deserialize<ContainerItemsResponse>(json, s_jsonOptions);
        if (container?.Value is null)
        {
            return [];
        }

        return container.Value
            .Where(i => i.ItemType == "file")
            .Select(i => new ArtifactFileEntry
            {
                Path = i.Path ?? "",
                Size = i.FileLength ?? 0,
                DownloadUrl = i.ContentLocation,
            }).ToList();
    }

    private async Task<List<ArtifactFileEntry>> GetPipelineArtifactFilesAsync(int buildId, AzdoArtifact artifact)
    {
        var fileId = artifact.ResourceData
            ?? throw new InvalidOperationException($"Artifact '{artifact.Name}' has no resource data (file ID)");

        var url = $"_apis/build/builds/{buildId}/artifacts?artifactName={Uri.EscapeDataString(artifact.Name)}&fileId={fileId}&fileName=manifest.json&api-version=7.1";

        using var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var manifest = JsonSerializer.Deserialize<PipelineArtifactManifest>(json, s_jsonOptions);
        if (manifest?.Items is null)
        {
            return [];
        }

        return manifest.Items
            .Where(i => i.Blob is not null)
            .Select(i => new ArtifactFileEntry
            {
                Path = i.Path ?? "",
                Size = i.Blob!.Size,
                BlobId = i.Blob.Id,
            }).ToList();
    }

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

        [JsonPropertyName("sourceVersion")]
        public string? SourceVersion { get; init; }

        [JsonPropertyName("definition")]
        public AzdoBuildDefinition? Definition { get; init; }

        [JsonPropertyName("repository")]
        public AzdoBuildRepository? Repository { get; init; }

        [JsonPropertyName("finishTime")]
        public DateTime? FinishTime { get; init; }
    }

    private class AzdoBuildDefinition
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    private class AzdoBuildRepository
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
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

        [JsonPropertyName("startedDate")]
        public DateTime? StartedDate { get; init; }

        [JsonPropertyName("completedDate")]
        public DateTime? CompletedDate { get; init; }
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

        [JsonPropertyName("data")]
        public string? Data { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    // Container artifact file listing types
    private class ContainerItemsResponse
    {
        [JsonPropertyName("value")]
        public List<ContainerItem>? Value { get; init; }
    }

    private class ContainerItem
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("itemType")]
        public string? ItemType { get; init; }

        [JsonPropertyName("fileLength")]
        public long? FileLength { get; init; }

        [JsonPropertyName("contentLocation")]
        public string? ContentLocation { get; init; }
    }

    // Pipeline artifact manifest types
    private class PipelineArtifactManifest
    {
        [JsonPropertyName("items")]
        public List<PipelineArtifactItem>? Items { get; init; }
    }

    private class PipelineArtifactItem
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("blob")]
        public PipelineArtifactBlob? Blob { get; init; }
    }

    private class PipelineArtifactBlob
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }

    private sealed class BearerTokenHandler : DelegatingHandler
    {
        private readonly TokenCredential _credential;
        private readonly TokenRequestContext _context = new(["499b84ac-1321-427f-aa17-267ca6975798/.default"]);
        private readonly AzdoRateLimitState _rateLimitState;

        public BearerTokenHandler(TokenCredential credential, AzdoRateLimitState rateLimitState)
            : base(new HttpClientHandler())
        {
            _credential = credential;
            _rateLimitState = rateLimitState;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Honor any active Retry-After delay before sending
            var delay = _rateLimitState.DelayRemaining;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var token = await _credential.GetTokenAsync(_context, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            var response = await base.SendAsync(request, cancellationToken);
            _rateLimitState.Update(response.Headers);

            // If rate-limited (429), wait and retry once
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                delay = _rateLimitState.DelayRemaining;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                // Need a fresh request since the original may have been consumed
                using var retryRequest = new HttpRequestMessage(request.Method, request.RequestUri);
                token = await _credential.GetTokenAsync(_context, cancellationToken);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                response = await base.SendAsync(retryRequest, cancellationToken);
                _rateLimitState.Update(response.Headers);
            }

            return response;
        }
    }
}
