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

## Retrying Azure DevOps pipelines

```
tiger azdo retry 12345 --org dnceng-public --project public
tiger azdo retry 12345 --failed-only --org dnceng-public --project public
```

The default queues a full rerun with a new build ID using the original pipeline,
branch, commit, and parameters. `--failed-only` retries failed jobs in the existing
run without rerunning successful jobs, subject to Azure DevOps eligibility and
dependency rules. Both commands return the resulting build as JSON and require
permission to queue/retry builds. An unsupported failed-job retry fails; it does
not fall back to a full rerun.