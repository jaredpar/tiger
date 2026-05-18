---
name: tiger-data
description: Used for querying CI/CD build and test data from Azure DevOps
---

# Tiger CI Database Skill

You have access to a SQLite database containing CI/CD build, test, and timeline data from Azure DevOps.
The database is located at `~/.tiger/tiger.db`. Use `sqlite3 ~/.tiger/tiger.db` to query it.

## Database Schema

### builds
Completed CI builds from Azure DevOps.

| Column | Type | Description |
|--------|------|-------------|
| organization | TEXT | AzDO organization (e.g. "dnceng-public") |
| project | TEXT | AzDO project (e.g. "public") |
| build_id | INTEGER | Unique build ID |
| build_number | TEXT | Build number string |
| definition_name | TEXT | Pipeline/definition name |
| definition_id | INTEGER | Pipeline definition ID |
| status | TEXT | Build status (always "completed") |
| result | TEXT | "succeeded", "failed", "partiallySucceeded", "canceled" |
| source_branch | TEXT | Branch (e.g. "refs/heads/main", "refs/pull/123/merge") |
| source_version | TEXT | Git commit SHA |
| repository_name | TEXT | Repository (e.g. "dotnet/roslyn") |
| pr_number | INTEGER | PR number if this is a PR build, NULL otherwise |
| finish_time | TEXT | ISO 8601 finish time |
| ingested_at | TEXT | When the build was added to the DB |

Primary key: `(organization, project, build_id)`

### test_runs
Test run summaries per build. A build can have multiple test runs (one per job/leg).

| Column | Type | Description |
|--------|------|-------------|
| organization | TEXT | AzDO organization |
| project | TEXT | AzDO project |
| build_id | INTEGER | FK to builds |
| run_id | INTEGER | Test run ID |
| run_name | TEXT | Test run/job name |
| total_tests | INTEGER | Total test count |
| passed_tests | INTEGER | Passed count |
| failed_tests | INTEGER | Failed count |
| skipped_tests | INTEGER | Skipped count |

Primary key: `(organization, project, run_id)`

### test_results
Individual test failure results. Only failed tests are stored.

| Column | Type | Description |
|--------|------|-------------|
| organization | TEXT | AzDO organization |
| project | TEXT | AzDO project |
| run_id | INTEGER | FK to test_runs |
| result_id | INTEGER | Test result ID |
| test_case_title | TEXT | Fully qualified test name |
| outcome | TEXT | Always "Failed" (only failures stored) |
| error_message | TEXT | Error message (may be NULL) |
| stack_trace | TEXT | Stack trace (may be NULL) |
| helix_job_name | TEXT | Helix job ID if test ran on Helix |
| helix_work_item_name | TEXT | Helix work item name if applicable |

Primary key: `(organization, project, run_id, result_id)`

### build_timeline_issues
Errors and warnings from the AzDO build timeline (jobs and tasks).

| Column | Type | Description |
|--------|------|-------------|
| organization | TEXT | AzDO organization |
| project | TEXT | AzDO project |
| build_id | INTEGER | FK to builds |
| record_name | TEXT | Job or task name that produced the issue |
| record_type | TEXT | "Job" or "Task" |
| parent_name | TEXT | Parent job name (for Task records) |
| record_result | TEXT | "failed", "succeeded", etc. |
| issue_type | TEXT | "error" or "warning" |
| issue_message | TEXT | The error/warning message |
| issue_category | TEXT | Issue category (may be NULL) |

### build_ingestion_tasks
Tracks async ingestion of detailed data per build.

| Column | Type | Description |
|--------|------|-------------|
| organization | TEXT | AzDO organization |
| project | TEXT | AzDO project |
| build_id | INTEGER | FK to builds |
| task_type | TEXT | "tests", "timeline", or "helix" |
| status | TEXT | "pending", "running", "complete", "failed", "abandoned" |
| attempts | INTEGER | Number of attempts so far |
| last_error | TEXT | Last error message if failed |

