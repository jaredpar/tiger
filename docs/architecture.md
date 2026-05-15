# Tiger — Architecture & Design

This file captures architecture decisions so work can resume across sessions/machines.

## What Tiger Is

A unified CLI tool (`tiger.exe`) for managing CI/CD infrastructure. It:
1. Polls Azure DevOps pipelines for completed builds
2. Stores build/test results in a local SQLite database
3. Detects flaky tests and takes action (PR comments, issues, fix PRs via GitHub)
4. Generates reports (top failures, infra flakiness, trends)
5. Serves MCP tools over HTTP for a copilot console experience

## Single Process Model

Everything runs in one process:
- **CLI commands** — `tiger status`, `tiger report ...`, `tiger mcp serve`
- **Background poller** — `BackgroundService` that polls AzDO on interval
- **HTTP/MCP server** — Kestrel serving MCP tools, always running

When you run `tiger`, the process starts the poller + HTTP server and keeps running.
CLI commands like `tiger report` query the SQLite DB and exit.

## Naming Conventions

| Context | Style | Example |
|---------|-------|---------|
| Projects/Namespaces | PascalCase | `Tiger.Core`, `namespace Tiger.Core` |
| User-facing paths | lowercase | `~/.tiger/`, `tiger.exe` |
| Tool command | lowercase | `tiger` |
| Solution | PascalCase | `Tiger.slnx` |

## Directory Layout (Target)

```
Tiger.slnx
src/
  Tiger/              — Single project: CLI + HTTP host + MCP tools (OutputType=Exe, PackAsTool)
    Program.cs        — Entry point, CLI routing
    AzdoClient.cs     — HTTP client for AzDO REST API
    HelixClient.cs    — HTTP client for Helix API
    TigerUtils.cs     — Context/credential helpers
    Mcp/              — MCP tool definitions
    Commands/         — CLI command handlers (future)
    Poller/           — BackgroundService for AzDO polling (future)
    Database/         — SQLite schema, migrations, query helpers (future)
    Config/           — Configuration model + loading (future)
  Tiger.Tests/        — xUnit test project
```

## Configuration (`~/.tiger/config.json`)

```json
{
  "pollIntervalSeconds": 300,
  "sources": [
    {
      "organization": "dnceng-public",
      "project": "public",
      "repositories": ["dotnet/roslyn", "dotnet/runtime"]
    },
    {
      "organization": "devdiv",
      "project": "DevDiv",
      "repositories": ["dotnet/razor"]
    }
  ]
}
```

## SQLite Schema (Multi-Org Aware)

All tables use `(organization, project)` as key prefix:

```sql
CREATE TABLE builds (
    organization TEXT NOT NULL,
    project TEXT NOT NULL,
    build_id INTEGER NOT NULL,
    build_number TEXT NOT NULL,
    definition_name TEXT NOT NULL,
    status TEXT NOT NULL,
    result TEXT,
    source_branch TEXT NOT NULL,
    finish_time TEXT,
    ingested_at TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (organization, project, build_id)
);

CREATE TABLE test_runs (
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
    FOREIGN KEY (organization, project, build_id) REFERENCES builds(organization, project, build_id)
);

CREATE TABLE test_results (
    organization TEXT NOT NULL,
    project TEXT NOT NULL,
    run_id INTEGER NOT NULL,
    result_id INTEGER NOT NULL,
    test_case_title TEXT NOT NULL,
    outcome TEXT NOT NULL,
    error_message TEXT,
    stack_trace TEXT,
    helix_job_name TEXT,
    helix_work_item_name TEXT,
    PRIMARY KEY (organization, project, run_id, result_id),
    FOREIGN KEY (organization, project, run_id) REFERENCES test_runs(organization, project, run_id)
);

CREATE TABLE poll_watermarks (
    organization TEXT NOT NULL,
    project TEXT NOT NULL,
    last_build_id INTEGER NOT NULL DEFAULT 0,
    last_poll_time TEXT,
    PRIMARY KEY (organization, project)
);

CREATE TABLE flaky_tests (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    organization TEXT NOT NULL,
    project TEXT NOT NULL,
    test_case_title TEXT NOT NULL,
    branch TEXT NOT NULL,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    flip_count INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'active',  -- active, resolved, ignored
    github_issue_url TEXT,
    UNIQUE (organization, project, test_case_title, branch)
);
```

## Data Access Abstraction

A key design goal: operations that analyze build data should work seamlessly against either
**live AzDO data** or **cached SQLite data**. To achieve this:

- Extract an interface (e.g., `IBuildDataSource`) from the read/query methods of `AzdoClient`
- `AzdoClient` implements the interface by hitting the live AzDO REST API
- A `SqliteBuildDataSource` implements the same interface by querying the local SQLite DB

Methods that belong on the interface (analysis-oriented):
- `GetBuildsForRepositoryAsync`
- `GetBuildsForPullRequestAsync`
- `GetRecentBuildsAsync`
- `GetTestFailuresAsync`
- `GetTestSummaryByJobAsync`

Methods that stay on `AzdoClient` only (live-only operations):
- `GetTimelineAsync` (detailed timeline data not cached)
- `GetArtifactsAsync` / `DownloadArtifactAsync` (artifact download is live-only)

This lets MCP tools, reports, and the flaky-test detector accept an `IBuildDataSource` and
work against whichever backend is appropriate — cached for speed/offline, live for fresh data.

## Authentication

- **AzDO**: `DefaultAzureCredential` (tenant `72f988bf-86f1-41af-91ab-2d7cd011db47`)
- **Helix**: Bearer token from `~/.tiger/helix.txt`
- **GitHub**: User's `gh` CLI auth (invoked via process)

## Technology Stack

- .NET 10 (net10.0)
- `Microsoft.Data.Sqlite` for DB access
- `Microsoft.Extensions.Hosting` for BackgroundService + DI
- `ModelContextProtocol` for MCP server
- Kestrel (ASP.NET) for HTTP transport
- `Azure.Identity` for AzDO auth

## Current State (Before Changes)

- Solution: `Pipeline.slnx` with 4 projects
- Projects: `Pipeline`, `Pipeline.Core`, `Pipeline.Mcp`, `Scratch`
- Existing code: AzDO client, Helix client, MCP tools, CLI — all functional
- No SQLite, no poller, no config system yet
- MCP uses stdio transport (needs to become HTTP)
