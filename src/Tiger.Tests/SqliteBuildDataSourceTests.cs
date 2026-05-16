using Xunit;

namespace Tiger.Tests;

public class SqliteBuildDataSourceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;
    private readonly SqliteBuildDataSource _source;

    public SqliteBuildDataSourceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
        _source = new SqliteBuildDataSource(_db, "org", "proj");
        SeedData();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private void SeedData()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, result, source_branch, repository_name, pr_number, finish_time)
            VALUES
                ('org', 'proj', 100, '20250101.1', 'runtime',   1, 'completed', 'succeeded', 'refs/heads/main', 'dotnet/runtime', NULL, '2025-01-01T10:00:00'),
                ('org', 'proj', 101, '20250101.2', 'runtime',   1, 'completed', 'failed',    'refs/pull/42/merge', 'dotnet/runtime', 42, '2025-01-01T11:00:00'),
                ('org', 'proj', 102, '20250101.3', 'sdk',       2, 'completed', 'succeeded', 'refs/heads/main', 'dotnet/sdk', NULL, '2025-01-01T12:00:00'),
                ('other', 'proj', 200, '20250102.1', 'runtime', 1, 'completed', 'succeeded', 'refs/heads/main', 'dotnet/runtime', NULL, '2025-01-02T10:00:00');

            INSERT INTO test_runs (organization, project, build_id, run_id, run_name, total_tests, passed_tests, failed_tests, skipped_tests)
            VALUES
                ('org', 'proj', 101, 500, 'Test Run 1', 100, 95, 4, 1),
                ('org', 'proj', 101, 501, 'Test Run 2', 50,  48, 2, 0);

            INSERT INTO test_results (organization, project, run_id, result_id, test_case_title, outcome, error_message, helix_job_name, helix_work_item_name)
            VALUES
                ('org', 'proj', 500, 1, 'FailingTest1', 'Failed', 'Assert failed', 'helix-job-1', 'work-item-1'),
                ('org', 'proj', 500, 2, 'FailingTest2', 'Failed', 'Timeout', NULL, NULL),
                ('org', 'proj', 500, 3, 'PassingTest',  'Passed', NULL, NULL, NULL),
                ('org', 'proj', 501, 4, 'FailingTest3', 'Failed', 'NullRef', NULL, NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetRecentBuilds_ReturnsAll()
    {
        var builds = await _source.GetRecentBuildsAsync();
        Assert.Equal(3, builds.Count);
        // Ordered by build_id DESC
        Assert.Equal(102, builds[0].Id);
        Assert.Equal(101, builds[1].Id);
        Assert.Equal(100, builds[2].Id);
    }

    [Fact]
    public async Task GetRecentBuilds_FilterByDefinition()
    {
        var builds = await _source.GetRecentBuildsAsync(definitionId: 1);
        Assert.Equal(2, builds.Count);
        Assert.All(builds, b => Assert.Equal("runtime", b.DefinitionName));
    }

    [Fact]
    public async Task GetRecentBuilds_RespectsTop()
    {
        var builds = await _source.GetRecentBuildsAsync(top: 1);
        Assert.Single(builds);
    }

    [Fact]
    public async Task GetBuildsForRepository_FiltersCorrectly()
    {
        var builds = await _source.GetBuildsForRepositoryAsync("dotnet/runtime");
        Assert.Equal(2, builds.Count);
        Assert.All(builds, b => Assert.Equal("runtime", b.DefinitionName));
    }

    [Fact]
    public async Task GetBuildsForPullRequest_FindsPrBuilds()
    {
        var builds = await _source.GetBuildsForPullRequestAsync("dotnet/runtime", 42);
        Assert.Single(builds);
        Assert.Equal(101, builds[0].Id);
    }

    [Fact]
    public async Task GetBuildsForPullRequest_ReturnsEmptyForUnknownPr()
    {
        var builds = await _source.GetBuildsForPullRequestAsync("dotnet/runtime", 999);
        Assert.Empty(builds);
    }

    [Fact]
    public async Task GetTestFailures_ReturnsOnlyFailed()
    {
        var failures = await _source.GetTestFailuresAsync(101);
        Assert.Equal(3, failures.Count);
        Assert.All(failures, f => Assert.Equal("Failed", f.Outcome));
    }

    [Fact]
    public async Task GetTestFailures_IncludesHelixInfo()
    {
        var failures = await _source.GetTestFailuresAsync(101);
        var helix = failures.Single(f => f.TestCaseTitle == "FailingTest1");
        Assert.True(helix.IsHelixWorkItem);
        Assert.Equal("helix-job-1", helix.HelixJobName);
        Assert.Equal("work-item-1", helix.HelixWorkItemName);
    }

    [Fact]
    public async Task GetTestFailures_NonHelixHasNullFields()
    {
        var failures = await _source.GetTestFailuresAsync(101);
        var nonHelix = failures.Single(f => f.TestCaseTitle == "FailingTest2");
        Assert.False(nonHelix.IsHelixWorkItem);
        Assert.Null(nonHelix.HelixJobName);
    }

    [Fact]
    public async Task GetTestSummaryByJob_ReturnsSummary()
    {
        var summary = await _source.GetTestSummaryByJobAsync(101);
        Assert.Equal(2, summary.Count);

        var run1 = summary.Single(s => s.JobName == "Test Run 1");
        Assert.Equal(100, run1.TotalCount);
        Assert.Equal(95, run1.PassedCount);
        Assert.Equal(4, run1.FailedCount);
        Assert.Equal(1, run1.SkippedCount);
    }

    [Fact]
    public async Task MultiOrg_IsolatesData()
    {
        // Our source is scoped to ("org", "proj") — should not see ("other", "proj")
        var builds = await _source.GetRecentBuildsAsync(top: 100);
        Assert.DoesNotContain(builds, b => b.Id == 200);
    }
}
