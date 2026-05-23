using Microsoft.Data.Sqlite;

namespace Tiger;

/// <summary>
/// Manages the SQLite database at ~/.tiger/tiger.db.
/// Handles connection creation and schema migration.
/// </summary>
public sealed class TigerDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 5;

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

    /// <summary>
    /// Checks the schema version of an existing database file.
    /// Returns null if the file doesn't exist, or the version number if it does.
    /// </summary>
    public static int? GetExistingSchemaVersion(string databasePath)
    {
        if (!File.Exists(databasePath))
            return null;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = Convert.ToInt32(cmd.ExecuteScalar());
        connection.Close();
        SqliteConnection.ClearPool(connection);
        return result;
    }

    /// <summary>
    /// Returns true if the database file exists and has an outdated schema.
    /// </summary>
    public static bool IsOutdated(string databasePath)
    {
        var version = GetExistingSchemaVersion(databasePath);
        return version is not null && version != CurrentSchemaVersion;
    }

    private void EnsureSchema()
    {
        CreateSchema();
        SetSchemaVersion(CurrentSchemaVersion);
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

    private void CreateSchema()
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
                PRIMARY KEY (organization, build_id)
            );

            CREATE INDEX IF NOT EXISTS ix_builds_definition
                ON builds (organization, project, definition_id, finish_time);

            CREATE INDEX IF NOT EXISTS ix_builds_branch
                ON builds (organization, project, source_branch, finish_time);

            CREATE INDEX IF NOT EXISTS ix_builds_repo_def
                ON builds (repository_name, definition_name, finish_time);

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
                PRIMARY KEY (organization, run_id),
                FOREIGN KEY (organization, build_id)
                    REFERENCES builds (organization, build_id)
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
                PRIMARY KEY (organization, run_id, result_id),
                FOREIGN KEY (organization, run_id)
                    REFERENCES test_runs (organization, run_id)
            );

            CREATE INDEX IF NOT EXISTS ix_test_results_title
                ON test_results (organization, test_case_title);

            CREATE INDEX IF NOT EXISTS ix_test_results_outcome
                ON test_results (organization, outcome);

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
                UNIQUE (organization, test_case_title, branch)
            );

            CREATE INDEX IF NOT EXISTS ix_flaky_tests_status
                ON flaky_tests (organization, status);

            CREATE TABLE IF NOT EXISTS build_timeline_issues (
                organization TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                record_name TEXT NOT NULL,
                record_type TEXT NOT NULL,
                parent_name TEXT,
                record_result TEXT,
                issue_type TEXT NOT NULL,
                issue_message TEXT NOT NULL,
                issue_category TEXT,
                FOREIGN KEY (organization, build_id)
                    REFERENCES builds (organization, build_id)
            );

            CREATE INDEX IF NOT EXISTS ix_timeline_issues_build
                ON build_timeline_issues (organization, build_id);

            CREATE TABLE IF NOT EXISTS build_ingestion_tasks (
                organization TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                task_type TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                last_attempt_time TEXT,
                next_retry_time TEXT,
                completed_time TEXT,
                PRIMARY KEY (organization, build_id, task_type)
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_tasks_status
                ON build_ingestion_tasks (status, next_retry_time);

            CREATE TABLE IF NOT EXISTS pull_requests (
                repository TEXT NOT NULL,
                pr_number INTEGER NOT NULL,
                title TEXT,
                author TEXT,
                fetched_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (repository, pr_number)
            );

            CREATE INDEX IF NOT EXISTS ix_pull_requests_repo
                ON pull_requests (repository);

            CREATE TABLE IF NOT EXISTS known_issues (
                repository TEXT NOT NULL,
                issue_number INTEGER NOT NULL,
                title TEXT NOT NULL,
                error_message TEXT,
                error_pattern TEXT,
                build_retry INTEGER NOT NULL DEFAULT 0,
                exclude_console_log INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL DEFAULT 'open',
                closed_at TEXT,
                fetched_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (repository, issue_number)
            );

            CREATE INDEX IF NOT EXISTS ix_known_issues_repo
                ON known_issues (repository);

            CREATE TABLE IF NOT EXISTS helix_work_items (
                job_name TEXT NOT NULL,
                work_item_name TEXT NOT NULL,
                state TEXT NOT NULL,
                exit_code INTEGER,
                console_output_uri TEXT,
                files TEXT,
                PRIMARY KEY (job_name, work_item_name)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes a build and all associated data (test runs, test results, helix work items,
    /// timeline issues, ingestion tasks). This is a complete wipe of the build from the database.
    /// Build IDs are unique within an organization.
    /// </summary>
    public void DeleteBuild(string organization, int buildId)
    {
        using var transaction = Connection.BeginTransaction();

        // Delete helix work items referenced by test results for this build
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM helix_work_items
                WHERE (job_name, work_item_name) IN (
                    SELECT tr.helix_job_name, tr.helix_work_item_name
                    FROM test_results tr
                    JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                    WHERE r.organization = @org AND r.build_id = @buildId
                      AND tr.helix_job_name IS NOT NULL
                )
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        // Delete test results for this build
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM test_results
                WHERE (organization, run_id) IN (
                    SELECT organization, run_id FROM test_runs
                    WHERE organization = @org AND build_id = @buildId
                )
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        // Delete test runs
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM test_runs
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        // Delete timeline issues
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM build_timeline_issues
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        // Delete ingestion tasks
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM build_ingestion_tasks
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        // Delete the build itself
        using (var cmd = Connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}
