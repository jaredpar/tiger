using Microsoft.Data.Sqlite;
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
    public void Open_SetsSchemaVersion()
    {
        using var db = TigerDatabase.Open(_dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        Assert.Equal(TigerDatabase.CurrentSchemaVersion, version);
    }

    [Fact]
    public void Open_CreatesAllTables()
    {
        using var db = TigerDatabase.Open(_dbPath);
        var tables = GetTableNames(db.Connection);
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
        var tables = GetTableNames(db2.Connection);
        Assert.Contains("builds", tables);
    }

    [Fact]
    public void Builds_InsertAndQuery()
    {
        using var db = TigerDatabase.Open(_dbPath);
        using var insert = db.Connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch)
            VALUES ('dnceng-public', 'public', 1, '20250101.1', 'runtime', 42, 'completed', 'refs/heads/main');
            """;
        insert.ExecuteNonQuery();

        using var query = db.Connection.CreateCommand();
        query.CommandText = "SELECT build_number, definition_name FROM builds WHERE build_id = 1;";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("20250101.1", reader.GetString(0));
        Assert.Equal("runtime", reader.GetString(1));
    }

    [Fact]
    public void TestResults_ForeignKeyChain()
    {
        using var db = TigerDatabase.Open(_dbPath);

        // Enable FK enforcement
        using var fk = db.Connection.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON;";
        fk.ExecuteNonQuery();

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO builds (organization, project, build_id, build_number, definition_name, definition_id, status, source_branch)
            VALUES ('org', 'proj', 1, '1.0', 'def', 1, 'completed', 'main');

            INSERT INTO test_runs (organization, project, build_id, run_id, run_name)
            VALUES ('org', 'proj', 1, 100, 'Test Run');

            INSERT INTO test_results (organization, project, run_id, result_id, test_case_title, outcome)
            VALUES ('org', 'proj', 100, 1, 'MyTest', 'Failed');
            """;
        cmd.ExecuteNonQuery();

        using var query = db.Connection.CreateCommand();
        query.CommandText = """
            SELECT tr.test_case_title, r.run_name, b.definition_name
            FROM test_results tr
            JOIN test_runs r ON tr.organization = r.organization AND tr.project = r.project AND tr.run_id = r.run_id
            JOIN builds b ON r.organization = b.organization AND r.project = b.project AND r.build_id = b.build_id
            WHERE tr.outcome = 'Failed';
            """;
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("MyTest", reader.GetString(0));
        Assert.Equal("Test Run", reader.GetString(1));
        Assert.Equal("def", reader.GetString(2));
    }

    private static List<string> GetTableNames(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }
}
