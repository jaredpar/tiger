using Microsoft.Data.Sqlite;

namespace Tiger;

/// <summary>
/// Implements <see cref="IBuildDataSource"/> by querying the local SQLite cache.
/// </summary>
public sealed class SqliteBuildDataSource : IBuildDataSource
{
    private readonly TigerDatabase _db;
    private readonly string _organization;
    private readonly string _project;

    public SqliteBuildDataSource(TigerDatabase db, string organization, string project)
    {
        _db = db;
        _organization = organization;
        _project = project;
    }

    public Task<List<AzdoBuild>> GetRecentBuildsAsync(int? definitionId = null, int top = 10)
    {
        using var cmd = _db.Connection.CreateCommand();
        var sql = """
            SELECT build_id, build_number, definition_name, status, result, source_branch, finish_time
            FROM builds
            WHERE organization = @org AND project = @proj
            """;

        if (definitionId is not null)
        {
            sql += " AND definition_id = @defId";
            cmd.Parameters.AddWithValue("@defId", definitionId.Value);
        }

        sql += " ORDER BY build_id DESC LIMIT @top";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@org", _organization);
        cmd.Parameters.AddWithValue("@proj", _project);
        cmd.Parameters.AddWithValue("@top", top);

        return Task.FromResult(ReadBuilds(cmd));
    }

    public Task<List<AzdoBuild>> GetBuildsForRepositoryAsync(string repository, int top = 10, string? reasonFilter = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        var sql = """
            SELECT build_id, build_number, definition_name, status, result, source_branch, finish_time
            FROM builds
            WHERE organization = @org AND project = @proj AND repository_name = @repo
            """;

        if (reasonFilter is not null)
        {
            // reasonFilter maps to source_branch patterns in cached data
            sql += " AND source_branch LIKE @reason";
            cmd.Parameters.AddWithValue("@reason", $"%{reasonFilter}%");
        }

        sql += " ORDER BY build_id DESC LIMIT @top";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@org", _organization);
        cmd.Parameters.AddWithValue("@proj", _project);
        cmd.Parameters.AddWithValue("@repo", repository);
        cmd.Parameters.AddWithValue("@top", top);

        return Task.FromResult(ReadBuilds(cmd));
    }

    public Task<List<AzdoBuild>> GetBuildsForPullRequestAsync(string repository, int prNumber, int top = 10)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT build_id, build_number, definition_name, status, result, source_branch, finish_time
            FROM builds
            WHERE organization = @org AND project = @proj
              AND repository_name = @repo AND pr_number = @pr
            ORDER BY build_id DESC LIMIT @top
            """;

        cmd.Parameters.AddWithValue("@org", _organization);
        cmd.Parameters.AddWithValue("@proj", _project);
        cmd.Parameters.AddWithValue("@repo", repository);
        cmd.Parameters.AddWithValue("@pr", prNumber);
        cmd.Parameters.AddWithValue("@top", top);

        return Task.FromResult(ReadBuilds(cmd));
    }

    public Task<List<AzdoTestResult>> GetTestFailuresAsync(int buildId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT tr.result_id, tr.test_case_title, tr.outcome, tr.error_message, tr.stack_trace,
                   tr.helix_job_name, tr.helix_work_item_name, r.run_id, r.run_name
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            WHERE tr.organization = @org AND tr.project = @proj
              AND r.build_id = @buildId AND tr.outcome = 'Failed'
            """;

        cmd.Parameters.AddWithValue("@org", _organization);
        cmd.Parameters.AddWithValue("@proj", _project);
        cmd.Parameters.AddWithValue("@buildId", buildId);

        var results = new List<AzdoTestResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var helixJob = reader.IsDBNull(5) ? null : reader.GetString(5);
            var helixWorkItem = reader.IsDBNull(6) ? null : reader.GetString(6);
            results.Add(new AzdoTestResult
            {
                Id = reader.GetInt32(0),
                TestCaseTitle = reader.GetString(1),
                Outcome = reader.GetString(2),
                ErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3),
                StackTrace = reader.IsDBNull(4) ? null : reader.GetString(4),
                HelixJobName = helixJob,
                HelixWorkItemName = helixWorkItem,
                IsHelixWorkItem = helixJob is not null,
                TestRunId = reader.GetInt32(7),
                TestRunName = reader.GetString(8),
            });
        }

        return Task.FromResult(results);
    }

    public Task<List<AzdoJobTestSummary>> GetTestSummaryByJobAsync(int buildId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT run_name, total_tests, passed_tests, failed_tests, skipped_tests
            FROM test_runs
            WHERE organization = @org AND project = @proj AND build_id = @buildId
            """;

        cmd.Parameters.AddWithValue("@org", _organization);
        cmd.Parameters.AddWithValue("@proj", _project);
        cmd.Parameters.AddWithValue("@buildId", buildId);

        var results = new List<AzdoJobTestSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AzdoJobTestSummary
            {
                JobName = reader.GetString(0),
                TotalCount = reader.GetInt32(1),
                PassedCount = reader.GetInt32(2),
                FailedCount = reader.GetInt32(3),
                SkippedCount = reader.GetInt32(4),
            });
        }

        return Task.FromResult(results);
    }

    private List<AzdoBuild> ReadBuilds(SqliteCommand cmd)
    {
        var builds = new List<AzdoBuild>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var buildId = reader.GetInt32(0);
            builds.Add(new AzdoBuild
            {
                Id = buildId,
                BuildNumber = reader.GetString(1),
                DefinitionName = reader.GetString(2),
                Status = reader.GetString(3),
                Result = reader.IsDBNull(4) ? null : reader.GetString(4),
                Uri = $"https://dev.azure.com/{_organization}/{_project}/_build/results?buildId={buildId}",
                SourceBranch = reader.GetString(5),
                FinishTime = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
            });
        }
        return builds;
    }
}
