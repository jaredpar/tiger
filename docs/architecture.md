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

Build IDs are unique within an organization in Azure DevOps.
All tables use `(organization, build_id)` as the key for build-scoped data.
`project` is stored as data but is not part of primary keys (except `poll_watermarks`
which is keyed by org+project since polling is per-project).

For the full schema with all tables, columns, indexes, and example queries,
see the agent skill file at `src/Tiger/skills/tiger-data/SKILL.md`.

## Data Access

`AzdoClient` provides all live AzDO REST API access. There is no abstraction layer —
consumers use `AzdoClient` directly. Cached data is queried directly from SQLite.

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

## Interactive Browser UI

The interactive browser (`BuildBrowser`, `TestBrowser`, etc.) follows a page-based
navigation model. Each page renders content and handles input, returning a `NavAction`
(Push, Back, or Replace).

### Hotkey Menus

**Hotkeys are always displayed at the bottom**, below all content. This applies to both
selectable lists and content displays.

#### Selectable Lists (Build list, Test list)

Use `BrowserUI.SelectWithEscape` with `extraKeys` for hotkey dispatch and the `hotkeys`
parameter for the bottom label:

```csharp
AnsiConsole.Clear();
AnsiConsole.MarkupLine("[bold underline]Builds[/]");
AnsiConsole.WriteLine();

// ... render any status/filter info ...

var hotkeys = "[blue]E[/] Edit filter   [blue]F[/] Filter menu   [blue]H[/] Help";
var selected = BrowserUI.SelectWithEscape("Select a build:", choices,
    extraKeys: new Dictionary<ConsoleKey, int> {
        { ConsoleKey.E, -5 },
        { ConsoleKey.F, -2 },
        { ConsoleKey.H, -3 },
    },
    useMarkup: true,
    hotkeys: hotkeys);

if (selected == -5) { /* handle E */ }
if (selected == -2) { /* handle F */ }
if (selected < 0) return NavAction.Back.Instance; // Esc or B
```

`SelectWithEscape` renders the hotkey label at the bottom: `"{hotkeys}   [blue]↑↓[/] Navigate  [blue]Enter[/] Select  [blue]Esc[/] Back"`.

#### Content Displays (Timeline/Jobs, Service Log)

Use a `while (true)` loop with `Console.ReadKey(true)`. Render all content first,
then the hotkey bar at the bottom:

```csharp
while (true)
{
    AnsiConsole.Clear();
    AnsiConsole.MarkupLine("[bold underline]Title[/]");
    AnsiConsole.WriteLine();

    // ... render content ...

    // Hotkey menu always at the bottom
    AnsiConsole.MarkupLine("  [blue]E[/] Errors only   [blue]T[/] Full messages   [blue]Esc[/] Back");

    var key = Console.ReadKey(true);
    if (key.Key is ConsoleKey.Escape or ConsoleKey.B)
        return NavAction.Back.Instance;
    if (key.Key == ConsoleKey.E)
        errorsOnly = !errorsOnly;
    if (key.Key == ConsoleKey.T)
        truncate = !truncate;
}
```

### Conventions

- **Escape** and **B** always mean "go back"
- Toggle hotkeys show current action (e.g., `"[blue]E[/] Show all"` when errors-only is active,
  `"[blue]E[/] Errors only"` when showing all)
- Hotkey labels use format: `[blue]X[/] Label` separated by triple-space (`   `)
- Hotkey bar is always the last line rendered before waiting for input
- Indent hotkey bars with two leading spaces for visual alignment

## Current State (Before Changes)

- Solution: `Pipeline.slnx` with 4 projects
- Projects: `Pipeline`, `Pipeline.Core`, `Pipeline.Mcp`, `Scratch`
- Existing code: AzDO client, Helix client, MCP tools, CLI — all functional
- No SQLite, no poller, no config system yet
- MCP uses stdio transport (needs to become HTTP)
