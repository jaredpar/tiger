using System.Net;
using Xunit;

namespace Tiger.Tests;

/// <summary>
/// Tests for the priority preemption behavior in <see cref="BuildIngestionService"/>.
/// </summary>
public class BuildIngestionPriorityTests : IDisposable
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
    /// Verifies that PrioritizeBuild preempts in-flight 'tests' tasks.
    /// The preempted tasks must be reset to 'pending' in the DB.
    /// </summary>
    [Fact]
    public async Task PrioritizeBuild_PreemptsTestsTasks()
    {
        var normalFetchStarted = new SemaphoreSlim(0);
        var priorityTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int priorityBuildId = 100;

        var handler = new DelegateHandler(async (request, ct) =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains($"Build%2F{priorityBuildId}") || url.Contains($"Build/{priorityBuildId}"))
            {
                priorityTaskStarted.TrySetResult();
                return CreateJsonResponse("""{"count":0,"value":[]}""");
            }
            normalFetchStarted.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });

        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(handler, org, proj));
        var service = new BuildIngestionService(_db, factory, maxParallelism: 2);

        InsertBuildWithTask("org", "proj", buildId: 1, taskType: "tests");
        InsertBuildWithTask("org", "proj", buildId: 2, taskType: "tests");
        InsertBuildWithTask("org", "proj", buildId: priorityBuildId, taskType: "tests");

        service.Start();
        await normalFetchStarted.WaitAsync();
        await normalFetchStarted.WaitAsync();

        service.PrioritizeBuild("org", priorityBuildId);
        await priorityTaskStarted.Task;
        await service.StopAsync();

        var status1 = GetTaskStatus("org", 1, "tests");
        var status2 = GetTaskStatus("org", 2, "tests");
        Assert.Equal("pending", status1);
        Assert.Equal("pending", status2);
    }

    /// <summary>
    /// Verifies that PrioritizeBuild preempts in-flight 'timeline' tasks.
    /// The preempted tasks must be reset to 'pending' in the DB.
    /// </summary>
    [Fact]
    public async Task PrioritizeBuild_PreemptsTimelineTasks()
    {
        var normalFetchStarted = new SemaphoreSlim(0);
        var priorityTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int priorityBuildId = 100;

        var handler = new DelegateHandler(async (request, ct) =>
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.Contains($"builds/{priorityBuildId}/timeline"))
            {
                priorityTaskStarted.TrySetResult();
                return CreateJsonResponse("""{"records":[]}""");
            }
            normalFetchStarted.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });

        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(handler, org, proj));
        var service = new BuildIngestionService(_db, factory, maxParallelism: 2);

        InsertBuildWithTask("org", "proj", buildId: 1, taskType: "timeline");
        InsertBuildWithTask("org", "proj", buildId: 2, taskType: "timeline");
        InsertBuildWithTask("org", "proj", buildId: priorityBuildId, taskType: "timeline");

        service.Start();
        await normalFetchStarted.WaitAsync();
        await normalFetchStarted.WaitAsync();

        service.PrioritizeBuild("org", priorityBuildId);
        await priorityTaskStarted.Task;
        await service.StopAsync();

        var status1 = GetTaskStatus("org", 1, "timeline");
        var status2 = GetTaskStatus("org", 2, "timeline");
        Assert.Equal("pending", status1);
        Assert.Equal("pending", status2);
    }

    /// <summary>
    /// Verifies that PrioritizeBuild preempts in-flight 'tests' tasks that are
    /// blocked fetching Helix work items. The Helix HTTP calls receive the
    /// cancellation token and are interrupted properly.
    /// </summary>
    [Fact]
    public async Task PrioritizeBuild_PreemptsHelixFetch()
    {
        var normalHelixStarted = new SemaphoreSlim(0);
        var priorityTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int priorityBuildId = 100;

        // AzDO handler: returns test results with helix info for normal builds,
        // returns empty for priority build
        var azdoHandler = new DelegateHandler((request, ct) =>
        {
            var url = request.RequestUri?.ToString() ?? "";

            if (url.Contains($"Build%2F{priorityBuildId}") || url.Contains($"Build/{priorityBuildId}"))
            {
                priorityTaskStarted.TrySetResult();
                return Task.FromResult(CreateJsonResponse("""{"count":0,"value":[]}"""));
            }

            // Return test runs with a helix-linked failure so the service proceeds to fetch helix data
            if (url.Contains("test/runs") && url.Contains("includeRunDetails"))
            {
                return Task.FromResult(CreateJsonResponse(
                    """{"count":1,"value":[{"id":1,"name":"TestRun","totalTests":1,"passedTests":0,"notApplicableTests":0,"unanalyzedTests":0}]}"""));
            }

            if (url.Contains("test/Runs/") && url.Contains("results"))
            {
                return Task.FromResult(CreateJsonResponse("""
                    {"count":1,"value":[{
                        "id":1,
                        "testRun":{"id":"1","name":"TestRun"},
                        "testCaseTitle":"SomeTest",
                        "automatedTestName":"Namespace.SomeTest",
                        "outcome":"Failed",
                        "comment":"{\"HelixJobId\":\"test-job\",\"HelixWorkItemName\":\"test-workitem\"}"
                    }]}
                    """));
            }

            if (url.Contains("test/runs"))
            {
                return Task.FromResult(CreateJsonResponse(
                    """{"count":1,"value":[{"id":1,"name":"TestRun","totalTests":1,"passedTests":0,"notApplicableTests":0,"unanalyzedTests":0}]}"""));
            }

            return Task.FromResult(CreateJsonResponse("""{"count":0,"value":[]}"""));
        });

        // Helix handler: blocks on work item fetch, signals when started
        var helixHandler = new DelegateHandler(async (request, ct) =>
        {
            normalHelixStarted.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });

        var factory = new AzdoClientFactory((org, proj) => AzdoClient.Create(azdoHandler, org, proj));
        Func<HelixClient> helixFactory = () => HelixClient.Create(helixHandler);
        var service = new BuildIngestionService(_db, factory, maxParallelism: 2, helixClientFactory: helixFactory);

        InsertBuildWithTask("org", "proj", buildId: 1, taskType: "tests");
        InsertBuildWithTask("org", "proj", buildId: 2, taskType: "tests");
        InsertBuildWithTask("org", "proj", buildId: priorityBuildId, taskType: "tests");

        service.Start();
        await normalHelixStarted.WaitAsync();
        await normalHelixStarted.WaitAsync();

        service.PrioritizeBuild("org", priorityBuildId);
        await priorityTaskStarted.Task;
        await service.StopAsync();

        var status1 = GetTaskStatus("org", 1, "tests");
        var status2 = GetTaskStatus("org", 2, "tests");
        Assert.Equal("pending", status1);
        Assert.Equal("pending", status2);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void InsertBuildWithTask(string organization, string project, int buildId, string taskType)
    {
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO builds
                    (organization, project, build_id, build_number, definition_name, definition_id,
                     status, result, source_branch)
                VALUES
                    (@org, @proj, @buildId, @buildNumber, 'test-def', 1,
                     'completed', 'failed', 'refs/heads/main')
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@proj", project);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.Parameters.AddWithValue("@buildNumber", $"20250101.{buildId}");
            cmd.ExecuteNonQuery();
        });

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO build_ingestion_tasks
                    (organization, build_id, task_type, status, is_complete, attempts)
                VALUES
                    (@org, @buildId, @taskType, 'pending', 0, 0)
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.Parameters.AddWithValue("@taskType", taskType);
            cmd.ExecuteNonQuery();
        });
    }

    private string GetTaskStatus(string organization, int buildId, string taskType)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT status FROM build_ingestion_tasks
                WHERE organization = @org AND build_id = @buildId AND task_type = @type
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.Parameters.AddWithValue("@type", taskType);
            return cmd.ExecuteScalar() as string ?? throw new InvalidOperationException(
                $"No task found for org={organization}, buildId={buildId}, taskType={taskType}");
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
}
