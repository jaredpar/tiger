using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Tiger.Tests;

/// <summary>
/// Tests for the priority queue-jump behavior in <see cref="BuildIngestionService"/>.
/// Ingestion is now a single atomic pass per build, so priority no longer preempts
/// in-flight work — it only moves a build to the front of the queue so it's picked
/// up next once a worker slot frees.
/// </summary>
public partial class BuildIngestionPriorityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;

    public BuildIngestionPriorityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    /// <summary>
    /// Verifies that PrioritizeBuild moves a build to the front of the queue: once the
    /// currently in-flight build finishes, the prioritized build is picked up next,
    /// ahead of builds that would normally come first (by build_id descending).
    /// </summary>
    [Fact]
    public async Task PrioritizeBuild_JumpsQueue()
    {
        var order = new List<int>();
        var started = new SemaphoreSlim(0);
        var proceed = new SemaphoreSlim(0);

        var handler = new DelegateHandler(async (request, ct) =>
        {
            var url = request.RequestUri?.ToString() ?? "";

            // Only the initial "test summary" fetch (includeRunDetails=true) is gated;
            // this is the first HTTP call made per build during ingestion.
            if (url.Contains("includeRunDetails=true"))
            {
                var match = TestSummaryBuildIdRegex().Match(url);
                if (match.Success)
                {
                    var buildId = int.Parse(match.Groups[1].Value);
                    lock (order)
                    {
                        order.Add(buildId);
                    }
                    started.Release();
                    await proceed.WaitAsync(ct);
                }
            }

            return CreateJsonResponse("""{"count":0,"value":[]}""");
        });

        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(handler, org, proj));
        var service = new BuildIngestionService(_db, factory, maxParallelism: 1);

        InsertPendingBuild("org", "proj", buildId: 10);
        InsertPendingBuild("org", "proj", buildId: 20);
        InsertPendingBuild("org", "proj", buildId: 30);

        service.Start();

        // Without priority, builds are picked in build_id descending order, so build
        // 30 starts first.
        await started.WaitAsync();

        // Prioritize builds in the same top-to-bottom order as the build list.
        // They must be ingested in that order rather than in reverse.
        service.PrioritizeBuild("org", 10);
        service.PrioritizeBuild("org", 20);
        proceed.Release();

        // Build 10 should be picked up next, ahead of build 20.
        await started.WaitAsync();
        proceed.Release();

        await started.WaitAsync();
        proceed.Release();

        await service.StopAsync();

        Assert.Equal([30, 10, 20], order);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void InsertPendingBuild(string organization, string project, int buildId)
    {
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO builds
                    (organization, project, build_id, build_number, definition_name, definition_id,
                     status, result, source_branch, ingestion_status)
                VALUES
                    (@org, @proj, @buildId, @buildNumber, 'test-def', 1,
                     'completed', 'failed', 'refs/heads/main', 'pending')
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@proj", project);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.Parameters.AddWithValue("@buildNumber", $"20250101.{buildId}");
            cmd.ExecuteNonQuery();
        });
    }

    // ── Shared Infrastructure ───────────────────────────────────────

    /// <summary>
    /// An HttpMessageHandler that delegates to a func, avoiding the need for
    /// a separate subclass per test scenario.
    /// </summary>
    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    [GeneratedRegex(@"Build%2FBuild%2F(\d+)")]
    private static partial Regex TestSummaryBuildIdRegex();
}
