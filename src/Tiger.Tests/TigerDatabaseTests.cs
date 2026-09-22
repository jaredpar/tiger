using Xunit;

namespace Tiger.Tests;

public class TigerDatabaseTests : IDisposable
{
    private readonly string _dbPath;

    public TigerDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void Open_CreatesDatabase()
    {
        using var db = TigerDatabase.Open(_dbPath);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void InsertAgentTask_PersistsGenericNamesAndPreservesSessionKeys()
    {
        using (var db = TigerDatabase.Open(_dbPath))
        {
            db.InsertAgentTask("session-test", "dotnet/roslyn", "ClientTests.Shutdown", "test.md");
            db.InsertAgentTask("session-build", "dotnet/roslyn", "roslyn-CI #1607626", "build.md");
            db.InsertAgentTask("session-other", "dotnet/runtime", "Dependency update", "other.md");
            db.InsertAgentTask("session-build", "dotnet/roslyn", "Duplicate", "duplicate.md");
        }

        using var reopened = TigerDatabase.Open(_dbPath);
        var actual = reopened.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT session_id, repository, test_name, file_path FROM agent_tasks ORDER BY session_id";
            using var reader = cmd.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}");
            }
            return string.Join("\n", rows);
        });
        var expected = """
            session-build|dotnet/roslyn|roslyn-CI #1607626|build.md
            session-other|dotnet/runtime|Dependency update|other.md
            session-test|dotnet/roslyn|ClientTests.Shutdown|test.md
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
        Assert.Equal("""
            session-build
            session-other
            session-test
            """.ReplaceLineEndings("\n"), string.Join("\n", reopened.GetAgentTaskSessionIds().Order()));
    }

    [Fact]
    public void Open_SetsSchemaVersion()
    {
        using var db = TigerDatabase.Open(_dbPath);
        var version = db.WithCommand(cmd =>
        {
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
        Assert.Equal(TigerDatabase.CurrentSchemaVersion, version);
    }

    [Fact]
    public void Open_CreatesAllTables()
    {
        using var db = TigerDatabase.Open(_dbPath);
        var tables = GetTableNames(db);
        Assert.Contains("builds", tables);
        Assert.Contains("test_runs", tables);
        Assert.Contains("test_results", tables);
        Assert.Contains("poll_watermarks", tables);
        Assert.Contains("flaky_tests", tables);
    }

    [Fact]
    public void Open_IsIdempotent()
    {
        using (var db = TigerDatabase.Open(_dbPath)) { }
        using var db2 = TigerDatabase.Open(_dbPath);
        var tables = GetTableNames(db2);
        Assert.Contains("builds", tables);
    }

    [Fact]
    public void Builds_InsertAndQuery()
    {
        using var db = TigerDatabase.Open(_dbPath);
        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch)
                VALUES ('dnceng-public', 'public', 1, '20250101.1', 'runtime', 42, 'completed', 'refs/heads/main');
                """;
            cmd.ExecuteNonQuery();
        });

        db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT build_number, definition_name FROM builds WHERE build_id = 1;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("20250101.1", reader.GetString(0));
            Assert.Equal("runtime", reader.GetString(1));
        });
    }

    [Fact]
    public void TestResults_ForeignKeyChain()
    {
        using var db = TigerDatabase.Open(_dbPath);

        db.WithCommand(cmd =>
        {
            // Enable FK enforcement
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        });

        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch)
                VALUES ('org', 'proj', 1, '1.0', 'def', 1, 'completed', 'main');

                INSERT INTO test_runs (organization, project, build_id, run_id, run_name)
                VALUES ('org', 'proj', 1, 100, 'Test Run');

                INSERT INTO test_results (organization, project, run_id, result_id, test_case_title, outcome)
                VALUES ('org', 'proj', 100, 1, 'MyTest', 'Failed');
                """;
            cmd.ExecuteNonQuery();
        });

        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT tr.test_case_title, r.run_name, b.definition_name
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
                JOIN builds b ON r.organization = b.organization AND r.project = b.project AND r.build_id = b.build_id
                WHERE tr.outcome = 'Failed';
                """;
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("MyTest", reader.GetString(0));
            Assert.Equal("Test Run", reader.GetString(1));
            Assert.Equal("def", reader.GetString(2));
        });
    }

    private static List<string> GetTableNames(TigerDatabase db)
    {
        return db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            var tables = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
            return tables;
        });
    }

    private static void InsertFullBuild(TigerDatabase db, string org, string project, int buildId)
    {
        var prefix = $"{org}-{project}-{buildId}";
        var runId = Math.Abs($"{org}-{project}".GetHashCode() % 10000) + buildId * 100;
        db.WithCommand(cmd =>
        {
            cmd.CommandText = $"""
                INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch, finish_time)
                VALUES ('{org}', '{project}', {buildId}, '1.0.{buildId}', 'pipeline', 1, 'completed', 'refs/heads/main', '2026-01-01T00:00:00Z');

                INSERT INTO test_runs (organization, project, build_id, run_id, run_name)
                VALUES ('{org}', '{project}', {buildId}, {runId}, 'Run {buildId}');

                INSERT INTO test_results (organization, project, run_id, result_id, test_case_title, outcome, helix_job_name, helix_work_item_name)
                VALUES ('{org}', '{project}', {runId}, 1, 'Test.One', 'Failed', 'job-{prefix}', 'wi-{prefix}');

                INSERT INTO helix_work_items (job_name, work_item_name, state, exit_code, console_output_uri)
                VALUES ('job-{prefix}', 'wi-{prefix}', 'Finished', 1, 'https://example.com/console');

                INSERT INTO build_timeline_issues (organization, build_id, record_name, record_type, issue_type, issue_message)
                VALUES ('{org}', {buildId}, 'Build', 'Job', 'error', 'Something failed');

                INSERT INTO build_ingestion_tasks (organization, build_id, task_type, status)
                VALUES ('{org}', {buildId}, 'tests', 'completed');
                """;
            cmd.ExecuteNonQuery();
        });
    }

    private static long CountRows(TigerDatabase db, string table, string? where = null)
    {
        return db.WithCommand(cmd =>
        {
            cmd.CommandText = where is null ? $"SELECT COUNT(*) FROM {table}" : $"SELECT COUNT(*) FROM {table} WHERE {where}";
            return Convert.ToInt64(cmd.ExecuteScalar());
        });
    }

    [Fact]
    public void DeleteBuild_RemovesAllAssociatedData()
    {
        using var db = TigerDatabase.Open(_dbPath);
        InsertFullBuild(db, "org", "proj", 1);

        // Verify data exists
        Assert.Equal(1, CountRows(db, "builds"));
        Assert.Equal(1, CountRows(db, "test_runs"));
        Assert.Equal(1, CountRows(db, "test_results"));
        Assert.Equal(1, CountRows(db, "helix_work_items"));
        Assert.Equal(1, CountRows(db, "build_timeline_issues"));
        Assert.Equal(1, CountRows(db, "build_ingestion_tasks"));

        db.DeleteBuild("org", 1);

        // Verify everything is gone
        Assert.Equal(0, CountRows(db, "builds"));
        Assert.Equal(0, CountRows(db, "test_runs"));
        Assert.Equal(0, CountRows(db, "test_results"));
        Assert.Equal(0, CountRows(db, "helix_work_items"));
        Assert.Equal(0, CountRows(db, "build_timeline_issues"));
        Assert.Equal(0, CountRows(db, "build_ingestion_tasks"));
    }

    [Fact]
    public void DeleteBuild_LeavesOtherBuildsIntact()
    {
        using var db = TigerDatabase.Open(_dbPath);
        InsertFullBuild(db, "org", "proj", 1);
        InsertFullBuild(db, "org", "proj", 2);

        Assert.Equal(2, CountRows(db, "builds"));
        Assert.Equal(2, CountRows(db, "test_runs"));
        Assert.Equal(2, CountRows(db, "helix_work_items"));

        db.DeleteBuild("org", 1);

        // Build 2 still exists
        Assert.Equal(1, CountRows(db, "builds"));
        Assert.Equal(1, CountRows(db, "test_runs"));
        Assert.Equal(1, CountRows(db, "test_results"));
        Assert.Equal(1, CountRows(db, "helix_work_items"));
        Assert.Equal(1, CountRows(db, "build_timeline_issues"));
        Assert.Equal(1, CountRows(db, "build_ingestion_tasks"));

        // Verify it's build 2 that remains
        Assert.Equal(1, CountRows(db, "builds", "build_id = 2"));
    }

    [Fact]
    public void DeleteBuild_DifferentOrgProjectNotAffected()
    {
        using var db = TigerDatabase.Open(_dbPath);
        InsertFullBuild(db, "org1", "proj1", 1);
        InsertFullBuild(db, "org2", "proj2", 1);

        db.DeleteBuild("org1", 1);

        // org2/proj2 build still exists
        Assert.Equal(1, CountRows(db, "builds"));
        Assert.Equal(1, CountRows(db, "builds", "organization = 'org2'"));
        Assert.Equal(1, CountRows(db, "test_runs", "organization = 'org2'"));
    }

    [Fact]
    public void DeleteBuild_NoOpForNonexistentBuild()
    {
        using var db = TigerDatabase.Open(_dbPath);
        InsertFullBuild(db, "org", "proj", 1);

        // Should not throw
        db.DeleteBuild("org", 999);

        // Original build still intact
        Assert.Equal(1, CountRows(db, "builds"));
    }

    [Fact]
    public void DeleteBuild_HandlesNoHelixData()
    {
        using var db = TigerDatabase.Open(_dbPath);

        // Insert build with test results but no helix info
        db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch)
                VALUES ('org', 'proj', 1, '1.0', 'pipeline', 1, 'completed', 'main');

                INSERT INTO test_runs (organization, project, build_id, run_id, run_name)
                VALUES ('org', 'proj', 1, 100, 'Run 1');

                INSERT INTO test_results (organization, project, run_id, result_id, test_case_title, outcome)
                VALUES ('org', 'proj', 100, 1, 'Test.One', 'Failed');
                """;
            cmd.ExecuteNonQuery();
        });

        db.DeleteBuild("org", 1);

        Assert.Equal(0, CountRows(db, "builds"));
        Assert.Equal(0, CountRows(db, "test_runs"));
        Assert.Equal(0, CountRows(db, "test_results"));
    }
}
