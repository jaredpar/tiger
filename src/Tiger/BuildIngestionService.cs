using Microsoft.Data.Sqlite;
namespace Tiger;

/// <summary>
/// Ingests build and test data from AzDO into the SQLite database.
/// Used as the callback for <see cref="BuildPoller.OnNewBuilds"/>.
/// </summary>
public sealed class BuildIngestionService
{
    private readonly TigerDatabase _db;
    private readonly ServiceLog? _log;

    public BuildIngestionService(TigerDatabase db, ServiceLog? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Ingests a batch of new builds for a given org/project.
    /// Fetches test runs and failed results for each build and stores everything in SQLite.
    /// </summary>
    public async Task IngestBuildsAsync(AzdoClient client, string organization, string project, List<AzdoBuild> builds)
    {
        foreach (var build in builds)
        {
            try
            {
                await IngestBuildAsync(client, organization, project, build);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("Ingestion",
                    $"Failed build #{build.Id} {build.DefinitionName} {build.BuildNumber}: {ex.Message}");
            }
        }
    }

    private async Task IngestBuildAsync(AzdoClient client, string organization, string project, AzdoBuild build)
    {
        var result = build.Result ?? "unknown";
        _log?.Info("Ingestion",
            $"#{build.Id} {build.DefinitionName} {build.BuildNumber} [{result}] {build.RepositoryName ?? ""}");

        InsertBuild(organization, project, build);

        // Fetch test summary (runs) and insert them
        var testSummary = await client.GetTestSummaryByJobAsync(build.Id);
        // We also need run IDs — GetTestSummaryByJobAsync doesn't return them.
        // Instead, fetch test failures which includes run IDs from the test run objects.

        var failures = await client.GetTestFailuresAsync(build.Id);

        // Group failures by run to build run-level data
        var runGroups = failures.GroupBy(f => f.TestRunId);
        foreach (var group in runGroups)
        {
            var first = group.First();
            // Match test summary if available
            var summary = testSummary.FirstOrDefault(s => s.JobName == first.TestRunName);
            InsertTestRun(organization, project, build.Id, group.Key, first.TestRunName,
                summary?.TotalCount ?? group.Count(),
                summary?.PassedCount ?? 0,
                summary?.FailedCount ?? group.Count(),
                summary?.SkippedCount ?? 0);

            foreach (var r in group)
            {
                InsertTestResult(organization, project, group.Key, r);
            }
        }

        // Also insert runs that had no failures (from test summary)
        foreach (var summary in testSummary)
        {
            if (!runGroups.Any(g => g.First().TestRunName == summary.JobName))
            {
                // No run_id available for all-pass runs — skip inserting them for now
                // as we don't have the run ID from the summary endpoint
            }
        }

        if (failures.Count > 0)
        {
            _log?.Warning("Ingestion",
                $"  #{build.Id} — {failures.Count} test failure(s) across {runGroups.Count()} run(s)");
        }

        // Fetch timeline and store error/warning issues from failed jobs
        try
        {
            var timeline = await client.GetTimelineAsync(build.Id);
            IngestTimelineIssues(organization, project, build.Id, timeline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("Ingestion", $"  #{build.Id} — could not fetch timeline: {ex.Message}");
        }
    }

    internal void InsertBuild(string organization, string project, AzdoBuild build)
    {
        using var cmd = _db.Connection.CreateCommand();
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

    internal void InsertTestRun(string organization, string project, int buildId, int runId,
        string runName, int total, int passed, int failed, int skipped)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO test_runs
                (organization, project, build_id, run_id, run_name, total_tests, passed_tests, failed_tests, skipped_tests)
            VALUES
                (@org, @proj, @buildId, @runId, @runName, @total, @passed, @failed, @skipped)
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@runName", runName);
        cmd.Parameters.AddWithValue("@total", total);
        cmd.Parameters.AddWithValue("@passed", passed);
        cmd.Parameters.AddWithValue("@failed", failed);
        cmd.Parameters.AddWithValue("@skipped", skipped);
        cmd.ExecuteNonQuery();
    }

    internal void InsertTestResult(string organization, string project, int runId, AzdoTestResult result)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO test_results
                (organization, project, run_id, result_id, test_case_title, outcome,
                 error_message, stack_trace, helix_job_name, helix_work_item_name)
            VALUES
                (@org, @proj, @runId, @resultId, @title, @outcome,
                 @errorMsg, @stack, @helixJob, @helixWi)
            """;
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
        cmd.ExecuteNonQuery();
    }

    internal void IngestTimelineIssues(string organization, string project, int buildId, AzdoTimeline timeline)
    {
        // Delete existing issues for this build (in case of re-ingestion)
        using (var delCmd = _db.Connection.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM build_timeline_issues WHERE organization = @org AND project = @proj AND build_id = @buildId";
            delCmd.Parameters.AddWithValue("@org", organization);
            delCmd.Parameters.AddWithValue("@proj", project);
            delCmd.Parameters.AddWithValue("@buildId", buildId);
            delCmd.ExecuteNonQuery();
        }

        // Build a lookup of record id → name for parent resolution
        var recordNames = timeline.Records.ToDictionary(r => r.Id, r => r.Name);

        foreach (var record in timeline.Records)
        {
            var issues = record.Issues.Where(i => i.Type is "error" or "warning").ToList();
            if (issues.Count == 0) continue;

            var parentName = record.ParentId is not null && recordNames.TryGetValue(record.ParentId, out var pn)
                ? pn : null;

            foreach (var issue in issues)
            {
                using var cmd = _db.Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO build_timeline_issues
                        (organization, project, build_id, record_name, record_type,
                         parent_name, record_result, issue_type, issue_message, issue_category)
                    VALUES
                        (@org, @proj, @buildId, @name, @type,
                         @parent, @result, @issueType, @message, @category)
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@proj", project);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.Parameters.AddWithValue("@name", record.Name);
                cmd.Parameters.AddWithValue("@type", record.RecordType);
                cmd.Parameters.AddWithValue("@parent", (object?)parentName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@result", (object?)record.Result ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@issueType", issue.Type);
                cmd.Parameters.AddWithValue("@message", issue.Message);
                cmd.Parameters.AddWithValue("@category", (object?)issue.Category ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
