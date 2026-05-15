# Tiger

Tiger is a CLI tool for managing CI/CD infrastructure. It polls Azure DevOps pipelines for completed builds, stores results in a local SQLite database, detects flaky tests, and serves MCP tools for a copilot console experience.

See `docs/architecture.md` for full design details and `docs/todo.md` for the work plan.

## Prerequisites

1. Be connected to the VPN.
2. This tool uses Azure Identity for authentication — use `az login` to authenticate. You can also use `az account set -s <subscription name>` to set the subscription.

## Building

```
dotnet build Tiger.slnx
```

## Running

```
dotnet run --project src/Tiger
```