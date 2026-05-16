# Tiger — TODO

Work items for building the Tiger infrastructure management CLI.
Items are ordered by dependency — work top-to-bottom.

## Phase 1: Foundation — Rename & Restructure

- [x] **rename-solution** — Rename `Pipeline.slnx` → `Tiger.slnx`. Rename projects:
  `Pipeline` → `Tiger`, `Pipeline.Core` → `Tiger.Core`, `Pipeline.Mcp` → merged into Tiger.
  Update all namespaces from `Pipeline.*` → `Tiger.*`. Tool command name becomes `tiger`.
  Drop `Scratch` project or rename to `Tiger.Scratch`.

- [x] **cli-framework** — Convert from single-shot command app to a long-running application
  using Spectre.Console (or similar). Add subcommand routing, interactive menus, and the
  ability to keep running (hosting the poller + MCP server). Existing AzDO/Helix query
  commands persist as one-shot subcommands (e.g. `tiger azdo builds` still works exactly
  as today). This is the foundation for `tiger status`, `tiger report`,
  `tiger mcp serve`, etc.

- [x] **config-system** — Create config at `~/.tiger/config.json`. Schema supports a list of
  AzDO org/project pairs to monitor, plus poll interval. Replace hardcoded
  `dnceng-public`/`public` defaults with config-driven values.

- [x] **sqlite-schema** — Design SQLite schema at `~/.tiger/tiger.db`. All tables keyed by
  `(organization, project)` so we can monitor multiple AzDO instances. Core tables:
  `builds`, `test_runs`, `test_results`, `flaky_tests`.

- [x] **data-source-interface** — Extract `IBuildDataSource` interface from `AzdoClient`
  (analysis-oriented methods: builds, test failures, test summary). Implement it on both
  `AzdoClient` (live) and a new `SqliteBuildDataSource` (cached). Consumers use the
  interface so they work seamlessly against either backend.

## Phase 2: Background Poller

- [x] **poller-service** — `BackgroundService` that polls each configured AzDO org/project
  for completed builds. Tracks watermark (last-seen build ID) per org/project in SQLite.

- [x] **poller-ingestion** — On new completed build: fetch test runs + test results via
  `AzdoClient`, insert into SQLite.

- [x] **poller-lifecycle** — Poller starts automatically when tiger runs. Add `tiger status`
  command showing poller health, last poll time, build count.

## Phase 3: Flaky Test Detection

- [ ] **flaky-detection** — Query SQLite for tests that flip pass↔fail on the same branch
  within a configurable window. Mark them in `flaky_tests` table.

- [ ] **flaky-pr-comments** — When a flaky test is detected in a PR build, post a comment
  on the PR via `gh pr comment`.

- [ ] **flaky-issue-filing** — For persistent flaky tests (above threshold), file a GitHub
  issue with failure history.

- [ ] **flaky-fix-prs** — (Stretch) Open PRs to skip/quarantine flaky tests.

## Phase 4: Reporting

- [ ] **report-top-failures** — `tiger report failures` — top N failing tests across
  recent builds, grouped by test name.

- [ ] **report-infra-flakiness** — `tiger report infra` — infrastructure failures
  (timeouts, machine issues, helix work item failures).

- [ ] **report-trends** — `tiger report trends` — failure rate over time (daily/weekly).

## Phase 5: MCP/HTTP Server & Copilot Console

- [ ] **mcp-http-server** — Serve MCP tools over HTTP (Kestrel). Starts with the main
  process. `tiger mcp serve` prints the port/URL for manual connection.

- [ ] **mcp-sqlite-tools** — MCP tools to query SQLite: list flaky tests, failure history,
  build status, search test results.

- [ ] **mcp-live-tools** — Keep existing AzDO/Helix MCP tools for live queries. Adapt to
  Tiger naming and multi-org config.

- [ ] **mcp-skills** — Skill definitions for manual triage workflows (investigate failure,
  mark as known flaky, correlate with infra issues).
