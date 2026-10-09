---
name: tiger-cli
description: Use the tiger cli tool to query live Azure DevOps and Helix CI/CD data and retry pipeline builds
---

# Tiger CLI Skill

The Tiger CLI tool can query **live** Azure DevOps and Helix data. Use it when you need
real-time CI/CD information that may not yet be in the local database.

## Location

The Tiger executable is located at `../../tiger` relative to this skill file. Run it as:

```
dotnet run --project src/Tiger/Tiger.csproj --
```

All output is structured **JSON** suitable for programmatic consumption.

## Available Commands

### `tiger azdo` — Azure DevOps queries

| Command | Description |
|---------|-------------|
| `azdo builds` | Get recent builds, optionally filtered by definition ID |
| `azdo tests <build-id>` | Get test failures for a build |
| `azdo test-summary <build-id>` | Get test counts per job for a build |
| `azdo timeline <build-id>` | Get the timeline for a build |
| `azdo artifacts <build-id>` | Get artifacts for a build |
| `azdo jobs <build-id>` | Get job records from a build timeline |
| `azdo download <build-id>` | Download an artifact from a build |
| `azdo download-dumps <build-id>` | Download crash dump files from build artifacts |
| `azdo pr-builds` | Get builds for a pull request |
| `azdo repo-builds` | Get builds for a repository; use `--repository-type TfsGit` for Azure Repos |
| `azdo retry [build-id]` | Retry a build by ID or the latest PR build with `--pr <number> --repo <owner/repo>`; use `--failed-only` to retry failed jobs in the existing run |

### Retrying pipelines

`azdo retry <build-id>` queues a new build using the original pipeline, branch,
commit, and build/template parameters. The response is the new build as JSON,
including its ID and results-page URI.

`azdo retry <build-id> --failed-only` asks Azure DevOps to retry failed jobs in the
existing run, preserving successful jobs. The response describes that same build.
Azure DevOps determines eligibility and dependent-job behavior; unsupported or
ineligible retries fail rather than falling back to a full rerun.

Alternatively, `azdo retry --pr <number> --repo <owner/repo> --org <organization>`
queries live Azure DevOps and retries the single most recently queued build for
that PR in the selected organization/project. Selection spans all matching pipelines,
regardless of build status or result. `--failed-only` also works with this form.
`--repo` is required with `--pr`; a build ID and `--pr` are mutually exclusive.
No matching build is an error and does not start a retry.
Azure DevOps queue-time variable policies can reject full retries when copying
the original build's PR system variables. The server's error details are reported;
the command does not bypass pipeline policies or fall back to another retry mode.

Both modes accept `--org` and `--project` and require permission to queue/retry
builds (defaults: `dnceng-public` and `public`). These commands start pipeline
execution; they do not retry local ingestion.
Use them only when the user has authorized the retry.

### `tiger helix` — Helix queries

| Command | Description |
|---------|-------------|
| `helix workitems` | List work items for a Helix job |
| `helix console` | Get console output for a Helix work item |
| `helix files` | List or download files from a Helix work item |

## Getting Detailed Help

Each command supports `--help` for full argument and option details. Always run
`--help` on a command before using it to discover required arguments and available
options. For example:

```
dotnet run --project src/Tiger/Tiger.csproj -- azdo tests --help
dotnet run --project src/Tiger/Tiger.csproj -- helix workitems --help
```