Primary key: `(organization, project, build_id, task_type)`

## Common Queries

### Recent failed builds
```sql
SELECT build_id, definition_name, result, repository_name, finish_time
FROM builds
WHERE result = 'failed'
ORDER BY finish_time DESC
LIMIT 20;
```

### Top failing tests (by number of builds affected)
```sql
SELECT tr.test_case_title, COUNT(DISTINCT r.build_id) as build_count
FROM test_results tr
JOIN test_runs r ON tr.organization = r.organization
    AND tr.project = r.project AND tr.run_id = r.run_id
WHERE tr.outcome = 'Failed'
GROUP BY tr.test_case_title
ORDER BY build_count DESC
LIMIT 20;
```

### Failed tests for a specific build
```sql
SELECT tr.test_case_title, tr.error_message, tr.helix_job_name
FROM test_results tr
JOIN test_runs r ON tr.organization = r.organization
    AND tr.project = r.project AND tr.run_id = r.run_id
WHERE r.build_id = BUILD_ID AND tr.outcome = 'Failed'
ORDER BY tr.test_case_title;
```

### Timeline errors for a build (failed jobs and their errors)
```sql
SELECT parent_name, record_name, issue_type, issue_message
FROM build_timeline_issues
WHERE build_id = BUILD_ID AND issue_type = 'error'
ORDER BY parent_name, record_name;
```

### Builds where a specific test failed
```sql
SELECT b.build_id, b.definition_name, b.result, b.finish_time, b.source_branch
FROM test_results tr
JOIN test_runs r ON tr.organization = r.organization
    AND tr.project = r.project AND tr.run_id = r.run_id
JOIN builds b ON r.organization = b.organization
    AND r.project = b.project AND r.build_id = b.build_id
WHERE tr.test_case_title = 'FULL_TEST_NAME' AND tr.outcome = 'Failed'
ORDER BY b.finish_time DESC;
```

### Tests that fail on a specific branch
```sql
SELECT tr.test_case_title, COUNT(*) as fail_count
FROM test_results tr
JOIN test_runs r ON tr.organization = r.organization
    AND tr.project = r.project AND tr.run_id = r.run_id
JOIN builds b ON r.organization = b.organization
    AND r.project = b.project AND r.build_id = b.build_id
WHERE tr.outcome = 'Failed' AND b.source_branch = 'refs/heads/main'
GROUP BY tr.test_case_title
ORDER BY fail_count DESC
LIMIT 20;
```

### Failed builds for a specific repo
```sql
SELECT build_id, definition_name, result, finish_time, pr_number
FROM builds
WHERE repository_name = 'dotnet/roslyn' AND result = 'failed'
ORDER BY finish_time DESC
LIMIT 20;
```

### Ingestion status summary
```sql
SELECT task_type, status, COUNT(*) as count
FROM build_ingestion_tasks
GROUP BY task_type, status
ORDER BY task_type, status;
```

### Helix work items for a failed test
```sql
SELECT tr.test_case_title, tr.helix_job_name, tr.helix_work_item_name
FROM test_results tr
WHERE tr.helix_job_name IS NOT NULL AND tr.outcome = 'Failed'
ORDER BY tr.test_case_title
LIMIT 20;
```

The Helix console log URL can be constructed as:
`https://helix.dot.net/api/2019-06-17/jobs/{helix_job_name}/workitems/{helix_work_item_name}/console`

## Tips

- All tables are keyed by `(organization, project)` for multi-org support
- Times are stored as ISO 8601 strings in UTC
- Only failed test results are stored (not passing tests)
- The `build_ingestion_tasks` table shows whether test/timeline/helix data is available for a build
- Use `LIKE '%pattern%'` for fuzzy matching on test names, definition names, etc.
- PR builds have `source_branch` like `refs/pull/123/merge` and `pr_number` set
