using Xunit;

namespace Tiger.Tests;

public class BuildIngestionServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;
    private readonly BuildIngestionService _service;

    public BuildIngestionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
        _service = new BuildIngestionService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void InsertBuild_StoresCorrectly()
    {
        var build = new AzdoBuild
        {
            Id = 1,
            BuildNumber = "20250101.1",
            DefinitionName = "runtime",
            DefinitionId = 42,
            Status = "completed",
            Result = "failed",
            Uri = "https://dev.azure.com/org/proj/_build/results?buildId=1",
            SourceBranch = "refs/heads/main",
            SourceVersion = "abc123",
            RepositoryName = "dotnet/runtime",
            PrNumber = null,
            FinishTime = new DateTime(2025, 1, 1, 12, 0, 0),
        };

        _service.InsertBuild("org", "proj", build);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT build_number, definition_name, definition_id, result, repository_name FROM builds WHERE build_id = 1;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("20250101.1", reader.GetString(0));
            Assert.Equal("runtime", reader.GetString(1));
            Assert.Equal(42, reader.GetInt32(2));
            Assert.Equal("failed", reader.GetString(3));
            Assert.Equal("dotnet/runtime", reader.GetString(4));
        });
    }

    [Fact]
    public void InsertBuild_WithPrNumber()
    {
        var build = new AzdoBuild
        {
            Id = 2,
            BuildNumber = "20250101.2",
            DefinitionName = "runtime",
            DefinitionId = 42,
            Status = "completed",
            Result = "succeeded",
            Uri = "https://dev.azure.com/org/proj/_build/results?buildId=2",
            SourceBranch = "refs/pull/99/merge",
            PrNumber = 99,
        };

        _service.InsertBuild("org", "proj", build);

        var prNumber = _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT pr_number FROM builds WHERE build_id = 2;";
            return cmd.ExecuteScalar();
        });
        Assert.Equal(99L, prNumber);
    }

    [Fact]
    public void InsertBuild_Duplicate_IsIgnored()
    {
        var build = new AzdoBuild
        {
            Id = 3,
            BuildNumber = "20250101.3",
            DefinitionName = "runtime",
            DefinitionId = 42,
            Status = "completed",
            Uri = "https://dev.azure.com/org/proj/_build/results?buildId=3",
            SourceBranch = "main",
        };

        _service.InsertBuild("org", "proj", build);
        _service.InsertBuild("org", "proj", build); // no exception
    }

    [Fact]
    public void InsertTestRun_StoresCorrectly()
    {
        InsertSampleBuild(10);
        _service.InsertTestRun("org", "proj", 10, 500, "Test Run A", 100, 95, 4, 1);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT run_name, total_tests, failed_tests FROM test_runs WHERE run_id = 500;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Test Run A", reader.GetString(0));
            Assert.Equal(100, reader.GetInt32(1));
            Assert.Equal(4, reader.GetInt32(2));
        });
    }

    [Fact]
    public void InsertTestResult_StoresCorrectly()
    {
        InsertSampleBuild(20);
        _service.InsertTestRun("org", "proj", 20, 600, "Run", 10, 9, 1, 0);

        var result = new AzdoTestResult
        {
            Id = 1,
            TestCaseTitle = "MyNamespace.MyTest",
            Outcome = "Failed",
            ErrorMessage = "Assert.Equal failed",
            StackTrace = "at MyTest.cs:42",
            HelixJobName = "helix-job",
            HelixWorkItemName = "work-item",
            IsHelixWorkItem = true,
            TestRunId = 600,
            TestRunName = "Run",
        };

        _service.InsertTestResult("org", "proj", 600, result);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT test_case_title, outcome, error_message, helix_job_name, is_helix_work_item FROM test_results WHERE result_id = 1 AND run_id = 600;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("MyNamespace.MyTest", reader.GetString(0));
            Assert.Equal("Failed", reader.GetString(1));
            Assert.Equal("Assert.Equal failed", reader.GetString(2));
            Assert.Equal("helix-job", reader.GetString(3));
            Assert.Equal(1, reader.GetInt32(4));
        });
    }

    [Fact]
    public void InsertTestResult_NullHelixFields()
    {
        InsertSampleBuild(30);
        _service.InsertTestRun("org", "proj", 30, 700, "Run", 10, 9, 1, 0);

        var result = new AzdoTestResult
        {
            Id = 1,
            TestCaseTitle = "NonHelixTest",
            Outcome = "Failed",
            TestRunId = 700,
            TestRunName = "Run",
        };

        _service.InsertTestResult("org", "proj", 700, result);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT helix_job_name, helix_work_item_name, is_helix_work_item FROM test_results WHERE result_id = 1 AND run_id = 700;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(0, reader.GetInt32(2));
        });
    }

    [Fact]
    public void IngestTimelineIssues_StoresCorrectly()
    {
        InsertSampleBuild(40);

        var timeline = new AzdoTimeline
        {
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "job-1",
                    ParentId = null,
                    Name = "Build_Windows",
                    RecordType = "Job",
                    Result = "failed",
                    Issues =
                    [
                        new AzdoTimelineIssue { Type = "error", Message = "Build failed", Category = "General" },
                    ],
                },
                new AzdoTimelineRecord
                {
                    Id = "task-1",
                    ParentId = "job-1",
                    Name = "Compile",
                    RecordType = "Task",
                    Result = "failed",
                    Issues =
                    [
                        new AzdoTimelineIssue { Type = "error", Message = "CS0001: Compilation error" },
                        new AzdoTimelineIssue { Type = "warning", Message = "CS0168: Unused variable" },
                    ],
                },
                new AzdoTimelineRecord
                {
                    Id = "job-2",
                    ParentId = null,
                    Name = "Build_Linux",
                    RecordType = "Job",
                    Result = "succeeded",
                    Issues = [],
                },
            ],
        };

        _service.IngestTimelineIssues("org", "proj", 40, timeline);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT record_name, record_type, parent_name, issue_type, issue_message FROM build_timeline_issues WHERE build_id = 40 ORDER BY record_name, issue_type;";
            using var reader = cmd.ExecuteReader();

            // First: Build_Windows job error
            Assert.True(reader.Read());
            Assert.Equal("Build_Windows", reader.GetString(0));
            Assert.Equal("Job", reader.GetString(1));
            Assert.True(reader.IsDBNull(2)); // no parent
            Assert.Equal("error", reader.GetString(3));
            Assert.Equal("Build failed", reader.GetString(4));

            // Second: Compile task error
            Assert.True(reader.Read());
            Assert.Equal("Compile", reader.GetString(0));
            Assert.Equal("Task", reader.GetString(1));
            Assert.Equal("Build_Windows", reader.GetString(2)); // parent is the job
            Assert.Equal("error", reader.GetString(3));
            Assert.Equal("CS0001: Compilation error", reader.GetString(4));

            // Third: Compile task warning
            Assert.True(reader.Read());
            Assert.Equal("Compile", reader.GetString(0));
            Assert.Equal("warning", reader.GetString(3));

            // No more rows (Build_Linux had no issues)
            Assert.False(reader.Read());
        });
    }

    [Fact]
    public void IngestTimelineIssues_ReIngestion_ReplacesOldData()
    {
        InsertSampleBuild(50);

        var timeline1 = new AzdoTimeline
        {
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "j1", Name = "Job1", RecordType = "Job",
                    Issues = [new AzdoTimelineIssue { Type = "error", Message = "Old error" }],
                },
            ],
        };

        _service.IngestTimelineIssues("org", "proj", 50, timeline1);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT COUNT(*) FROM build_timeline_issues WHERE build_id = 50;";
            Assert.Equal(1L, cmd.ExecuteScalar());
        });

        // Re-ingest with different data
        var timeline2 = new AzdoTimeline
        {
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "j2", Name = "Job2", RecordType = "Job",
                    Issues =
                    [
                        new AzdoTimelineIssue { Type = "error", Message = "New error 1" },
                        new AzdoTimelineIssue { Type = "warning", Message = "New warning" },
                    ],
                },
            ],
        };

        _service.IngestTimelineIssues("org", "proj", 50, timeline2);

        _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT COUNT(*) FROM build_timeline_issues WHERE build_id = 50;";
            Assert.Equal(2L, cmd.ExecuteScalar());

            cmd.CommandText = "SELECT issue_message FROM build_timeline_issues WHERE build_id = 50 AND issue_type = 'error';";
            Assert.Equal("New error 1", cmd.ExecuteScalar());
        });
    }

    private void InsertSampleBuild(int buildId)
    {
        _service.InsertBuild("org", "proj", new AzdoBuild
        {
            Id = buildId,
            BuildNumber = $"build-{buildId}",
            DefinitionName = "def",
            DefinitionId = 1,
            Status = "completed",
            Uri = $"https://example.com/{buildId}",
            SourceBranch = "main",
        });
    }
}
