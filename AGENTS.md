# Copilot Instructions

This is the **Tiger** project — a CLI tool for managing CI/CD infrastructure.

## Workflow

Work through `docs/todo.md` **one item at a time**:
1. Implement the change for the current item
2. **Stop and present the changes for human review** — do NOT commit or move on
3. Only after explicit approval: commit and proceed to the next item

Rules:

- Do not commit without explicit approval.
- Do not delete `tiger.db` without explicit approval.

## Key Documentation

Read `docs/architecture.md` for full context on:
- Project structure and naming conventions
- Single-process architecture (CLI + poller + MCP/HTTP server)
- SQLite schema design (multi-org aware)
- Configuration format
- Authentication approach
- Technology stack

Read `docs/todo.md` for the current work item checklist.

## Keeping SKILL.md Files in Sync

When changing the SQLite database schema (adding/removing/renaming tables or columns) or
adding/changing valid values for existing columns (e.g. new status values), you **must**
update the corresponding `SKILL.md` files under `src/Tiger/skills/` to stay in sync.
The `tiger-data/SKILL.md` file documents the full schema and is used by MCP skills to
query the database correctly.

When adding, removing, or renaming `tiger azdo` or `tiger helix` CLI subcommands, you
**must** update `src/Tiger/skills/tiger-cli/SKILL.md` to keep the command tables in sync.
This skill file is how MCP agents discover available CLI commands for querying live
AzDO and Helix data.

## CLI Subcommand Conventions

- The `tiger azdo` and `tiger helix` subcommands are designed for **agent consumption**, not human use. They must produce **structured JSON output** (via `JsonSerializer.Serialize` with `JsonOptions.Indented`). Do not use Spectre.Console tables, color markup, or interactive prompts in these commands.
- The interactive dashboard (`tiger dashboard`) is for human use and uses Spectre.Console for rich UI.

## Conventions

- Projects/namespaces use PascalCase: `Tiger`, `Tiger.Core`
- User-facing artifacts use lowercase: `tiger.exe`, `~/.tiger/`
- Target framework: `net10.0`
- All SQLite tables are keyed by `(organization, project)` for multi-org support
- GitHub operations use the user's `gh` CLI auth
- AzDO auth uses `DefaultAzureCredential`

## Testing

When modifying code that has associated tests (check `src/Tiger.Tests/`), you **must**
update or add tests to cover your changes. Run `dotnet test --nologo` to verify all tests
pass before presenting changes for review.

## UI Conventions

### Panel Layout

All screens use `PanelLayout` for a consistent "command and control" look:
- **Header**: `TIGER ▸ Section ▸ Subsection` breadcrumb trail
- **Content area**: List selection or detail view
- **Command bar**: Bottom bar with hotkey commands, focusable via Tab

### Command Bar (`CommandBarItem`)

The command bar is the standard way to expose actions on any screen. It uses `List<CommandBarItem>` where each item has a label, hotkey, and return value.

- **Hotkey display**: Bracket style — `[B]uilds  [T]ests  [H]ealth` — with the bracketed letter rendered in blue.
- **Focus model**: Tab toggles focus between the list and the command bar. When the bar is focused, ←→ moves the highlight and Enter executes. Hotkey letters work regardless of focus.
- **Focused item**: Shown as `[bold white on blue] Label [/]` (inverted highlight).
- **Main menu**: Uses `PanelLayout.ShowMainMenu(commands)` — displays Figlet ASCII art + TIGER branding, navigation only via command bar.
- **List screens**: Use `PanelLayout.SelectInPanel(..., commands)` — list + Tab-focusable command bar.
- **Detail screens**: Use `PanelLayout.RenderDetailPanel(...)` with a static hotkey string footer (detail views handle their own key loops).

### Hotkey Conventions

- Hotkeys use the `[X]` bracket format in labels (e.g., `[E]dit filter`, `[R]efresh`)
- The bracket letter is highlighted in blue via Spectre markup: `[blue][[X]][/]`
- Escape always means "go back" or "cancel"
- Tab always switches focus to the command bar (on list screens)

### All menu locations (update all when changing the format convention):
  - `DashboardCommand.cs` — main menu (ShowMainMenu)
  - `BuildBrowser.cs` — build list command bar, empty-list detail, filter menu, filter help, build detail, test failures, timeline issues
  - `TestBrowser.cs` — test list command bar, empty-list detail, filter menu, test detail
  - `AnalysisBrowser.cs` — analysis list command bar, analysis detail menu, full log menu
  - `AgentBrowser.cs` — agent list command bar, agent detail menu
  - `HealthCommand.cs` — health list command bar, runs list command bar, state page menu, run detail
  - `ConfigEditor.cs` — config menu (still uses `BrowserUI.SelectWithEscape`)

### Code Style

- **if/try/catch** bodies and braces must be on separate lines — never on the same line as the keyword.
- **Escape** always means "go back" or "cancel" in any interactive context.
