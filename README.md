# Tiger

Tiger is a CLI tool for managing CI/CD infrastructure. It polls Azure DevOps pipelines for completed builds, stores results in a local SQLite database, detects flaky tests, and serves MCP tools for a copilot console experience.

See `docs/architecture.md` for full design details and `docs/todo.md` for the work plan.

## Prerequisites

Install the .NET 10 SDK and Git to build and run from this repository.

1. Be connected to the VPN.
2. This tool uses Azure Identity for authentication — use `az login` to authenticate. You can also use `az account set -s <subscription name>` to set the subscription.

## Building

```
dotnet build Tiger.slnx
```

## Running

Use the launcher in the repository root as the primary local entry point.
Every invocation fetches from GitHub (`origin`), fast-forwards the current branch
to `origin/main`, builds Tiger, and runs it with all supplied arguments.
If updating or building fails, the launcher stops without running Tiger.

From Command Prompt:

```cmd
tiger.cmd azdo --help
```

From PowerShell:

```powershell
.\tiger.cmd azdo --help
```

From Bash (Linux/macOS or Git Bash):

```bash
./tiger.sh azdo --help
```

Omit arguments to open the interactive dashboard. Both launchers resolve the
repository from their own location and return Tiger's exit code.

To run directly without automatically updating from GitHub:

```
dotnet run --project src/Tiger
```

## Waiting for Azure DevOps pipelines and jobs

```
tiger azdo wait 12345 --org dnceng-public --project public
tiger azdo wait 12345 --job-name "test"
tiger azdo wait 12345 --job-id "11111111-1111-1111-1111-111111111111"
tiger azdo wait 12345 --job-name "test" --poll-interval 15 --timeout 3600
```

The positional ID identifies a specific pipeline run (build), not a pipeline
definition. Without job filters, waits for the entire run. `--job-name` matches
display names by case-insensitive substring and waits for **all** matching jobs.
`--job-id` selects the exact timeline record GUID shown by `tiger azdo jobs`.
When both filters are supplied, both must match.

Each check discovers matching jobs from the current live timeline. If none have
appeared yet, waiting continues until they appear or the build completes; no
matches in a completed build is an error. With `--job-name`, Azure DevOps only adds
Job records once their stage/phase starts, so waiting also continues while any
stage or phase is unfinished and has no jobs yet (it could still produce a match).
Unrelated jobs that are already running do not block completion. `--job-id` alone
returns as soon as that job completes.

JSON progress snapshots on stderr list matched job IDs, names, states, and results,
plus `pendingJobSources` (unexpanded stages/phases still being waited on), when they
change (including the initial list). Stdout contains one final JSON
object with `build` and `jobs` (an empty list in whole-run mode). Completion returns
exit code 0 regardless of result, including failed, canceled, or skipped jobs;
inspect the JSON result to distinguish outcomes. Errors, timeout, and cancellation
return a nonzero exit code. Checks default to every 10 seconds, with no timeout.
Both modes accept `--org` and `--project`.

## Retrying Azure DevOps pipelines

```
tiger azdo retry 12345 --org dnceng-public --project public
tiger azdo retry 12345 --failed-only --org dnceng-public --project public
tiger azdo retry --pr 42 --repo dotnet/roslyn --org dnceng-public --project public
tiger azdo retry --pr 42 --repo dotnet/roslyn --org dnceng-public --failed-only
```

The default queues a full rerun with a new build ID using the original pipeline,
branch, commit, and parameters. `--failed-only` retries failed jobs in the existing
run without rerunning successful jobs, subject to Azure DevOps eligibility and
dependency rules. Both commands return the resulting build as JSON and require
permission to queue/retry builds. An unsupported failed-job retry fails; it does
not fall back to a full rerun.
Full retries can also be rejected by Azure DevOps queue-time variable policies,
including restrictions on copying PR system variables from the original build.
The command reports the server's error details without bypassing these policies.

Use either a build ID or `--pr` with `--repo owner/repo`, not both. PR mode queries
live Azure DevOps and targets the single most recently queued build for that PR
in the selected organization/project, across all pipelines and regardless of
status or result. If no build matches, the command fails without retrying anything.
Both forms accept `--org` and `--project` (defaults: `dnceng-public` and `public`).