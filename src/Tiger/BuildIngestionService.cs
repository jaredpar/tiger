namespace Tiger;

/// <summary>
/// Ingests build and test data from AzDO into the SQLite database.
/// Build rows are inserted immediately; detailed data (tests, timeline, helix)
/// is handled via ingestion tasks processed by <see cref="IngestionWorker"/>.
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
    /// Inserts build rows and creates ingestion tasks for async processing.
    /// </summary>
    public Task IngestBuildsAsync(AzdoClient client, string organization, string project, List<AzdoBuild> builds)
    {
        foreach (var build in builds)
        {
            var result = build.Result ?? "unknown";
            _log?.Info("Ingestion",
                $"#{build.Id} {build.DefinitionName} {build.BuildNumber} [{result}] {build.RepositoryName ?? ""}");

            InsertBuild(organization, project, build);
            CreateIngestionTasks(organization, project, build);
        }
        return Task.CompletedTask;
    }

    internal void InsertBuild(string organization, string project, AzdoBuild build)
    {
        _db.WithCommand(cmd =>
        {
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
        });
    }

    internal void CreateIngestionTasks(string organization, string project, AzdoBuild build)
    {
        var taskTypes = new List<string> { "tests", "timeline", "helix" };

        // Only create pr_info task if this is a PR build and we don't already have the PR cached
        if (build.PrNumber is not null && build.RepositoryName is not null)
        {
            if (!HasPullRequest(build.RepositoryName, build.PrNumber.Value))
            {
                taskTypes.Add("pr_info");
            }
        }

        foreach (var taskType in taskTypes)
        {
            _db.WithCommand(cmd =>
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO build_ingestion_tasks
                        (organization, build_id, task_type, status)
                    VALUES
                        (@org, @buildId, @type, 'pending')
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", build.Id);
                cmd.Parameters.AddWithValue("@type", taskType);
                cmd.ExecuteNonQuery();
            });
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

    internal void InsertTestRun(string organization, string project, int buildId, int runId,
        string runName, int total, int passed, int failed, int skipped)
    {
        _db.WithCommand(cmd =>
        {
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
        });
    }

    internal void InsertTestResult(string organization, string project, int runId, AzdoTestResult result)
    {
        _db.WithCommand(cmd =>
        {
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
        });
    }

    internal void IngestTimelineIssues(string organization, string project, int buildId, AzdoTimeline timeline)
    {
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "DELETE FROM build_timeline_issues WHERE organization = @org AND build_id = @buildId";
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        });

        var recordNames = timeline.Records.ToDictionary(r => r.Id, r => r.Name);

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
                _db.WithCommand(cmd =>
                {
                    cmd.CommandText = """
                        INSERT INTO build_timeline_issues
                            (organization, build_id, record_name, record_type,
                             parent_name, record_result, issue_type, issue_message, issue_category)
                        VALUES
                            (@org, @buildId, @name, @type,
                             @parent, @result, @issueType, @message, @category)
                        """;
                    cmd.Parameters.AddWithValue("@org", organization);
                    cmd.Parameters.AddWithValue("@buildId", buildId);
                    cmd.Parameters.AddWithValue("@name", record.Name);
                    cmd.Parameters.AddWithValue("@type", record.RecordType);
                    cmd.Parameters.AddWithValue("@parent", (object?)parentName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@result", (object?)record.Result ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@issueType", issue.Type);
                    cmd.Parameters.AddWithValue("@message", issue.Message);
                    cmd.Parameters.AddWithValue("@category", (object?)issue.Category ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                });
            }
        }
    }
}
