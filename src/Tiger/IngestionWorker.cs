namespace Tiger;

/// <summary>
/// Background worker that processes build ingestion tasks (tests, timeline, helix).
/// Picks up pending/failed tasks from the DB and processes them independently.
/// Uses exponential backoff for retries and a circuit breaker to avoid hammering
/// a broken API.
/// </summary>
public sealed class IngestionWorker : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly BuildIngestionService _ingestion;
    private readonly Func<string, string, AzdoClient> _clientFactory;
    private readonly ServiceLog? _log;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    private const int MaxAttempts = 5;
    private const int WorkerIntervalSeconds = 5;
    private const int CircuitBreakerThreshold = 5;
    private const int CircuitBreakerCooldownSeconds = 120;

    /// <summary>
    /// Backoff delays per attempt: 30s, 2min, 10min, 1hr, then abandon.
    /// </summary>
    private static readonly int[] s_backoffSeconds = [30, 120, 600, 3600];

    public bool IsRunning => _workerTask is not null && !_workerTask.IsCompleted;

    public IngestionWorker(
        TigerDatabase db,
        BuildIngestionService ingestion,
        Func<string, string, AzdoClient> clientFactory,
        ServiceLog? log = null)
    {
        _db = db;
        _ingestion = ingestion;
        _clientFactory = clientFactory;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _workerTask = WorkLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_workerTask is not null)
            {
                try { await _workerTask; } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tasks = GetReadyTasks();
                if (tasks.Count == 0)
                {
                    consecutiveFailures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(WorkerIntervalSeconds), ct);
                    continue;
                }

                foreach (var task in tasks)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        MarkTaskRunning(task);
                        await ProcessTaskAsync(task, ct);
                        MarkTaskComplete(task);
                        consecutiveFailures = 0;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var newAttempts = task.Attempts + 1;
                        if (newAttempts >= MaxAttempts)
                        {
                            MarkTaskAbandoned(task, ex.Message);
                            _log?.Error("Worker",
                                $"Task {task.TaskType} for build #{task.BuildId} abandoned after {newAttempts} attempts: {ex.Message}");
                        }
                        else
                        {
                            var backoffIndex = Math.Min(newAttempts - 1, s_backoffSeconds.Length - 1);
                            var delaySecs = s_backoffSeconds[backoffIndex];
                            MarkTaskFailed(task, ex.Message, delaySecs);
                            _log?.Warning("Worker",
                                $"Task {task.TaskType} for build #{task.BuildId} failed (attempt {newAttempts}), retry in {delaySecs}s: {ex.Message}");
                        }

                        consecutiveFailures++;
                        if (consecutiveFailures >= CircuitBreakerThreshold)
                        {
                            _log?.Warning("Worker",
                                $"Circuit breaker: {consecutiveFailures} consecutive failures, cooling down {CircuitBreakerCooldownSeconds}s");
                            await Task.Delay(TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds), ct);
                            consecutiveFailures = 0;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.Error("Worker", $"Unexpected error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(WorkerIntervalSeconds), ct);
            }
        }
    }

    private async Task ProcessTaskAsync(IngestionTask task, CancellationToken ct)
    {
        var client = _clientFactory(task.Organization, task.Project);

        switch (task.TaskType)
        {
            case "tests":
                await ProcessTestsAsync(client, task, ct);
                break;
            case "timeline":
                await ProcessTimelineAsync(client, task, ct);
                break;
            case "helix":
                // Helix ingestion placeholder — mark complete for now
                _log?.Info("Worker", $"Helix ingestion for build #{task.BuildId} — not yet implemented");
                break;
            default:
                _log?.Warning("Worker", $"Unknown task type: {task.TaskType}");
                break;
        }
    }

    private async Task ProcessTestsAsync(AzdoClient client, IngestionTask task, CancellationToken ct)
    {
        _log?.Info("Worker", $"Fetching tests for build #{task.BuildId}...");

        var testSummary = await client.GetTestSummaryByJobAsync(task.BuildId);
        var failures = await client.GetTestFailuresAsync(task.BuildId);

        var runGroups = failures.GroupBy(f => f.TestRunId);
        foreach (var group in runGroups)
        {
            var first = group.First();
            var summary = testSummary.FirstOrDefault(s => s.JobName == first.TestRunName);
            _ingestion.InsertTestRun(task.Organization, task.Project, task.BuildId, group.Key, first.TestRunName,
                summary?.TotalCount ?? group.Count(),
                summary?.PassedCount ?? 0,
                summary?.FailedCount ?? group.Count(),
                summary?.SkippedCount ?? 0);

            foreach (var r in group)
            {
                _ingestion.InsertTestResult(task.Organization, task.Project, group.Key, r);
            }
        }

        if (failures.Count > 0)
        {
            _log?.Warning("Worker",
                $"  Build #{task.BuildId} — {failures.Count} test failure(s) across {runGroups.Count()} run(s)");
        }
        else
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — tests complete (no failures)");
        }
    }

    private async Task ProcessTimelineAsync(AzdoClient client, IngestionTask task, CancellationToken ct)
    {
        _log?.Info("Worker", $"Fetching timeline for build #{task.BuildId}...");
        var timeline = await client.GetTimelineAsync(task.BuildId);
        _ingestion.IngestTimelineIssues(task.Organization, task.Project, task.BuildId, timeline);

        var issueCount = timeline.Records.Sum(r => r.Issues.Count(i => i.Type is "error" or "warning"));
        _log?.Info("Worker", $"  Build #{task.BuildId} — timeline complete ({issueCount} issues)");
    }

    // ── DB helpers ──────────────────────────────────────────────────

    private List<IngestionTask> GetReadyTasks()
    {
        var tasks = new List<IngestionTask>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT organization, project, build_id, task_type, status, attempts
            FROM build_ingestion_tasks
            WHERE status IN ('pending', 'failed')
              AND (next_retry_time IS NULL OR next_retry_time <= datetime('now'))
            ORDER BY build_id ASC, task_type ASC
            LIMIT 20
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(new IngestionTask(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }
        return tasks;
    }

    private void MarkTaskRunning(IngestionTask task)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE build_ingestion_tasks
            SET status = 'running', last_attempt_time = datetime('now')
            WHERE organization = @org AND project = @proj AND build_id = @buildId AND task_type = @type
            """;
        cmd.Parameters.AddWithValue("@org", task.Organization);
        cmd.Parameters.AddWithValue("@proj", task.Project);
        cmd.Parameters.AddWithValue("@buildId", task.BuildId);
        cmd.Parameters.AddWithValue("@type", task.TaskType);
        cmd.ExecuteNonQuery();
    }

    private void MarkTaskComplete(IngestionTask task)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE build_ingestion_tasks
            SET status = 'complete', completed_time = datetime('now'), last_error = NULL
            WHERE organization = @org AND project = @proj AND build_id = @buildId AND task_type = @type
            """;
        cmd.Parameters.AddWithValue("@org", task.Organization);
        cmd.Parameters.AddWithValue("@proj", task.Project);
        cmd.Parameters.AddWithValue("@buildId", task.BuildId);
        cmd.Parameters.AddWithValue("@type", task.TaskType);
        cmd.ExecuteNonQuery();
    }

    private void MarkTaskFailed(IngestionTask task, string error, int retryDelaySecs)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE build_ingestion_tasks
            SET status = 'failed',
                attempts = attempts + 1,
                last_error = @error,
                last_attempt_time = datetime('now'),
                next_retry_time = datetime('now', '+{retryDelaySecs} seconds')
            WHERE organization = @org AND project = @proj AND build_id = @buildId AND task_type = @type
            """;
        cmd.Parameters.AddWithValue("@org", task.Organization);
        cmd.Parameters.AddWithValue("@proj", task.Project);
        cmd.Parameters.AddWithValue("@buildId", task.BuildId);
        cmd.Parameters.AddWithValue("@type", task.TaskType);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    private void MarkTaskAbandoned(IngestionTask task, string error)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE build_ingestion_tasks
            SET status = 'abandoned',
                attempts = attempts + 1,
                last_error = @error,
                last_attempt_time = datetime('now')
            WHERE organization = @org AND project = @proj AND build_id = @buildId AND task_type = @type
            """;
        cmd.Parameters.AddWithValue("@org", task.Organization);
        cmd.Parameters.AddWithValue("@proj", task.Project);
        cmd.Parameters.AddWithValue("@buildId", task.BuildId);
        cmd.Parameters.AddWithValue("@type", task.TaskType);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Resets all abandoned tasks to pending for retry.
    /// </summary>
    public int RetryAbandoned()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE build_ingestion_tasks
            SET status = 'pending', attempts = 0, last_error = NULL, next_retry_time = NULL
            WHERE status = 'abandoned'
            """;
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private record IngestionTask(
        string Organization, string Project, int BuildId,
        string TaskType, string Status, int Attempts);
}
