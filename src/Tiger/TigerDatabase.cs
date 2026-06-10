using Microsoft.Data.Sqlite;

namespace Tiger;

/// <summary>
/// Manages the SQLite database at ~/.tiger/tiger.db.
/// Handles connection pooling and schema migration.
/// Each operation creates its own connection from a pool managed by
/// Microsoft.Data.Sqlite, so no application-level locking is needed.
/// </summary>
public sealed class TigerDatabase : IDisposable
{
    public const int CurrentSchemaVersion = 10;

    public string DatabasePath { get; }
    private string ConnectionString { get; }

    private TigerDatabase(string databasePath, string connectionString)
    {
        DatabasePath = databasePath;
        ConnectionString = connectionString;
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

        var db = new TigerDatabase(databasePath, connectionString);

        // Initialize schema and WAL mode using a one-off connection
        db.WithCommand(cmd =>
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        });
        db.EnsureSchema();
        return db;
    }

    /// <summary>
    /// Executes an action with a fresh command. The connection and command
    /// are created from the pool and disposed after the action completes.
    /// </summary>
    public void WithCommand(Action<SqliteCommand> action)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        action(cmd);
    }

    /// <summary>
    /// Executes a function with a fresh command and returns its result.
    /// </summary>
    public T WithCommand<T>(Func<SqliteCommand, T> func)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        return func(cmd);
    }

    /// <summary>
    /// Executes an action within a transaction. The connection, transaction,
    /// and disposal are managed automatically. Commits on success, rolls back
    /// on exception.
    /// </summary>
    public void WithTransaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            action(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Executes a function within a transaction and returns its result.
    /// </summary>
    public T WithTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> func)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = func(connection, transaction);
            transaction.Commit();
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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
        MigrateSchema();
        SetSchemaVersion(CurrentSchemaVersion);
    }

    private void MigrateSchema()
    {
        // v9: add duration_seconds to test_runs
        TryAddColumn("test_runs", "duration_seconds", "REAL");
    }

    private void TryAddColumn(string table, string column, string type)
    {
        try
        {
            WithCommand(cmd =>
            {
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
                cmd.ExecuteNonQuery();
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Column already exists
        }
    }

    private int GetSchemaVersion()
    {
        return WithCommand(cmd =>
        {
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });
    }

    private void SetSchemaVersion(int version)
    {
        WithCommand(cmd =>
        {
            cmd.CommandText = $"PRAGMA user_version = {version};";
            cmd.ExecuteNonQuery();
        });
    }

    private void CreateSchema()
    {
        WithCommand(cmd =>
        {
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
                ingestion_tasks_complete INTEGER NOT NULL DEFAULT 0,
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
                duration_seconds REAL,
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
                is_helix_work_item INTEGER NOT NULL DEFAULT 0,
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
                is_complete INTEGER NOT NULL DEFAULT 0,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                last_attempt_time TEXT,
                next_retry_time TEXT,
                completed_time TEXT,
                PRIMARY KEY (organization, build_id, task_type)
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_tasks_status
                ON build_ingestion_tasks (is_complete, next_retry_time);

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
                is_deadletter INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (job_name, work_item_name)
            );
            """;
            cmd.ExecuteNonQuery();
        });

        WithCommand(cmd =>
        {
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS build_analyses (
                organization TEXT NOT NULL,
                build_id INTEGER NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                category TEXT,
                confidence TEXT,
                diagnosis_summary TEXT,
                log_path TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                completed_at TEXT,
                PRIMARY KEY (organization, build_id),
                FOREIGN KEY (organization, build_id) REFERENCES builds (organization, build_id)
            );
            """;
            cmd.ExecuteNonQuery();
        });

        WithCommand(cmd =>
        {
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agent_tasks (
                session_id TEXT NOT NULL PRIMARY KEY,
                repository TEXT NOT NULL,
                test_name TEXT,
                file_path TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes a build and all associated data (test runs, test results, helix work items,
    /// timeline issues, ingestion tasks). This is a complete wipe of the build from the database.
    /// Build IDs are unique within an organization.
    /// </summary>
    public void DeleteBuild(string organization, int buildId)
    {
        WithTransaction((conn, tx) =>
        {
            // Delete helix work items referenced by test results for this build
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM test_runs
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            }

            // Delete timeline issues
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM build_timeline_issues
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            }

            // Delete ingestion tasks
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM build_ingestion_tasks
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            }

            // Delete build analyses
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM build_analyses
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            }

            // Delete the build itself
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM builds
                    WHERE organization = @org AND build_id = @buildId
                    """;
                cmd.Parameters.AddWithValue("@org", organization);
                cmd.Parameters.AddWithValue("@buildId", buildId);
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Inserts a new build analysis row with status 'pending'.
    /// </summary>
    public void InsertBuildAnalysis(string organization, int buildId)
    {
        WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO build_analyses (organization, build_id, status)
                VALUES (@org, @buildId, 'pending')
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Updates an existing build analysis row.
    /// </summary>
    public void UpdateBuildAnalysis(
        string organization,
        int buildId,
        string status,
        string? category = null,
        string? confidence = null,
        string? diagnosisSummary = null,
        string? logPath = null)
    {
        WithCommand(cmd =>
        {
            cmd.CommandText = """
                UPDATE build_analyses
                SET status = @status,
                    category = @category,
                    confidence = @confidence,
                    diagnosis_summary = @diagnosisSummary,
                    log_path = @logPath,
                    completed_at = CASE WHEN @status IN ('complete', 'failed', 'skipped') THEN datetime('now') ELSE completed_at END
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@confidence", (object?)confidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@diagnosisSummary", (object?)diagnosisSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@logPath", (object?)logPath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes a build analysis row so it can be re-queued.
    /// </summary>
    public void DeleteBuildAnalysis(string organization, int buildId)
    {
        WithCommand(cmd =>
        {
            cmd.CommandText = """
                DELETE FROM build_analyses
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Records an agent task that was submitted via <c>gh agent-task create</c>.
    /// </summary>
    public void InsertAgentTask(string sessionId, string repository, string? testName, string? filePath)
    {
        WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO agent_tasks (session_id, repository, test_name, file_path)
                VALUES (@sessionId, @repo, @testName, @filePath)
                """;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@testName", (object?)testName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@filePath", (object?)filePath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Returns all tracked agent task session IDs.
    /// </summary>
    public HashSet<string> GetAgentTaskSessionIds()
    {
        return WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT session_id FROM agent_tasks";
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
            return ids;
        });
    }

    /// <summary>
    /// Returns recent build analyses joined with build info.
    /// </summary>
    public List<BuildAnalysisInfo> GetRecentAnalyses(int limit = 50)
    {
        return WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT ba.organization, ba.build_id, ba.status, ba.category, ba.confidence,
                       ba.diagnosis_summary, ba.log_path, ba.created_at, ba.completed_at,
                       b.project, b.definition_name, b.build_number, b.source_branch
                FROM build_analyses ba
                JOIN builds b ON ba.organization = b.organization AND ba.build_id = b.build_id
                ORDER BY ba.created_at DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<BuildAnalysisInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new BuildAnalysisInfo
                {
                    Organization = reader.GetString(0),
                    BuildId = reader.GetInt32(1),
                    Status = reader.GetString(2),
                    Category = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Confidence = reader.IsDBNull(4) ? null : reader.GetString(4),
                    DiagnosisSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
                    LogPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CreatedAt = reader.GetString(7),
                    CompletedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Project = reader.GetString(9),
                    DefinitionName = reader.GetString(10),
                    BuildNumber = reader.GetString(11),
                    SourceBranch = reader.GetString(12),
                });
            }
            return results;
        });
    }

    public BuildAnalysisInfo? GetBuildAnalysis(string organization, int buildId)
    {
        return WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT ba.organization, ba.build_id, ba.status, ba.category, ba.confidence,
                       ba.diagnosis_summary, ba.log_path, ba.created_at, ba.completed_at,
                       b.project, b.definition_name, b.build_number, b.source_branch
                FROM build_analyses ba
                JOIN builds b ON ba.organization = b.organization AND ba.build_id = b.build_id
                WHERE ba.organization = @org AND ba.build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new BuildAnalysisInfo
            {
                Organization = reader.GetString(0),
                BuildId = reader.GetInt32(1),
                Status = reader.GetString(2),
                Category = reader.IsDBNull(3) ? null : reader.GetString(3),
                Confidence = reader.IsDBNull(4) ? null : reader.GetString(4),
                DiagnosisSummary = reader.IsDBNull(5) ? null : reader.GetString(5),
                LogPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetString(7),
                CompletedAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                Project = reader.GetString(9),
                DefinitionName = reader.GetString(10),
                BuildNumber = reader.GetString(11),
                SourceBranch = reader.GetString(12),
            };
        });
    }

    public void Dispose()
    {
        // Clear the connection pool so pooled connections release the file lock.
        // This is critical for tests that delete the DB file after use.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
    }
}

public sealed class BuildAnalysisInfo
{
    public required string Organization { get; init; }
    public required int BuildId { get; init; }
    public required string Status { get; init; }
    public string? Category { get; init; }
    public string? Confidence { get; init; }
    public string? DiagnosisSummary { get; init; }
    public string? LogPath { get; init; }
    public required string CreatedAt { get; init; }
    public string? CompletedAt { get; init; }
    public required string Project { get; init; }
    public required string DefinitionName { get; init; }
    public required string BuildNumber { get; init; }
    public required string SourceBranch { get; init; }
}
