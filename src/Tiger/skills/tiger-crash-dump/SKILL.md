---
name: tiger-crash-dump
description: Identify and access crash dumps from CI test failures in Azure DevOps, for both normal test runs and Helix-based tests.
---

# Tiger Crash Dump Analysis Skill

This skill helps identify crash dumps produced during CI test failures and guides you to access them.

## Detecting Crash Dumps

Crash dumps are indicated by examining **error messages** or **stack traces** in the `test_results` table. Look for:

1. The phrase **"Test host crash detected"** in `error_message` or `stack_trace`
2. A file name ending in **`.dmp`** in the `error_message` or `stack_trace`

### Example queries to find crash dumps

```sql
-- Find test results mentioning crash dumps
SELECT tr.organization, tr.run_id, tr.result_id, tr.test_case_title, tr.error_message
FROM test_results tr
WHERE tr.error_message LIKE '%Test host crash detected%'
   OR tr.error_message LIKE '%.dmp%'
   OR tr.stack_trace LIKE '%.dmp%';

-- Find crash dumps for a specific build
SELECT tr.organization, tr.run_id, tr.result_id, tr.test_case_title,
       tr.error_message, tr.helix_job_name, tr.helix_work_item_name
FROM test_results tr
JOIN test_runs r ON r.organization = tr.organization AND r.run_id = tr.run_id
WHERE r.build_id = @buildId
  AND (tr.error_message LIKE '%Test host crash detected%'
       OR tr.error_message LIKE '%.dmp%'
       OR tr.stack_trace LIKE '%.dmp%');
```

## Accessing Crash Dumps

The method for retrieving the dump file depends on whether the test ran on Helix or not.

### Normal (non-Helix) tests

When `helix_job_name` is NULL, the crash dump is uploaded inside an **Azure DevOps pipeline artifact** associated with the test job.

**Download dump files directly:**

```shell
# Download all dump files from a build
tiger azdo download-dumps <build-id>

# Download dumps only from a specific test run
tiger azdo download-dumps <build-id> --run-id <run-id>

# Specify output directory
tiger azdo download-dumps <build-id> --output ./dumps
```

This command scans build artifacts, finds `.dmp` files, and downloads them individually without downloading entire artifacts.

The `tiger` CLI is available at `../../tiger` (relative to this skill).

**How it works under the hood:**
1. Lists all artifacts for the build
2. If `--run-id` is provided, filters to the artifact matching that test run's name
3. Fetches the file manifest for each artifact (supports both Container and PipelineArtifact types)
4. Downloads only files ending in `.dmp`

**Fallback — download the full artifact:**

If `download-dumps` doesn't work, you can download the entire artifact containing the dump:

```shell
# List artifacts to find the right one
tiger azdo artifacts <build-id>

# Download the full artifact (artifact name = test run name with underscores + "Attempt N Logs")
tiger azdo download <build-id> --artifact "<artifact-name>" --output ./logs.zip
```

As a last resort, use the AzDO REST API or the build's Artifacts tab in the web UI.

### Helix tests

When `helix_job_name` is NOT NULL, the crash dump is stored as a file in the Helix work item.

Use the `helix_work_items` table to find the dump file:

```sql
SELECT hwi.job_name, hwi.work_item_name, hwi.files
FROM helix_work_items hwi
WHERE hwi.job_name = @helixJobName
  AND hwi.work_item_name = @helixWorkItemName;
```

The `files` column contains a JSON array of file entries. Look for entries with a file name ending in `.dmp`. Each entry has a download URI.

## helix_work_items Table Schema

| Column | Type | Description |
|--------|------|-------------|
| job_name | TEXT | Helix job ID (matches `test_results.helix_job_name`) |
| work_item_name | TEXT | Helix work item name (matches `test_results.helix_work_item_name`) |
| state | TEXT | Work item state (e.g. "Passed", "Failed") |
| exit_code | INTEGER | Process exit code (non-zero often indicates a crash) |
| console_output_uri | TEXT | URI to the console log for the work item |
| files | TEXT | JSON array of uploaded files with download URIs |

Primary key: `(job_name, work_item_name)`

### Parsing the files column

The `files` column is a JSON array of objects. Each object has:
- `fileName` — the file name (look for `.dmp` suffix)
- `uri` — download URI for the file

Example:
```json
[
  {"fileName": "core.12345.dmp", "uri": "https://helix.dot.net/api/..."},
  {"fileName": "console.log", "uri": "https://helix.dot.net/api/..."}
]
```

### Example: finding dump URIs from Helix

```sql
-- Get all Helix work items with dump files for a build
SELECT hwi.job_name, hwi.work_item_name, hwi.files
FROM helix_work_items hwi
JOIN test_results tr ON tr.helix_job_name = hwi.job_name
                    AND tr.helix_work_item_name = hwi.work_item_name
JOIN test_runs r ON r.organization = tr.organization AND r.run_id = tr.run_id
WHERE r.build_id = @buildId
  AND hwi.files LIKE '%.dmp%';
```

Then parse the JSON in the `files` column to extract the URI for the `.dmp` file.

## Summary

| Scenario | Where to find the dump |
|----------|----------------------|
| Normal test (`helix_job_name` IS NULL) | AzDO build artifact named after the test run |
| Helix test (`helix_job_name` IS NOT NULL) | `helix_work_items.files` JSON array |
