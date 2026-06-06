namespace Tiger;

/// <summary>
/// Information about a build whose ingestion has completed.
/// </summary>
public sealed class BuildIngestedEvent
{
    public required string Organization { get; init; }
    public required string Project { get; init; }
    public required int BuildId { get; init; }
    public required string DefinitionName { get; init; }
    public required string Result { get; init; }
    public required string SourceBranch { get; init; }
    public string? FinishTime { get; init; }
}

/// <summary>
/// Background worker that processes build ingestion tasks (tests, timeline, helix).
/// Picks up pending/failed tasks from the DB and processes them independently.
/// Uses exponential backoff for retries and a circuit breaker to avoid hammering
/// a broken API.
/// </summary>
public sealed class TaskIngestionService : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly BuildIngestionService _ingestion;
    private readonly AzdoClientFactory _clientFactory;
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

    /// <summary>
    /// Raised when all ingestion tasks for a build have completed (or been abandoned).
    /// </summary>
    public event Action<BuildIngestedEvent>? OnBuildIngested;

    public bool IsRunning => _workerTask is not null && !_workerTask.IsCompleted;

    public TaskIngestionService(
        TigerDatabase db,
        BuildIngestionService ingestion,
        AzdoClientFactory clientFactory,
        ServiceLog? log = null)
    {
        _db = db;
        _ingestion = ingestion;
        _clientFactory = clientFactory;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

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
                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    private const int MaxParallelism = 8;

    /// <summary>
    /// Maintains up to <see cref="MaxParallelism"/> in-flight tasks at all times,
    /// adapting concurrency based on AzDO rate-limit headers.
    /// When any task completes, its result is handled immediately and a new task
    /// is claimed to fill the slot — no waiting for an entire batch to drain.
    /// </summary>
    private async Task WorkLoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var inFlight = new List<Task<IngestionTask?>>(MaxParallelism);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Circuit breaker
                if (consecutiveFailures >= CircuitBreakerThreshold)
                {
                    _log?.Warning("Worker",
                        $"Circuit breaker: {consecutiveFailures} consecutive failures, cooling down {CircuitBreakerCooldownSeconds}s");

                    // Drain in-flight work before cooling down
                    while (inFlight.Count > 0)
                    {
                        var done = await Task.WhenAny(inFlight);
                        inFlight.Remove(done);
                        HandleCompletion(done);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds), ct);
                    consecutiveFailures = 0;
                    continue;
                }

                // Fill available slots up to effective parallelism
                var effectiveParallelism = GetEffectiveParallelism();
                while (inFlight.Count < effectiveParallelism)
                {
                    var task = GetNextReadyTask();
                    if (task is null)
                    {
                        break;
                    }

                    inFlight.Add(RunTaskAsync(task, ct));
                }

                if (inFlight.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(WorkerIntervalSeconds), ct);
                    continue;
                }

                // Wait for any one task to complete, then loop to refill the slot
                var completed = await Task.WhenAny(inFlight);
                inFlight.Remove(completed);
                HandleCompletion(completed);
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

        // Drain remaining in-flight tasks on shutdown
        foreach (var remaining in inFlight)
        {
            try
            {
                await remaining;
            }
            catch
            {
            }
        }

        void HandleCompletion(Task<IngestionTask?> task)
        {
            if (task.IsFaulted || task.Result is null)
            {
                consecutiveFailures++;
            }
            else
            {
                consecutiveFailures = 0;
            }
        }
    }

    /// <summary>
    /// Computes how many tasks to run concurrently based on AzDO rate-limit state.
    /// Checks all known organizations and uses the most constrained one.
    /// </summary>
    private int GetEffectiveParallelism()
    {
        var fraction = GetMinRemainingFraction();

        return fraction switch
        {
            > 0.5 => MaxParallelism,
            > 0.25 => MaxParallelism / 2,
            > 0.1 => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Returns the lowest <see cref="AzdoRateLimitState.RemainingFraction"/> across all
    /// organizations, representing the most constrained org. Returns 1.0 if no
    /// rate-limit data has been received yet.
    /// </summary>
    private double GetMinRemainingFraction()
    {
        var min = 1.0;
        foreach (var state in _clientFactory.GetAllRateLimitStates())
        {
            if (state.ShouldDelay)
            {
                return 0;
            }

            min = Math.Min(min, state.RemainingFraction);
        }

        return min;
    }

    /// <summary>
    /// Processes a single ingestion task and returns it for post-completion handling.
    /// Exceptions are captured so the caller can inspect them.
    /// </summary>
    private async Task<IngestionTask?> RunTaskAsync(IngestionTask task, CancellationToken ct)
    {
        try
        {
            MarkRunning();
            await ProcessTaskAsync(task, ct);
            MarkComplete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var newAttempts = task.Attempts + 1;
            if (newAttempts >= MaxAttempts)
            {
                MarkAbandoned(ex.Message);
                _log?.Error("Worker",
                    $"Task {task.TaskType} for build #{task.BuildId} abandoned after {newAttempts} attempts: {ex.Message}");
            }
            else
            {
                var backoffIndex = Math.Min(newAttempts - 1, s_backoffSeconds.Length - 1);
                var delaySecs = s_backoffSeconds[backoffIndex];
                MarkFailed(ex.Message, delaySecs);
                _log?.Warning("Worker",
                    $"Task {task.TaskType} for build #{task.BuildId} failed (attempt {newAttempts}), retry in {delaySecs}s: {ex.Message}");
            }

            return null; // signals failure
        }
        finally
        {
            NotifyIfBuildFullyIngested(task.Organization, task.BuildId);
        }

        return task; // signals success

        void MarkRunning()
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE build_ingestion_tasks
                    SET status = 'running', last_attempt_time = datetime('now')
                    WHERE organization = @org AND build_id = @buildId AND task_type = @type
                    """;
                cmd.Parameters.AddWithValue("@org", task.Organization);
                cmd.Parameters.AddWithValue("@buildId", task.BuildId);
                cmd.Parameters.AddWithValue("@type", task.TaskType);
                cmd.ExecuteNonQuery();
            });
        }

        void MarkComplete()
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE build_ingestion_tasks
                    SET status = 'complete', is_complete = 1, completed_time = datetime('now'), last_error = NULL
                    WHERE organization = @org AND build_id = @buildId AND task_type = @type
                    """;
                cmd.Parameters.AddWithValue("@org", task.Organization);
                cmd.Parameters.AddWithValue("@buildId", task.BuildId);
                cmd.Parameters.AddWithValue("@type", task.TaskType);
                cmd.ExecuteNonQuery();
            });
        }

        void MarkFailed(string error, int retryDelaySecs)
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = $"""
                    UPDATE build_ingestion_tasks
                    SET status = 'failed',
                        attempts = attempts + 1,
                        last_error = @error,
                        last_attempt_time = datetime('now'),
                        next_retry_time = datetime('now', '+{retryDelaySecs} seconds')
                    WHERE organization = @org AND build_id = @buildId AND task_type = @type
                    """;
                cmd.Parameters.AddWithValue("@org", task.Organization);
                cmd.Parameters.AddWithValue("@buildId", task.BuildId);
                cmd.Parameters.AddWithValue("@type", task.TaskType);
                cmd.Parameters.AddWithValue("@error", error);
                cmd.ExecuteNonQuery();
            });
        }

        void MarkAbandoned(string error)
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE build_ingestion_tasks
                    SET status = 'abandoned',
                        is_complete = 1,
                        attempts = attempts + 1,
                        last_error = @error,
                        last_attempt_time = datetime('now')
                    WHERE organization = @org AND build_id = @buildId AND task_type = @type
                    """;
                cmd.Parameters.AddWithValue("@org", task.Organization);
                cmd.Parameters.AddWithValue("@buildId", task.BuildId);
                cmd.Parameters.AddWithValue("@type", task.TaskType);
                cmd.Parameters.AddWithValue("@error", error);
                cmd.ExecuteNonQuery();
            });
        }
    }

    private async Task ProcessTaskAsync(IngestionTask task, CancellationToken ct)
    {
        var client = _clientFactory.Create(task.Organization, task.Project);

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

        // Insert test runs for ALL runs from the summary, not just those with failures
        foreach (var summary in testSummary)
        {
            _ingestion.InsertTestRun(task.Organization, task.Project, task.BuildId, summary.RunId, summary.JobName,
                summary.TotalCount, summary.PassedCount, summary.FailedCount, summary.SkippedCount,
                summary.Duration?.TotalSeconds);
        }

        // Insert individual failure results
        var runGroups = failures.GroupBy(f => f.TestRunId);
        foreach (var group in runGroups)
        {
            var first = group.First();

            // If this run wasn't in the summary (unlikely but defensive), insert it now
            if (!testSummary.Any(s => s.RunId == group.Key))
            {
                _ingestion.InsertTestRun(task.Organization, task.Project, task.BuildId, group.Key, first.TestRunName,
                    group.Count(), 0, group.Count(), 0);
            }

            foreach (var r in group)
            {
                _ingestion.InsertTestResult(task.Organization, task.Project, group.Key, r);
            }
        }

        if (failures.Count > 0)
        {
            _log?.Info("Worker",
                $"  Build #{task.BuildId} — {failures.Count} test failure(s) across {runGroups.Count()} run(s)");
        }
        else
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — tests complete (no failures)");
        }

        // Unblock the helix task now that test data is available.
        // If there are helix work items to fetch, move it to 'pending'.
        // Otherwise mark it complete immediately.
        UnblockHelixTask(task.Organization, task.BuildId);
    }

    /// <summary>
    /// Transitions the helix ingestion task from 'blocked' to either 'pending'
    /// (if there are helix work items to fetch) or 'complete' (if there are none).
    /// Called after test ingestion finishes so the helix task can see test_results.
    /// </summary>
    private void UnblockHelixTask(string organization, int buildId)
    {
        var hasHelixWorkItems = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT 1
                FROM test_results tr
                JOIN test_runs trn ON tr.organization = trn.organization AND tr.run_id = trn.run_id
                WHERE trn.organization = @org AND trn.build_id = @buildId
                  AND tr.helix_job_name IS NOT NULL AND tr.helix_work_item_name IS NOT NULL
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteScalar() is not null;
        });

        if (hasHelixWorkItems)
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE build_ingestion_tasks
                    SET status = 'pending'
                    WHERE organization = @org AND build_id = @buildId
                      AND task_type = 'helix' AND status = 'blocked'
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            });
        }
        else
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE build_ingestion_tasks
                    SET status = 'complete', is_complete = 1, completed_time = datetime('now')
                    WHERE organization = @org AND build_id = @buildId
                      AND task_type = 'helix' AND status = 'blocked'
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            });
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
        var workItemKeys = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT DISTINCT tr.helix_job_name, tr.helix_work_item_name
                FROM test_results tr
                JOIN test_runs trn ON tr.organization = trn.organization AND tr.run_id = trn.run_id
                WHERE trn.organization = @org AND trn.build_id = @buildId
                  AND tr.helix_job_name IS NOT NULL AND tr.helix_work_item_name IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("@org", task.Organization);
            cmd.Parameters.AddWithValue("@buildId", task.BuildId);

            var workItemKeys = new List<(string JobName, string WorkItemName)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                workItemKeys.Add((reader.GetString(0), reader.GetString(1)));
            }

            return workItemKeys;
        });

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
            {
                break;
            }

            // Skip if already fetched
            var exists = _db.WithCommand(cmd =>
            {
                cmd.CommandText = "SELECT 1 FROM helix_work_items WHERE job_name = @job AND work_item_name = @wi";
                cmd.Parameters.AddWithValue("@job", jobName);
                cmd.Parameters.AddWithValue("@wi", workItemName);
                return cmd.ExecuteScalar() is not null;
            });

            if (exists)
            {
                continue;
            }

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

                _db.WithCommand(cmd =>
                {
                    cmd.CommandText = """
                        INSERT OR IGNORE INTO helix_work_items
                            (job_name, work_item_name, state, exit_code, console_output_uri, files)
                        VALUES
                            (@job, @wi, @state, @exitCode, @consoleUri, @files)
                        """;
                    cmd.Parameters.AddWithValue("@job", workItem.Job);
                    cmd.Parameters.AddWithValue("@wi", workItem.Name);
                    cmd.Parameters.AddWithValue("@state", workItem.State);
                    cmd.Parameters.AddWithValue("@exitCode", workItem.ExitCode.HasValue ? workItem.ExitCode.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@consoleUri", (object?)workItem.ConsoleOutputUri ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                });
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
        var prInfo = _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT pr_number, repository_name FROM builds WHERE organization = @org AND build_id = @buildId";
            cmd.Parameters.AddWithValue("@org", task.Organization);
            cmd.Parameters.AddWithValue("@buildId", task.BuildId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return ((int PrNumber, string Repository)?)null;
            }

            return (reader.GetInt32(0), reader.GetString(1));
        });

        if (prInfo is null)
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — no PR info to fetch");
            return;
        }

        var (prNumber, repository) = prInfo.Value;

        // Check if we already have this PR cached
        var exists = _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT 1 FROM pull_requests WHERE repository = @repo AND pr_number = @pr";
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@pr", prNumber);
            return cmd.ExecuteScalar() is not null;
        });
        if (exists)
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

            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO pull_requests (repository, pr_number, title, author)
                    VALUES (@repo, @pr, @title, @author)
                    """;
                cmd.Parameters.AddWithValue("@repo", repository);
                cmd.Parameters.AddWithValue("@pr", prNumber);
                cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@author", (object?)author ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            });

            _log?.Info("Worker", $"  Build #{task.BuildId} — PR #{prNumber} info cached ({author})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("Worker", $"  Build #{task.BuildId} — PR info fetch failed: {ex.Message}");
        }
    }

    // ── DB helpers ──────────────────────────────────────────────────

    private IngestionTask? GetNextReadyTask()
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT t.organization, b.project, t.build_id, t.task_type, t.status, t.attempts
                FROM build_ingestion_tasks t
                JOIN builds b ON t.organization = b.organization AND t.build_id = b.build_id
                WHERE t.is_complete = 0
                  AND t.status IN ('pending', 'failed')
                  AND (t.next_retry_time IS NULL OR t.next_retry_time <= datetime('now'))
                ORDER BY t.build_id DESC, t.task_type ASC
                LIMIT 1
                """;

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new IngestionTask(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5));
            }

            return null;
        });
    }

    /// <summary>
    /// After a task completes, check if all tasks for the build are terminal.
    /// If so, raise the OnBuildIngested event.
    ///
    /// Invariant: a build is fully ingested when every row in build_ingestion_tasks
    /// for that (organization, build_id) has is_complete = 1.
    /// Tasks that should be skipped (e.g., timeline for canceled builds) are
    /// inserted with is_complete = 1 at creation time so they don't block this check.
    /// See <see cref="BuildIngestionService.CreateIngestionTasks"/> for task creation.
    /// </summary>
    private void NotifyIfBuildFullyIngested(string organization, int buildId)
    {
        var allDone = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT 1
                WHERE NOT EXISTS (
                    SELECT 1 FROM build_ingestion_tasks t
                    WHERE t.organization = @org AND t.build_id = @buildId
                      AND t.is_complete = 0
                )
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            return reader.Read();
        });

        if (!allDone)
        {
            return;
        }

        // Mark the build itself as fully ingested
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                UPDATE builds SET ingestion_tasks_complete = 1
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        });

        _log?.Info("Worker", $"Build #{buildId} fully ingested, notifying subscribers.");

        var buildEvent = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT project, definition_name, result, source_branch, finish_time
                FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new BuildIngestedEvent
            {
                Organization = organization,
                Project = reader.GetString(0),
                BuildId = buildId,
                DefinitionName = reader.GetString(1),
                Result = reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                SourceBranch = reader.GetString(3),
                FinishTime = reader.IsDBNull(4) ? null : reader.GetString(4),
            };
        });

        if (buildEvent is not null)
        {
            OnBuildIngested?.Invoke(buildEvent);
        }
    }

    /// <summary>
    /// Resets all abandoned tasks to pending for retry.
    /// </summary>
    public int RetryAbandoned()
    {
        return _db.WithCommand(cmd =>
        {
            // Reset the ingestion_tasks_complete flag on affected builds
            cmd.CommandText = """
                UPDATE builds SET ingestion_tasks_complete = 0
                WHERE (organization, build_id) IN (
                    SELECT organization, build_id FROM build_ingestion_tasks
                    WHERE status = 'abandoned'
                )
                """;
            cmd.ExecuteNonQuery();

            cmd.CommandText = """
                UPDATE build_ingestion_tasks
                SET status = 'pending', is_complete = 0, attempts = 0, last_error = NULL, next_retry_time = NULL
                WHERE status = 'abandoned'
                """;
            return cmd.ExecuteNonQuery();
        });
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
