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

    private const int MaxParallelism = 4;
    private readonly object _dbLock = new();

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<IngestionTask> tasks;
                lock (_dbLock)
                {
                    tasks = GetReadyTasks();
                }

                if (tasks.Count == 0)
                {
                    consecutiveFailures = 0;
                    await Task.Delay(TimeSpan.FromSeconds(WorkerIntervalSeconds), ct);
                    continue;
                }

                // Mark all as running before launching parallel work
                lock (_dbLock)
                {
                    foreach (var task in tasks)
                        MarkTaskRunning(task);
                }

                using var semaphore = new SemaphoreSlim(MaxParallelism);
                var taskList = tasks.Select(async task =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        await ProcessTaskAsync(task, ct);
                        lock (_dbLock)
                        {
                            MarkTaskComplete(task);
                        }
                        Interlocked.Exchange(ref consecutiveFailures, 0);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var newAttempts = task.Attempts + 1;
                        lock (_dbLock)
                        {
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
                        }
                        Interlocked.Increment(ref consecutiveFailures);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(taskList);

                if (consecutiveFailures >= CircuitBreakerThreshold)
                {
                    _log?.Warning("Worker",
                        $"Circuit breaker: {consecutiveFailures} consecutive failures, cooling down {CircuitBreakerCooldownSeconds}s");
                    await Task.Delay(TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds), ct);
                    consecutiveFailures = 0;
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
                await ProcessHelixAsync(task, ct);
                break;
            case "pr_info":
                await ProcessPrInfoAsync(task, ct);
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
        var failures = await client.GetTestFailuresAsync(task.BuildId, subResultCount: 50);

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

    private async Task ProcessHelixAsync(IngestionTask task, CancellationToken ct)
    {
        _log?.Info("Worker", $"Fetching helix work items for build #{task.BuildId}...");

        // Get distinct helix job/work-item pairs from failed test results for this build
        var workItemKeys = new List<(string JobName, string WorkItemName)>();
        lock (_dbLock)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT tr.helix_job_name, tr.helix_work_item_name
                FROM test_results tr
                JOIN test_runs trn ON tr.organization = trn.organization
                    AND tr.project = trn.project AND tr.run_id = trn.run_id
                WHERE trn.organization = @org AND trn.project = @proj AND trn.build_id = @buildId
                  AND tr.helix_job_name IS NOT NULL AND tr.helix_work_item_name IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("@org", task.Organization);
            cmd.Parameters.AddWithValue("@proj", task.Project);
            cmd.Parameters.AddWithValue("@buildId", task.BuildId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                workItemKeys.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (workItemKeys.Count == 0)
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — no helix work items to fetch");
            return;
        }

        var helixClient = HelixClient.Create();
        var insertedCount = 0;

        foreach (var (jobName, workItemName) in workItemKeys)
        {
            if (ct.IsCancellationRequested)
                break;

            // Skip if already fetched
            bool exists;
            lock (_dbLock)
            {
                using var checkCmd = _db.Connection.CreateCommand();
                checkCmd.CommandText = "SELECT 1 FROM helix_work_items WHERE job_name = @job AND work_item_name = @wi";
                checkCmd.Parameters.AddWithValue("@job", jobName);
                checkCmd.Parameters.AddWithValue("@wi", workItemName);
                exists = checkCmd.ExecuteScalar() is not null;
            }

            if (exists)
                continue;

            try
            {
                var workItem = await helixClient.GetWorkItemAsync(jobName, workItemName);

                // Collect non-console-log files as JSON
                string? filesJson = null;
                if (workItem.Files is { Count: > 0 })
                {
                    var filtered = workItem.Files
                        .Where(f => !f.IsConsoleLog)
                        .Select(f => new { fileName = f.FileName, uri = f.Uri })
                        .ToList();
                    if (filtered.Count > 0)
                    {
                        filesJson = System.Text.Json.JsonSerializer.Serialize(filtered);
                    }
                }

                lock (_dbLock)
                {
                    using var insertCmd = _db.Connection.CreateCommand();
                    insertCmd.CommandText = """
                        INSERT OR IGNORE INTO helix_work_items
                            (job_name, work_item_name, state, exit_code, console_output_uri, files)
                        VALUES
                            (@job, @wi, @state, @exitCode, @consoleUri, @files)
                        """;
                    insertCmd.Parameters.AddWithValue("@job", workItem.Job);
                    insertCmd.Parameters.AddWithValue("@wi", workItem.Name);
                    insertCmd.Parameters.AddWithValue("@state", workItem.State);
                    insertCmd.Parameters.AddWithValue("@exitCode", workItem.ExitCode.HasValue ? workItem.ExitCode.Value : DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@consoleUri", (object?)workItem.ConsoleOutputUri ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
                    insertCmd.ExecuteNonQuery();
                }
                insertedCount++;
            }
            catch (HttpRequestException ex)
            {
                _log?.Warning("Worker", $"  Failed to fetch helix work item {jobName}/{workItemName}: {ex.Message}");
            }
        }

        _log?.Info("Worker", $"  Build #{task.BuildId} — helix complete ({insertedCount} work item(s) fetched)");
    }

    private async Task ProcessPrInfoAsync(IngestionTask task, CancellationToken ct)
    {
        // Look up the build's PR number and repo
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT pr_number, repository_name FROM builds WHERE organization = @org AND project = @proj AND build_id = @buildId";
        cmd.Parameters.AddWithValue("@org", task.Organization);
        cmd.Parameters.AddWithValue("@proj", task.Project);
        cmd.Parameters.AddWithValue("@buildId", task.BuildId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — no PR info to fetch");
            return;
        }

        var prNumber = reader.GetInt32(0);
        var repository = reader.GetString(1);
        reader.Close();

        // Check if we already have this PR cached
        using var checkCmd = _db.Connection.CreateCommand();
        checkCmd.CommandText = "SELECT 1 FROM pull_requests WHERE repository = @repo AND pr_number = @pr";
        checkCmd.Parameters.AddWithValue("@repo", repository);
        checkCmd.Parameters.AddWithValue("@pr", prNumber);
        if (checkCmd.ExecuteScalar() is not null)
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — PR #{prNumber} already cached");
            return;
        }

        // Fetch PR info via gh CLI
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", $"pr view {prNumber} --repo {repository} --json title,author")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                _log?.Warning("Worker", $"  Build #{task.BuildId} — failed to start gh process");
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _log?.Warning("Worker", $"  Build #{task.BuildId} — gh pr view failed (exit {process.ExitCode})");
                return;
            }

            var prData = System.Text.Json.JsonDocument.Parse(output);
            var title = prData.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var author = prData.RootElement.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var login)
                ? login.GetString() : null;

            using var insertCmd = _db.Connection.CreateCommand();
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO pull_requests (repository, pr_number, title, author)
                VALUES (@repo, @pr, @title, @author)
                """;
            insertCmd.Parameters.AddWithValue("@repo", repository);
            insertCmd.Parameters.AddWithValue("@pr", prNumber);
            insertCmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@author", (object?)author ?? DBNull.Value);
            insertCmd.ExecuteNonQuery();

            _log?.Info("Worker", $"  Build #{task.BuildId} — PR #{prNumber} info cached ({author})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("Worker", $"  Build #{task.BuildId} — PR info fetch failed: {ex.Message}");
        }
    }

    // ── DB helpers ──────────────────────────────────────────────────

    private List<IngestionTask> GetReadyTasks()
    {
        var tasks = new List<IngestionTask>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT organization, project, build_id, task_type, status, attempts
            FROM build_ingestion_tasks t
            WHERE status IN ('pending', 'failed')
              AND (next_retry_time IS NULL OR next_retry_time <= datetime('now'))
              AND (task_type != 'helix' OR EXISTS (
                  SELECT 1 FROM build_ingestion_tasks dep
                  WHERE dep.organization = t.organization
                    AND dep.project = t.project
                    AND dep.build_id = t.build_id
                    AND dep.task_type = 'tests'
                    AND dep.status = 'complete'
              ))
            ORDER BY build_id DESC, task_type ASC
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
