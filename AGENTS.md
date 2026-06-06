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

- **Menus** use `BrowserUI.SelectWithEscape` with hotkey support (the `extraKeys` parameter). Menu items embed the hotkey letter in blue within the label text: `[blue]E[/]dit filter`. This gives both arrow-key scrolling and single-keypress shortcuts.
- Inline hotkey bars (e.g. `[blue]T[/]ests   [blue]J[/]obs   [blue]Esc[/] Back`) follow the same format.
- **All menu locations** (update all when changing the format convention):
  - `DashboardCommand.cs` — main menu, status view hotkey bar
  - `BuildBrowser.cs` — build list hotkey bar, empty-list hotkey bar, filter menu, filter help, build detail hotkey bar, test failure hotkey bar, timeline issues hotkey bar
  - `TestBrowser.cs` — test list hotkey bar, empty-list hotkey bar, filter menu, test detail hotkey bar
  - `AnalysisBrowser.cs` — analysis detail menu, full log menu
  - `HealthCommand.cs` — state page menu, run detail menu
  - `ConfigEditor.cs` — config menu
- **Escape** always means "go back" or "cancel" in any interactive context.
- **if/try/catch** bodies and braces must be on separate lines — never on the same line as the keyword.
