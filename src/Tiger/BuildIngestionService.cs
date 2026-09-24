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
/// The detailed data for a build — tests, helix work items, and timeline — is
/// fetched and written in a single pass: either the whole build's detailed data
/// lands in the database in one transaction, or none of it does. There is no
/// per-piece task tracking; a build's progress is a single
/// <c>builds.ingestion_status</c> column ("pending", "running", "complete",
/// "failed", or "abandoned").
/// </summary>
public sealed class BuildIngestionService : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly Func<HelixClient> _helixClientFactory;
    private readonly ServiceLog? _log;
    private readonly Queue<(string Organization, int BuildId)> _priorityBuilds = new();
    private readonly SemaphoreSlim _prioritySignal = new(0);
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    private const int MaxAttempts = 5;
    private const int WorkerIntervalSeconds = 5;
    private const int QueueReportIntervalSeconds = 10;
    private const int CircuitBreakerThreshold = 5;
    private const int CircuitBreakerCooldownSeconds = 120;
    private const int DefaultMaxParallelism = 8;

    private readonly int _maxParallelism;

    /// <summary>
    /// Backoff delays per attempt: 30s, 2min, 10min, 1hr, then abandon.
    /// </summary>
    private static readonly int[] s_backoffSeconds = [30, 120, 600, 3600];

    /// <summary>
    /// Raised when a build's detailed data has been fully committed to the database.
    /// </summary>
    public event Action<BuildIngestedEvent>? OnBuildIngested;

    public bool IsRunning => _workerTask is not null && !_workerTask.IsCompleted;

    public BuildIngestionService(TigerDatabase db, AzdoClientFactory clientFactory, ServiceLog? log = null, int maxParallelism = DefaultMaxParallelism, Func<HelixClient>? helixClientFactory = null)
    {
        _db = db;
        _clientFactory = clientFactory;
        _log = log;
        _maxParallelism = maxParallelism;
        _helixClientFactory = helixClientFactory ?? (() => HelixClient.Create());
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
    /// Inserts build rows so they can be picked up for detailed ingestion.
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
    /// Inserts a build row. Canceled builds have no useful test/timeline data,
    /// so they're marked ingestion-complete immediately rather than queued for
    /// a network fetch that would come back empty.
    /// </summary>
    internal void InsertBuild(string organization, string project, AzdoBuild build)
    {
        var result = build.Result ?? "unknown";
        _log?.Info("Ingestion",
            $"#{build.Id} {build.DefinitionName} {build.BuildNumber} [{result}] {build.RepositoryName ?? ""}");

        var isCanceled = string.Equals(build.Result, "canceled", StringComparison.OrdinalIgnoreCase);
        var initialStatus = isCanceled ? "complete" : "pending";

        _db.WithCommand(cmd => InsertBuildRow(cmd, organization, project, build, initialStatus));
    }

    private static void InsertBuildRow(SqliteCommand cmd,
        string organization, string project, AzdoBuild build, string initialStatus)
    {
        // Existing rows are left untouched — re-discovering a build (e.g. during a
        // backfill re-run) must never reset an already-completed ingestion status.
        cmd.CommandText = """
            INSERT OR IGNORE INTO builds
                (organization, project, build_id, build_number, definition_name, definition_id,
                 status, result, source_branch, source_version, repository_name, repository_type,
                 pr_number, finish_time, ingestion_status)
            VALUES
                (@org, @proj, @buildId, @buildNumber, @defName, @defId,
                 @status, @result, @branch, @sourceVersion, @repoName, @repoType,
                 @prNumber, @finishTime, @ingestionStatus)
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
        cmd.Parameters.AddWithValue("@repoType", (object?)build.RepositoryType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@prNumber", build.PrNumber.HasValue ? build.PrNumber.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@finishTime", build.FinishTime?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ingestionStatus", initialStatus);
        cmd.ExecuteNonQuery();
    }

    // ── Test/Helix/Timeline Data Insertion ──────────────────────────
    // These are also used directly by tests and by the single-pass ingestion below.

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

    internal void InsertTimelineIssues(string organization, string project, int buildId, AzdoTimeline timeline) =>
        _db.WithTransaction((conn, tx) => InsertTimelineIssues(conn, tx, organization, buildId, timeline));

    private static void InsertTimelineIssues(SqliteConnection conn, SqliteTransaction tx,
        string organization, int buildId, AzdoTimeline timeline)
    {
        var recordNames = timeline.Records.ToDictionary(r => r.Id, r => r.Name);

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
    }

    // ── Background Worker ───────────────────────────────────────────

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        // Any build left 'running' from a previous process (e.g. it was killed
        // mid-ingestion) is orphaned — no worker owns it anymore. Reset it to
        // 'pending' so it's picked up and retried normally.
        ReclaimOrphanedRunningBuilds();

        _cts = new CancellationTokenSource();
        _workerTask = WorkLoopAsync(_cts.Token);
    }

    private int ReclaimOrphanedRunningBuilds()
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                UPDATE builds
                SET ingestion_status = 'pending'
                WHERE ingestion_status = 'running'
                """;
            return cmd.ExecuteNonQuery();
        });
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
    /// Moves a build to the front of the ingestion queue. Since a build's detailed
    /// data is now fetched and written in a single pass, there's nothing to preempt
    /// mid-flight — this simply makes the build the next one picked up once a
    /// worker slot frees up. If the build is already 'running' (or has already
    /// completed), <see cref="TryClaimBuild"/> will simply decline the claim when
    /// this entry is popped, so the in-progress attempt is left alone.
    /// </summary>
    public void PrioritizeBuild(string organization, int buildId)
    {
        lock (_priorityBuilds)
        {
            _priorityBuilds.Enqueue((organization, buildId));
        }
        _prioritySignal.Release();
    }

    private sealed record InFlightBuild(string Organization, int BuildId, Task<bool> Task);

    internal void ReportQueueStatus(DateTime utcNow, ref DateTime lastQueueReportTime)
    {
        if (_log is null ||
            utcNow - lastQueueReportTime < TimeSpan.FromSeconds(QueueReportIntervalSeconds))
        {
            return;
        }

        var status = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT
                    COUNT(CASE WHEN ingestion_status = 'pending' THEN 1 END),
                    COUNT(CASE WHEN ingestion_status = 'running' THEN 1 END),
                    COUNT(CASE WHEN ingestion_status = 'failed' THEN 1 END),
                    COUNT(CASE WHEN ingestion_status = 'abandoned' THEN 1 END)
                FROM builds
                WHERE ingestion_status IN ('pending', 'running', 'failed', 'abandoned')
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (Pending: reader.GetInt64(0), Running: reader.GetInt64(1),
                AwaitingRetry: reader.GetInt64(2), Abandoned: reader.GetInt64(3));
        });

        _log.Info("Worker",
            $"Ingestion queue: {status.Pending} pending, {status.Running} running, {status.AwaitingRetry} awaiting retry, {status.Abandoned} abandoned");
        lastQueueReportTime = utcNow;
    }

    /// <summary>
    /// Maintains up to <see cref="_maxParallelism"/> in-flight builds at all times,
    /// adapting concurrency based on AzDO rate-limit headers. When any build
    /// completes, its result is handled immediately and a new build is claimed to
    /// fill the slot.
    /// </summary>
    private async Task WorkLoopAsync(CancellationToken ct)
    {
        DateTime lastQueueReportTime = default;
        var consecutiveFailures = 0;
        var inFlight = new List<InFlightBuild>(_maxParallelism);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Circuit breaker
                if (consecutiveFailures >= CircuitBreakerThreshold)
                {
                    ReportQueueStatus(DateTime.UtcNow, ref lastQueueReportTime);
                    _log?.Warning("Worker",
                        $"Circuit breaker: {consecutiveFailures} consecutive failures, cooling down {CircuitBreakerCooldownSeconds}s");

                    while (inFlight.Count > 0)
                    {
                        var done = await Task.WhenAny(inFlight.Select(f => (Task)f.Task));
                        var completed = inFlight.First(f => (Task)f.Task == done);
                        inFlight.Remove(completed);
                        HandleCompletion(completed.Task);
                    }

                    ReportQueueStatus(DateTime.UtcNow, ref lastQueueReportTime);
                    await Task.Delay(TimeSpan.FromSeconds(CircuitBreakerCooldownSeconds), ct);
                    consecutiveFailures = 0;
                    continue;
                }

                // Fill available slots up to effective parallelism
                var effectiveParallelism = GetEffectiveParallelism();
                while (inFlight.Count < effectiveParallelism)
                {
                    var next = GetNextReadyBuild();
                    if (next is null)
                    {
                        break;
                    }

                    var (organization, buildId) = next.Value;
                    inFlight.Add(new InFlightBuild(organization, buildId, IngestBuildSafeAsync(organization, buildId, ct)));
                }

                ReportQueueStatus(DateTime.UtcNow, ref lastQueueReportTime);

                if (inFlight.Count == 0)
                {
                    await WaitForWorkOrSignal(ct);
                    continue;
                }

                // Wait for any one build to complete OR a priority signal to arrive
                var signalTask = _prioritySignal.WaitAsync(ct);
                var tasksToAwait = new List<Task>(inFlight.Count + 1);
                tasksToAwait.AddRange(inFlight.Select(f => (Task)f.Task));
                tasksToAwait.Add(signalTask);
                var completedTask = await Task.WhenAny(tasksToAwait);

                // If the priority signal fired, loop back to pick up the priority build
                if (completedTask == signalTask)
                {
                    continue;
                }

                var completedFlight = inFlight.First(f => (Task)f.Task == completedTask);
                inFlight.Remove(completedFlight);
                HandleCompletion(completedFlight.Task);
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

        // Drain remaining in-flight builds on shutdown
        foreach (var flight in inFlight)
        {
            try
            {
                await flight.Task;
            }
            catch
            {
            }
        }

        void HandleCompletion(Task<bool> task)
        {
            if (task.IsFaulted || task.IsCanceled || task.Result == false)
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
    /// Waits for either the standard poll interval to elapse or a priority signal
    /// to arrive, whichever comes first.
    /// </summary>
    private async Task WaitForWorkOrSignal(CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(WorkerIntervalSeconds), delayCts.Token);
        var signalTask = _prioritySignal.WaitAsync(ct);

        await Task.WhenAny(delayTask, signalTask);
        await delayCts.CancelAsync();
    }

    /// <summary>
    /// Computes how many builds to ingest concurrently based on AzDO rate-limit state.
    /// Checks all known organizations and uses the most constrained one.
    /// </summary>
    private int GetEffectiveParallelism()
    {
        var fraction = GetMinRemainingFraction();

        return fraction switch
        {
            > 0.5 => _maxParallelism,
            > 0.25 => _maxParallelism / 2,
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
    /// Picks the next build ready for ingestion and atomically claims it by
    /// flipping its `ingestion_status` to 'running' in the database. Priority
    /// builds are tried first, then the highest build ID among pending builds
    /// or failed builds whose retry delay has elapsed.
    ///
    /// Claiming happens in the database (not just this process's in-memory
    /// state) so a build already being ingested — whether picked up normally or
    /// via <see cref="PrioritizeBuild"/> — can never be claimed a second time.
    /// A build dequeued from the priority queue that fails to claim (because it's
    /// already 'running' or has since completed) is simply skipped.
    /// </summary>
    private (string Organization, int BuildId)? GetNextReadyBuild()
    {
        lock (_priorityBuilds)
        {
            while (_priorityBuilds.Count > 0)
            {
                var candidate = _priorityBuilds.Dequeue();
                if (TryClaimBuild(candidate.Organization, candidate.BuildId))
                {
                    return candidate;
                }
            }
        }

        var candidates = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT organization, build_id
                FROM builds
                WHERE ingestion_status = 'pending'
                   OR (ingestion_status = 'failed'
                       AND (ingestion_next_retry_time IS NULL OR ingestion_next_retry_time <= datetime('now')))
                ORDER BY build_id DESC
                LIMIT 50
                """;

            var result = new List<(string, int)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetInt32(1)));
            }
            return result;
        });

        foreach (var (organization, buildId) in candidates)
        {
            if (TryClaimBuild(organization, buildId))
            {
                return (organization, buildId);
            }
        }

        return null;
    }

    /// <summary>
    /// Atomically transitions a build from 'pending'/'failed' to 'running',
    /// returning true only if this call is the one that made the transition.
    /// This is the single point of contention control: it's what guarantees a
    /// build is never ingested by two callers at once, regardless of whether
    /// they raced through the priority stack or the normal poll query.
    /// </summary>
    private bool TryClaimBuild(string organization, int buildId)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                UPDATE builds
                SET ingestion_status = 'running'
                WHERE organization = @org AND build_id = @buildId
                  AND (ingestion_status = 'pending'
                       OR (ingestion_status = 'failed'
                           AND (ingestion_next_retry_time IS NULL OR ingestion_next_retry_time <= datetime('now'))))
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteNonQuery() == 1;
        });
    }

    // ── Single-Pass Build Ingestion ──────────────────────────────────

    /// <summary>
    /// Ingests a single build's detailed data, handling failure bookkeeping
    /// (backoff/abandonment) on the way out. Returns true on success.
    /// </summary>
    private async Task<bool> IngestBuildSafeAsync(string organization, int buildId, CancellationToken ct)
    {
        var buildInfo = GetBuildIngestionInfo(organization, buildId);
        if (buildInfo is null || buildInfo.Value.IngestionStatus == "complete")
        {
            // Nothing to do — already ingested, or the build was deleted underneath us.
            return true;
        }

        try
        {
            await IngestBuildAsync(organization, buildInfo.Value.Project, buildId, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HandleIngestionFailure(organization, buildId, ex);
            return false;
        }
    }

    private (string Project, string IngestionStatus)? GetBuildIngestionInfo(string organization, int buildId)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT project, ingestion_status FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return ((string, string)?)null;
            }
            return (reader.GetString(0), reader.GetString(1));
        });
    }

    /// <summary>
    /// Fetches tests, helix work items, and the timeline for a build, then writes
    /// all of it to the database in a single transaction — either everything lands
    /// or nothing does. PR info is fetched afterward on a best-effort basis; it's
    /// non-essential metadata from a different system (GitHub) and doesn't block
    /// or roll back the rest of the build's ingestion.
    /// </summary>
    private async Task IngestBuildAsync(string organization, string project, int buildId, CancellationToken ct)
    {
        _log?.Info("Worker", $"Ingesting build #{buildId}...");

        var client = _clientFactory.Create(organization, project);

        var summary = await client.GetTestSummaryByJobAsync(buildId, ct);
        var failures = await client.GetTestFailuresAsync(buildId, subResultCount: 50, ct: ct);
        var helixWorkItems = await FetchHelixWorkItemsAsync(_db, _helixClientFactory, _log, failures, ct);
        var timeline = await client.GetTimelineAsync(buildId, ct);

        _db.WithTransaction((conn, tx) =>
        {
            InsertTestsData(conn, tx, organization, project, buildId, summary, failures, helixWorkItems);
            InsertTimelineIssues(conn, tx, organization, buildId, timeline);
            MarkIngestionComplete(conn, tx, organization, buildId);
        });

        var issueCount = timeline.Records.Sum(r => r.Issues.Count(i => i.Type is "error" or "warning"));
        _log?.Info("Worker",
            $"  Build #{buildId} — ingested ({failures.Count} test failure(s), {issueCount} timeline issue(s), {helixWorkItems.Count} helix work item(s))");

        await TryFetchPrInfoAsync(organization, buildId, ct);

        RaiseBuildIngested(organization, buildId);
    }

    private static async Task<List<HelixWorkItem>> FetchHelixWorkItemsAsync(
        TigerDatabase db, Func<HelixClient> helixClientFactory, ServiceLog? log,
        List<AzdoTestResult> failures, CancellationToken ct)
    {
        var workItemKeys = failures
            .Where(f => f.HelixJobName is not null && f.HelixWorkItemName is not null)
            .Select(f => (f.HelixJobName!, f.HelixWorkItemName!))
            .Distinct()
            .ToList();

        var helixWorkItems = new List<HelixWorkItem>();
        if (workItemKeys.Count == 0)
        {
            return helixWorkItems;
        }

        var helixClient = helixClientFactory();

        foreach (var (jobName, workItemName) in workItemKeys)
        {
            ct.ThrowIfCancellationRequested();

            var exists = db.WithCommand(cmd =>
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
                var workItem = await helixClient.GetWorkItemAsync(jobName, workItemName, ct);
                helixWorkItems.Add(workItem);
            }
            catch (HttpRequestException ex)
            {
                log?.Warning("Worker", $"  Failed to fetch helix work item {jobName}/{workItemName}: {ex.Message}");
            }
        }

        return helixWorkItems;
    }

    private static void InsertTestsData(SqliteConnection conn, SqliteTransaction tx,
        string organization, string project, int buildId,
        List<AzdoJobTestSummary> summary, List<AzdoTestResult> failures, List<HelixWorkItem> helixWorkItems)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        foreach (var s in summary)
        {
            InsertTestRun(cmd, organization, project, buildId,
                s.RunId, s.JobName, s.TotalCount, s.PassedCount, s.FailedCount, s.SkippedCount, s.Duration?.TotalSeconds);
        }

        var runGroups = failures.GroupBy(f => f.TestRunId);
        foreach (var group in runGroups)
        {
            var first = group.First();
            if (!summary.Any(s => s.RunId == group.Key))
            {
                InsertTestRun(cmd, organization, project, buildId,
                    group.Key, first.TestRunName, group.Count(), 0, group.Count(), 0);
            }

            foreach (var r in group)
            {
                InsertTestResult(cmd, organization, project, group.Key, r);
            }
        }

        foreach (var workItem in helixWorkItems)
        {
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

            using (var helixCmd = conn.CreateCommand())
            {
                helixCmd.Transaction = tx;
                helixCmd.CommandText = """
                    INSERT OR IGNORE INTO helix_work_items
                        (job_name, work_item_name, state, exit_code, console_output_uri, files, is_deadletter)
                    VALUES
                        (@job, @wi, @state, @exitCode, @consoleUri, @files, @isDeadletter)
                    """;
                helixCmd.Parameters.AddWithValue("@job", workItem.Job);
                helixCmd.Parameters.AddWithValue("@wi", workItem.Name);
                helixCmd.Parameters.AddWithValue("@state", workItem.State);
                helixCmd.Parameters.AddWithValue("@exitCode", workItem.ExitCode.HasValue ? workItem.ExitCode.Value : DBNull.Value);
                helixCmd.Parameters.AddWithValue("@consoleUri", (object?)workItem.ConsoleOutputUri ?? DBNull.Value);
                helixCmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
                helixCmd.Parameters.AddWithValue("@isDeadletter", workItem.IsDeadLetter ? 1 : 0);
                helixCmd.ExecuteNonQuery();
            }

            if (workItem.IsDeadLetter)
            {
                using var dlCmd = conn.CreateCommand();
                dlCmd.Transaction = tx;
                dlCmd.CommandText = """
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
                dlCmd.Parameters.AddWithValue("@job", workItem.Job);
                dlCmd.Parameters.AddWithValue("@wi", workItem.Name);
                dlCmd.Parameters.AddWithValue("@org", organization);
                dlCmd.Parameters.AddWithValue("@buildId", buildId);
                dlCmd.ExecuteNonQuery();
            }
        }
    }

    private static void MarkIngestionComplete(SqliteConnection conn, SqliteTransaction tx, string organization, int buildId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE builds
            SET ingestion_status = 'complete', ingestion_attempts = 0, ingestion_last_error = NULL, ingestion_next_retry_time = NULL
            WHERE organization = @org AND build_id = @buildId
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.ExecuteNonQuery();
    }

    private void HandleIngestionFailure(string organization, int buildId, Exception ex)
    {
        var attempts = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT ingestion_attempts FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            var value = cmd.ExecuteScalar();
            return value is not null ? Convert.ToInt32(value) : 0;
        });

        var newAttempts = attempts + 1;
        if (newAttempts >= MaxAttempts)
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    UPDATE builds
                    SET ingestion_status = 'abandoned', ingestion_attempts = @attempts,
                        ingestion_last_error = @error, ingestion_next_retry_time = NULL
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.Parameters.AddWithValue("@attempts", newAttempts);
                cmd.Parameters.AddWithValue("@error", ex.Message);
                cmd.ExecuteNonQuery();
            });
            _log?.Error("Worker", $"Build #{buildId} ingestion abandoned after {newAttempts} attempts: {ex.Message}");
        }
        else
        {
            var backoffIndex = Math.Min(newAttempts - 1, s_backoffSeconds.Length - 1);
            var delaySecs = s_backoffSeconds[backoffIndex];
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = $"""
                    UPDATE builds
                    SET ingestion_status = 'failed', ingestion_attempts = @attempts,
                        ingestion_last_error = @error,
                        ingestion_next_retry_time = datetime('now', '+{delaySecs} seconds')
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.Parameters.AddWithValue("@attempts", newAttempts);
                cmd.Parameters.AddWithValue("@error", ex.Message);
                cmd.ExecuteNonQuery();
            });
            _log?.Warning("Worker",
                $"Build #{buildId} ingestion failed (attempt {newAttempts}), retry in {delaySecs}s: {ex.Message}");
        }
    }

    // ── PR Info ──────────────────────────────────────────────────────

    private async Task TryFetchPrInfoAsync(string organization, int buildId, CancellationToken ct)
    {
        var prInfo = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT pr_number, repository_name, repository_type FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                return ((int PrNumber, string Repository, string? RepositoryType)?)null;
            }

            return (reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        });

        if (prInfo is null || !AzdoRepositoryTypes.IsGitHub(prInfo.Value.RepositoryType))
        {
            return;
        }

        var (prNumber, repository, _) = prInfo.Value;

        var hasTargetBranch = _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT target_branch FROM pull_requests WHERE repository = @repo AND pr_number = @pr";
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@pr", prNumber);
            return cmd.ExecuteScalar() is string targetBranch && !string.IsNullOrWhiteSpace(targetBranch);
        });
        if (hasTargetBranch)
        {
            return;
        }

        var psi = new System.Diagnostics.ProcessStartInfo("gh", $"pr view {prNumber} --repo {repository} --json title,author,baseRefName")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                _log?.Warning("Worker", $"  Build #{buildId} — failed to start gh process for PR info");
                return;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _log?.Warning("Worker", $"  Build #{buildId} — gh pr view failed (exit {process.ExitCode})");
                return;
            }

            var prDoc = System.Text.Json.JsonDocument.Parse(output);
            var title = prDoc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var author = prDoc.RootElement.TryGetProperty("author", out var a) && a.TryGetProperty("login", out var login)
                ? login.GetString() : null;
            var targetBranch = prDoc.RootElement.TryGetProperty("baseRefName", out var b) ? b.GetString() : null;

            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    INSERT INTO pull_requests (repository, pr_number, title, author, target_branch)
                    VALUES (@repo, @pr, @title, @author, @targetBranch)
                    ON CONFLICT(repository, pr_number) DO UPDATE SET
                        title = excluded.title,
                        author = excluded.author,
                        target_branch = excluded.target_branch,
                        fetched_at = datetime('now')
                    """;
                cmd.Parameters.AddWithValue("@repo", repository);
                cmd.Parameters.AddWithValue("@pr", prNumber);
                cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@author", (object?)author ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@targetBranch", (object?)targetBranch ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            });
            _log?.Info("Worker", $"  Build #{buildId} — PR #{prNumber} info cached ({author})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // PR info is best-effort metadata from a different system — never let
            // a failure here affect the build's own ingestion status.
            _log?.Warning("Worker", $"  Build #{buildId} — failed to fetch PR #{prNumber} info: {ex.Message}");
        }
    }

    private void RaiseBuildIngested(string organization, int buildId)
    {
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
            _log?.Info("Worker", $"Build #{buildId} fully ingested, notifying subscribers.");
            OnBuildIngested?.Invoke(buildEvent);
        }
    }

    /// <summary>
    /// Resets all abandoned builds to pending for retry.
    /// </summary>
    public int RetryAbandoned()
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                UPDATE builds
                SET ingestion_status = 'pending', ingestion_attempts = 0, ingestion_last_error = NULL, ingestion_next_retry_time = NULL
                WHERE ingestion_status = 'abandoned'
                """;
            return cmd.ExecuteNonQuery();
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _prioritySignal.Dispose();
    }
}
