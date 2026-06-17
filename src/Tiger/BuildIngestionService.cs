using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

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
/// Ingests build and test data from AzDO into the SQLite database.
/// Build rows are inserted immediately when discovered by the poller.
/// Detailed data (tests + helix, timeline, pr_info) is processed asynchronously
/// via a background worker loop with retry and circuit breaker logic.
/// </summary>
public sealed class BuildIngestionService : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly ServiceLog? _log;
    private readonly ConcurrentStack<IngestionTask> _priorityTasks = new();
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    private const int MaxAttempts = 5;
    private const int WorkerIntervalSeconds = 5;
    private const int CircuitBreakerThreshold = 5;
    private const int CircuitBreakerCooldownSeconds = 120;
    private const int MaxParallelism = 8;

    /// <summary>
    /// Backoff delays per attempt: 30s, 2min, 10min, 1hr, then abandon.
    /// </summary>
    private static readonly int[] s_backoffSeconds = [30, 120, 600, 3600];

    /// <summary>
    /// Raised when all ingestion tasks for a build have completed (or been abandoned).
    /// </summary>
    public event Action<BuildIngestedEvent>? OnBuildIngested;

    public bool IsRunning => _workerTask is not null && !_workerTask.IsCompleted;

    public BuildIngestionService(TigerDatabase db, AzdoClientFactory clientFactory, ServiceLog? log = null)
    {
        _db = db;
        _clientFactory = clientFactory;
        _log = log;
    }

    /// <summary>
    /// Constructor for callers that only need build insertion (no worker loop).
    /// </summary>
    public BuildIngestionService(TigerDatabase db, ServiceLog? log = null)
        : this(db, null!, log)
    {
    }

    // ── Build Discovery ─────────────────────────────────────────────

    /// <summary>
    /// Inserts build rows and creates ingestion tasks for processing.
    /// </summary>
    public void InsertBuilds(string organization, string project, List<AzdoBuild> builds)
    {
        foreach (var build in builds)
        {
            InsertBuild(organization, project, build);
        }
    }

    /// <summary>
    /// Async wrapper for <see cref="InsertBuilds"/> to satisfy delegate signatures.
    /// </summary>
    public Task InsertBuildsAsync(string organization, string project, List<AzdoBuild> builds)
    {
        InsertBuilds(organization, project, builds);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Inserts a build row and creates its ingestion tasks atomically.
    /// </summary>
    internal void InsertBuild(string organization, string project, AzdoBuild build)
    {
        var result = build.Result ?? "unknown";
        _log?.Info("Ingestion",
            $"#{build.Id} {build.DefinitionName} {build.BuildNumber} [{result}] {build.RepositoryName ?? ""}");

        _db.WithTransaction((conn, tx) =>
        {
            InsertBuildRow(conn, tx, organization, project, build);
            CreateIngestionTasks(conn, tx, organization, build);
        });
    }

    private static void InsertBuildRow(SqliteConnection conn, SqliteTransaction tx,
        string organization, string project, AzdoBuild build)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO builds
                (organization, project, build_id, build_number, definition_name, definition_id,
                 status, result, source_branch, source_version, repository_name, pr_number, finish_time)
            VALUES
                (@org, @proj, @buildId, @buildNumber, @defName, @defId,
                 @status, @result, @branch, @sourceVersion, @repoName, @prNumber, @finishTime)
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@buildId", build.Id);
        cmd.Parameters.AddWithValue("@buildNumber", build.BuildNumber);
        cmd.Parameters.AddWithValue("@defName", build.DefinitionName);
        cmd.Parameters.AddWithValue("@defId", build.DefinitionId);
        cmd.Parameters.AddWithValue("@status", build.Status);
        cmd.Parameters.AddWithValue("@result", (object?)build.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@branch", build.SourceBranch);
        cmd.Parameters.AddWithValue("@sourceVersion", (object?)build.SourceVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@repoName", (object?)build.RepositoryName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prNumber", build.PrNumber.HasValue ? build.PrNumber.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@finishTime", build.FinishTime?.ToString("o") ?? (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates ingestion task rows for a build. Every build must have a task row for
    /// each expected task type. A build is considered fully ingested when all its task
    /// rows have is_complete = 1. See <see cref="NotifyIfBuildFullyIngested"/>.
    ///
    /// Tasks that should be skipped (e.g., timeline for canceled builds) must still
    /// be inserted with is_complete = 1 so they don't block the completion check.
    /// </summary>
    internal void CreateIngestionTasks(SqliteConnection conn, SqliteTransaction tx,
        string organization, AzdoBuild build)
    {
        var taskTypes = new List<string> { "tests", "timeline" };

        // Only create pr_info task if this is a PR build and we don't already have the PR cached
        if (build.PrNumber is not null && build.RepositoryName is not null)
        {
            if (!HasPullRequest(build.RepositoryName, build.PrNumber.Value))
            {
                taskTypes.Add("pr_info");
            }
        }

        // Canceled builds have no useful timeline data — mark it complete immediately
        var isCanceled = string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase);

        foreach (var taskType in taskTypes)
        {
            var isSkipped = isCanceled && taskType == "timeline";
            var status = isSkipped ? "complete" : "pending";
            var isComplete = isSkipped ? 1 : 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO build_ingestion_tasks
                    (organization, build_id, task_type, status, is_complete)
                VALUES
                    (@org, @buildId, @type, @status, @isComplete)
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", build.Id);
            cmd.Parameters.AddWithValue("@type", taskType);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@isComplete", isComplete);
            cmd.ExecuteNonQuery();
        }
    }

    private bool HasPullRequest(string repository, int prNumber)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT 1 FROM pull_requests WHERE repository = @repo AND pr_number = @pr LIMIT 1";
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@pr", prNumber);
            return cmd.ExecuteScalar() is not null;
        });
    }

    // ── Test/Helix Data Insertion ───────────────────────────────────

    internal void InsertTestRun(string organization, string project, int buildId, int runId,
        string runName, int total, int passed, int failed, int skipped, double? durationSeconds = null)
    {
        _db.WithCommand(cmd =>
            InsertTestRun(cmd, organization, project, buildId, runId, runName, total, passed, failed, skipped, durationSeconds));
    }

    internal static void InsertTestRun(SqliteCommand cmd, string organization, string project, int buildId, int runId,
        string runName, int total, int passed, int failed, int skipped, double? durationSeconds = null)
    {
        cmd.CommandText = """
            INSERT OR IGNORE INTO test_runs
                (organization, project, build_id, run_id, run_name, total_tests, passed_tests, failed_tests, skipped_tests, duration_seconds)
            VALUES
                (@org, @proj, @buildId, @runId, @runName, @total, @passed, @failed, @skipped, @duration)
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@runName", runName);
        cmd.Parameters.AddWithValue("@total", total);
        cmd.Parameters.AddWithValue("@passed", passed);
        cmd.Parameters.AddWithValue("@failed", failed);
        cmd.Parameters.AddWithValue("@skipped", skipped);
        cmd.Parameters.AddWithValue("@duration", durationSeconds.HasValue ? (object)durationSeconds.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal void InsertTestResult(string organization, string project, int runId, AzdoTestResult result)
    {
        _db.WithCommand(cmd => InsertTestResult(cmd, organization, project, runId, result));
    }

    internal static void InsertTestResult(SqliteCommand cmd, string organization, string project, int runId, AzdoTestResult result)
    {
        cmd.CommandText = """
            INSERT OR IGNORE INTO test_results
                (organization, project, run_id, result_id, test_case_title, outcome,
                 error_message, stack_trace, helix_job_name, helix_work_item_name, is_helix_work_item)
            VALUES
                (@org, @proj, @runId, @resultId, @title, @outcome,
                 @errorMsg, @stack, @helixJob, @helixWi, @isHelixWi)
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@resultId", result.Id);
        cmd.Parameters.AddWithValue("@title", result.TestCaseTitle);
        cmd.Parameters.AddWithValue("@outcome", result.Outcome);
        cmd.Parameters.AddWithValue("@errorMsg", (object?)result.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stack", (object?)result.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@helixJob", (object?)result.HelixJobName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@helixWi", (object?)result.HelixWorkItemName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isHelixWi", result.IsHelixWorkItem ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    internal void InsertTimelineIssues(string organization, string project, int buildId, AzdoTimeline timeline)
    {
        var recordNames = timeline.Records.ToDictionary(r => r.Id, r => r.Name);

        _db.WithTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            cmd.CommandText = "DELETE FROM build_timeline_issues WHERE organization = @org AND build_id = @buildId";
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();

            foreach (var record in timeline.Records)
            {
                var issues = record.Issues.Where(i => i.Type is "error" or "warning").ToList();
                if (issues.Count == 0)
                {
                    continue;
                }

                var parentName = record.ParentId is not null && recordNames.TryGetValue(record.ParentId, out var pn)
                    ? pn : null;

                foreach (var issue in issues)
                {
                    cmd.CommandText = """
                        INSERT INTO build_timeline_issues
                            (organization, build_id, record_name, record_type,
                             parent_name, record_result, issue_type, issue_message, issue_category, log_url)
                        VALUES
                            (@org, @buildId, @name, @type,
                             @parent, @result, @issueType, @message, @category, @logUrl)
                        """;
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@org", organization);
                    cmd.Parameters.AddWithValue("@buildId", buildId);
                    cmd.Parameters.AddWithValue("@name", record.Name);
                    cmd.Parameters.AddWithValue("@type", record.RecordType);
                    cmd.Parameters.AddWithValue("@parent", (object?)parentName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@result", (object?)record.Result ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@issueType", issue.Type);
                    cmd.Parameters.AddWithValue("@message", issue.Message);
                    cmd.Parameters.AddWithValue("@category", (object?)issue.Category ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@logUrl", (object?)record.LogUrl ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }
        });
    }

    // ── Background Worker ───────────────────────────────────────────

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

    /// <summary>
    /// Pushes all non-complete ingestion tasks for the specified build onto the
    /// priority stack so they are picked up before the normal DB queue.
    /// </summary>
    public void PrioritizeBuild(string organization, int buildId)
    {
        var tasks = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT t.organization, b.project, t.build_id, t.task_type, t.status, t.attempts
                FROM build_ingestion_tasks t
                JOIN builds b ON t.organization = b.organization AND t.build_id = b.build_id
                WHERE t.organization = @org AND t.build_id = @buildId
                  AND t.is_complete = 0
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var result = new List<IngestionTask>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new IngestionTask(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5)));
            }
            return result;
        });

        foreach (var task in tasks)
        {
            _priorityTasks.Push(task);
        }
    }

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

    // ── Task Processing ─────────────────────────────────────────────

    /// <summary>
    /// Processes a single ingestion task and returns it for post-completion handling.
    /// Exceptions are captured so the caller can inspect them.
    /// </summary>
    private async Task<IngestionTask?> RunTaskAsync(IngestionTask task, CancellationToken ct)
    {
        // Tolerate duplicate tasks (e.g. from priority stack) — if already complete, skip
        var isComplete = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT 1 FROM build_ingestion_tasks
                WHERE organization = @org AND build_id = @buildId AND task_type = @type
                  AND is_complete = 1
                """;
            cmd.Parameters.AddWithValue("@org", task.Organization);
            cmd.Parameters.AddWithValue("@buildId", task.BuildId);
            cmd.Parameters.AddWithValue("@type", task.TaskType);
            return cmd.ExecuteScalar() is not null;
        });
        if (isComplete)
        {
            return task;
        }

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

        var (testSummary, failures) = await FetchTestResults();
        var helixWorkItems = await FetchHelixWorkItems();

        // Both fetches succeeded — persist everything in one transaction
        _db.WithTransaction((conn, tx) =>
        {
            InsertTestData(conn, tx);
            InsertHelixData(conn, tx);
        });

        if (failures.Count > 0)
        {
            _log?.Info("Worker",
                $"  Build #{task.BuildId} — {failures.Count} test failure(s) across {failures.GroupBy(f => f.TestRunId).Count()} run(s)");
        }
        else
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — tests complete (no failures)");
        }

        if (helixWorkItems.Count > 0)
        {
            _log?.Info("Worker", $"  Build #{task.BuildId} — helix complete ({helixWorkItems.Count} work item(s) fetched)");
        }

        return;

        async Task<(List<AzdoJobTestSummary> Summary, List<AzdoTestResult> Failures)> FetchTestResults()
        {
            var summary = await client.GetTestSummaryByJobAsync(task.BuildId);
            var results = await client.GetTestFailuresAsync(task.BuildId, subResultCount: 50);
            return (summary, results);
        }

        async Task<List<HelixWorkItem>> FetchHelixWorkItems()
        {
            // Extract helix job/work-item pairs directly from the fetched test results
            var workItemKeys = failures
                .Where(f => f.HelixJobName is not null && f.HelixWorkItemName is not null)
                .Select(f => (f.HelixJobName!, f.HelixWorkItemName!))
                .Distinct()
                .ToList();

            if (workItemKeys.Count == 0)
            {
                return [];
            }

            _log?.Info("Worker", $"Fetching helix work items for build #{task.BuildId}...");

            var helixClient = HelixClient.Create();
            var items = new List<HelixWorkItem>();

            foreach (var (jobName, workItemName) in workItemKeys)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Skip if already fetched in a previous ingestion
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
                    items.Add(workItem);
                }
                catch (HttpRequestException ex)
                {
                    _log?.Warning("Worker", $"  Failed to fetch helix work item {jobName}/{workItemName}: {ex.Message}");
                }
            }

            return items;
        }

        void InsertTestData(SqliteConnection conn, SqliteTransaction tx)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            // Insert test runs for ALL runs from the summary
            foreach (var summary in testSummary)
            {
                InsertTestRun(cmd, task.Organization, task.Project, task.BuildId,
                    summary.RunId, summary.JobName, summary.TotalCount, summary.PassedCount,
                    summary.FailedCount, summary.SkippedCount, summary.Duration?.TotalSeconds);
            }

            // Insert individual failure results
            var runGroups = failures.GroupBy(f => f.TestRunId);
            foreach (var group in runGroups)
            {
                var first = group.First();

                // If this run wasn't in the summary (unlikely but defensive), insert it now
                if (!testSummary.Any(s => s.RunId == group.Key))
                {
                    InsertTestRun(cmd, task.Organization, task.Project, task.BuildId,
                        group.Key, first.TestRunName, group.Count(), 0, group.Count(), 0);
                }

                foreach (var r in group)
                {
                    InsertTestResult(cmd, task.Organization, task.Project, group.Key, r);
                }
            }
        }

        void InsertHelixData(SqliteConnection conn, SqliteTransaction tx)
        {
            foreach (var workItem in helixWorkItems)
            {
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

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT OR IGNORE INTO helix_work_items
                            (job_name, work_item_name, state, exit_code, console_output_uri, files, is_deadletter)
                        VALUES
                            (@job, @wi, @state, @exitCode, @consoleUri, @files, @isDeadletter)
                        """;
                    cmd.Parameters.AddWithValue("@job", workItem.Job);
                    cmd.Parameters.AddWithValue("@wi", workItem.Name);
                    cmd.Parameters.AddWithValue("@state", workItem.State);
                    cmd.Parameters.AddWithValue("@exitCode", workItem.ExitCode.HasValue ? workItem.ExitCode.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@consoleUri", (object?)workItem.ConsoleOutputUri ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@isDeadletter", workItem.IsDeadLetter ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }

                if (workItem.IsDeadLetter)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        UPDATE test_results
                        SET error_message = 'Helix Work Item Dead Lettered. ' || COALESCE(error_message, '')
                        WHERE is_helix_work_item = 1
                          AND helix_job_name = @job
                          AND helix_work_item_name = @wi
                          AND organization = @org
                          AND run_id IN (
                              SELECT run_id FROM test_runs
                              WHERE organization = @org AND build_id = @buildId
                          )
                          AND error_message NOT LIKE 'Helix Work Item Dead Lettered.%'
                        """;
                    cmd.Parameters.AddWithValue("@job", workItem.Job);
                    cmd.Parameters.AddWithValue("@wi", workItem.Name);
                    cmd.Parameters.AddWithValue("@org", task.Organization);
                    cmd.Parameters.AddWithValue("@buildId", task.BuildId);
                    cmd.ExecuteNonQuery();
                    _log?.Warning("Worker", $"  Helix work item {workItem.Name} is dead-lettered");
                }
            }
        }
    }

    private async Task ProcessTimelineAsync(AzdoClient client, IngestionTask task, CancellationToken ct)
    {
        _log?.Info("Worker", $"Fetching timeline for build #{task.BuildId}...");
        var timeline = await client.GetTimelineAsync(task.BuildId);
        InsertTimelineIssues(task.Organization, task.Project, task.BuildId, timeline);

        var issueCount = timeline.Records.Sum(r => r.Issues.Count(i => i.Type is "error" or "warning"));
        _log?.Info("Worker", $"  Build #{task.BuildId} — timeline complete ({issueCount} issues)");
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

    // ── DB Helpers ──────────────────────────────────────────────────

    private IngestionTask? GetNextReadyTask()
    {
        // Priority tasks take precedence over the normal DB query
        if (_priorityTasks.TryPop(out var priority))
        {
            return priority;
        }

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
    /// See <see cref="CreateIngestionTasks"/> for task creation.
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
