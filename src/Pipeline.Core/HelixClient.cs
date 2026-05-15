using System.Net.Http.Headers;
using System.Text.Json;

namespace Pipeline.Core;

public sealed class HelixClient
{
    private const string BaseUrl = "https://helix.dot.net/";
    private const string ApiVersion = "2019-06-17";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient HttpClient { get; }

    private HelixClient(HttpClient httpClient)
    {
        HttpClient = httpClient;
    }

    /// <summary>
    /// Creates a new <see cref="HelixClient"/>. If a bearer token is provided it
    /// will be used for authentication; otherwise requests are unauthenticated
    /// (sufficient for all read-only endpoints).
    /// </summary>
    public static HelixClient Create(string? bearerToken = null)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        if (bearerToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return new HelixClient(httpClient);
    }

    /// <inheritdoc cref="Create"/>
    public static Task<HelixClient> CreateAsync(string? bearerToken = null) =>
        Task.FromResult(Create(bearerToken));

    /// <summary>
    /// Get summary information about a single job.
    /// </summary>
    public async Task<HelixJob> GetJobAsync(string jobName)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}?api-version={ApiVersion}";
        return await GetAsync<HelixJob>(url);
    }

    /// <summary>
    /// List all work items for a given job.
    /// </summary>
    public async Task<List<HelixWorkItemSummary>> GetWorkItemsAsync(string jobName)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}/workitems?api-version={ApiVersion}";
        return await GetAsync<List<HelixWorkItemSummary>>(url);
    }

    /// <summary>
    /// Get detailed information about a single work item.
    /// </summary>
    public async Task<HelixWorkItem> GetWorkItemAsync(string jobName, string workItemName)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}/workitems/{Uri.EscapeDataString(workItemName)}?api-version={ApiVersion}";
        return await GetAsync<HelixWorkItem>(url);
    }

    /// <summary>
    /// Get console output for a specific work item.
    /// </summary>
    public async Task<HelixWorkItemConsole> GetConsoleAsync(string jobName, string workItemName)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}/workitems/{Uri.EscapeDataString(workItemName)}/console?api-version={ApiVersion}";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return new HelixWorkItemConsole
        {
            Job = jobName,
            WorkItemName = workItemName,
            Text = text,
        };
    }

    /// <summary>
    /// Get console output for multiple work items.
    /// </summary>
    public async Task<List<HelixWorkItemConsole>> GetConsolesAsync(string jobName, List<HelixWorkItemSummary> workItems)
    {
        var list = new List<HelixWorkItemConsole>();
        foreach (var workItem in workItems)
        {
            var console = await GetConsoleAsync(jobName, workItem.Name);
            list.Add(console);
        }
        return list;
    }

    /// <summary>
    /// List files uploaded from a specific work item.
    /// </summary>
    public async Task<List<HelixUploadedFile>> GetFilesAsync(string jobName, string workItemName)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}/workitems/{Uri.EscapeDataString(workItemName)}/files?api-version={ApiVersion}";
        return await GetAsync<List<HelixUploadedFile>>(url);
    }

    /// <summary>
    /// Download a specific file from a work item to a local path.
    /// </summary>
    public async Task DownloadFileAsync(string jobName, string workItemName, string fileName, string outputPath)
    {
        var url = $"api/jobs/{Uri.EscapeDataString(jobName)}/workitems/{Uri.EscapeDataString(workItemName)}/files/{Uri.EscapeDataString(fileName)}?api-version={ApiVersion}";
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var dir = Path.GetDirectoryName(outputPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllBytesAsync(outputPath, bytes);
    }

    /// <summary>
    /// Download all files from a work item to a directory.
    /// </summary>
    public async Task DownloadFilesAsync(string jobName, string workItemName, string outputDir)
    {
        var files = await GetFilesAsync(jobName, workItemName);
        using var httpClient = new HttpClient();
        foreach (var file in files)
        {
            if (file.Name is null || file.Link is null)
                continue;
            var filePath = Path.Combine(outputDir, jobName, workItemName, file.Name);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            var bytes = await httpClient.GetByteArrayAsync(file.Link);
            await File.WriteAllBytesAsync(filePath, bytes);
        }
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, s_jsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize response from {url}");
    }
}