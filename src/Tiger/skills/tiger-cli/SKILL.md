---
name: tiger-cli
description: Use the tiger cli tool to query live Azure DevOps and Helix CI/CD data
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
| `azdo repo-builds` | Get builds for a repository |

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
