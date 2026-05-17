using Microsoft.Data.Sqlite;

namespace Tiger;

/// <summary>
/// Manages the SQLite database at ~/.tiger/tiger.db.
/// Handles connection creation and schema migration.
/// </summary>
public sealed class TigerDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    public string DatabasePath { get; }
    public SqliteConnection Connection { get; }

    private TigerDatabase(string databasePath, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        Connection = connection;
    }

    /// <summary>
    /// Opens (or creates) the database at the given path and ensures
    /// the schema is up to date.
    /// </summary>
    public static TigerDatabase Open(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Enable WAL mode for better concurrent read performance
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        var db = new TigerDatabase(databasePath, connection);
        db.EnsureSchema();
        return db;
    }

    private void EnsureSchema()
    {
        var version = GetSchemaVersion();
        if (version < 1)
        {
            ApplyV1();
            SetSchemaVersion(1);
        }
    }

    private int GetSchemaVersion()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void SetSchemaVersion(int version)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private void ApplyV1()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS builds (
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                build_number TEXT NOT NULL,
                definition_name TEXT NOT NULL,
                definition_id INTEGER NOT NULL,
                status TEXT NOT NULL,
                result TEXT,
                source_branch TEXT NOT NULL,
                source_version TEXT,
                repository_name TEXT,
                pr_number INTEGER,
                finish_time TEXT,
                ingested_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (organization, project, build_id)
            );

            CREATE INDEX IF NOT EXISTS ix_builds_definition
                ON builds (organization, project, definition_id, finish_time);

            CREATE INDEX IF NOT EXISTS ix_builds_branch
                ON builds (organization, project, source_branch, finish_time);

            CREATE TABLE IF NOT EXISTS test_runs (
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                run_id INTEGER NOT NULL,
                run_name TEXT NOT NULL,
                total_tests INTEGER NOT NULL DEFAULT 0,
                passed_tests INTEGER NOT NULL DEFAULT 0,
                failed_tests INTEGER NOT NULL DEFAULT 0,
                skipped_tests INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (organization, project, run_id),
                FOREIGN KEY (organization, project, build_id)
                    REFERENCES builds (organization, project, build_id)
            );

            CREATE TABLE IF NOT EXISTS test_results (
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                run_id INTEGER NOT NULL,
                result_id INTEGER NOT NULL,
                test_case_title TEXT NOT NULL,
                outcome TEXT NOT NULL,
                error_message TEXT,
                stack_trace TEXT,
                duration_ms REAL,
                helix_job_name TEXT,
                helix_work_item_name TEXT,
                PRIMARY KEY (organization, project, run_id, result_id),
                FOREIGN KEY (organization, project, run_id)
                    REFERENCES test_runs (organization, project, run_id)
            );

            CREATE INDEX IF NOT EXISTS ix_test_results_title
                ON test_results (organization, project, test_case_title);

            CREATE INDEX IF NOT EXISTS ix_test_results_outcome
                ON test_results (organization, project, outcome);

            CREATE TABLE IF NOT EXISTS poll_watermarks (
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                last_build_id INTEGER NOT NULL DEFAULT 0,
                last_poll_time TEXT,
                PRIMARY KEY (organization, project)
            );

            CREATE TABLE IF NOT EXISTS flaky_tests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                test_case_title TEXT NOT NULL,
                branch TEXT NOT NULL,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                flip_count INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'active',
                github_issue_url TEXT,
                UNIQUE (organization, project, test_case_title, branch)
            );

            CREATE INDEX IF NOT EXISTS ix_flaky_tests_status
                ON flaky_tests (organization, project, status);

            CREATE TABLE IF NOT EXISTS build_timeline_issues (
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                record_name TEXT NOT NULL,
                record_type TEXT NOT NULL,
                parent_name TEXT,
                record_result TEXT,
                issue_type TEXT NOT NULL,
                issue_message TEXT NOT NULL,
                issue_category TEXT,
                FOREIGN KEY (organization, project, build_id)
                    REFERENCES builds (organization, project, build_id)
            );

            CREATE INDEX IF NOT EXISTS ix_timeline_issues_build
                ON build_timeline_issues (organization, project, build_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}
