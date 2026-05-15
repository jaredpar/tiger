# Copilot Instructions

This is the **Tiger** project — a CLI tool for managing CI/CD infrastructure.

## Workflow

Work through `docs/todo.md` **one item at a time**:
1. Implement the change for the current item
2. **Stop and present the changes for human review** — do NOT commit or move on
3. Only after explicit approval: commit and proceed to the next item

Do not batch multiple todo items. Do not commit without sign-off.

## Key Documentation

Read `docs/architecture.md` for full context on:
- Project structure and naming conventions
- Single-process architecture (CLI + poller + MCP/HTTP server)
- SQLite schema design (multi-org aware)
- Configuration format
- Authentication approach
- Technology stack

Read `docs/todo.md` for the current work item checklist.

## Conventions

- Projects/namespaces use PascalCase: `Tiger`, `Tiger.Core`
- User-facing artifacts use lowercase: `tiger.exe`, `~/.tiger/`
- Target framework: `net10.0`
- All SQLite tables are keyed by `(organization, project)` for multi-org support
- GitHub operations use the user's `gh` CLI auth
- AzDO auth uses `DefaultAzureCredential`
