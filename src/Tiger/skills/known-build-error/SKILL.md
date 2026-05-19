---
name: known-build-error
description: File a "Known Build Error" GitHub issue to track a recurring build or test failure in the dotnet infrastructure
---

# Known Build Error Skill

This skill creates GitHub issues with the `Known Build Error` label that are automatically picked up by the dotnet
Build Analysis infrastructure to match and suppress known failures.

## When to Use

Use this skill when you identify a recurring build or test error that:
- Is not caused by a specific PR's changes
- Affects or could affect multiple builds
- Should be tracked so developers know it's a known issue

## Issue Types

- **Infrastructure issue**: Not repo-specific, needs investigation by engineering services. Filed in `dotnet/dnceng`.
- **Repository issue**: Specific to a particular repo, needs investigation by repo owners. Filed in the affected repository.

## Required JSON Blob Format

The issue body **must** contain a fenced JSON code block with the error matching configuration:

```json
{
    "ErrorMessage": "",
    "BuildRetry": false,
    "ErrorPattern": "",
    "ExcludeConsoleLog": false
}
```

### Fields

| Field | Type | Description |
|-------|------|-------------|
| `ErrorMessage` | `string` or `string[]` | Literal substring match against error text. Use when the error text is stable. |
| `ErrorPattern` | `string` or `string[]` | .NET regex pattern for matching. Use when the error contains variable parts (paths, timestamps, IDs). |
| `BuildRetry` | `bool` | Set to `true` if the failure is transient and retrying the build may fix it. Only retries on first attempt. |
| `ExcludeConsoleLog` | `bool` | Set to `true` to exclude console/Helix logs from matching (only match against AzDO error messages). |

**Important rules:**
- Use EITHER `ErrorMessage` OR `ErrorPattern`, never both in the same blob.
- Special JSON characters must be escaped (e.g., `\"` for quotes, `\\` for backslash).
- For regex patterns: use `.NET (C#)` flavor with options: single-line, case-insensitive, no backtracking.
- Strip unique identifiers (machine names, paths, timestamps, GUIDs) from the match string.

### Multi-line Matching (Array Form)

Both `ErrorMessage` and `ErrorPattern` accept an array of strings for matching multiple lines **in order** (AND condition):

```json
{
    "ErrorMessage": ["Assert.True() Failure", "Actual:   False"]
}
```

Rules for multi-line matching:
- Lines are matched in order — first match found, then subsequent lines searched after that point.
- ALL entries must match for the known issue to trigger.
- Each entry matches against a single line (not multi-line).
- Don't mix `ErrorMessage` and `ErrorPattern` in the same blob.

## Issue Template

The issue must follow this structure:

```markdown
## Build Information
Build: <!-- Link to the AzDO build -->
Leg Name: <!-- Name of the impacted leg/job -->

## Error Message
```json
{
    "ErrorMessage": "the error text here",
    "BuildRetry": false,
    "ErrorPattern": "",
    "ExcludeConsoleLog": false
}
`` `
```

## How to Create the Issue

Use the `gh` CLI to file the issue:

```bash
# For repository issues (filed in the affected repo):
gh issue create --repo dotnet/<REPO> \
  --label "Known Build Error" \
  --title "<Short description of the error>" \
  --body "<issue body with template above>"

# For infrastructure issues (filed in dotnet/dnceng):
gh issue create --repo dotnet/dnceng \
  --label "Known Build Error" \
  --title "<Short description of the error>" \
  --body "<issue body with template above>"
```

## Decision Guide

Ask these questions to fill out the JSON blob correctly:

1. **ErrorMessage vs ErrorPattern?**
   - If the error text is stable and literal → use `ErrorMessage`
   - If the error contains variable parts (paths, IDs, timestamps) → use `ErrorPattern` with regex

2. **ExcludeConsoleLog?**
   - `false` (default): Match against AzDO errors AND build logs AND Helix logs
   - `true`: Only match against AzDO error messages (not console/Helix logs)
   - Set to `true` when the error only appears in AzDO timeline records, not in raw logs

3. **BuildRetry?**
   - `true`: The failure is transient (network timeouts, agent disconnects, resource exhaustion)
   - `false` (default): The failure is deterministic or retry won't help

4. **Infrastructure vs Repository?**
   - Infrastructure (`dotnet/dnceng`): Affects multiple repos, relates to build infra (agents, network, Helix machines)
   - Repository (the affected repo): Specific to one repo's tests or build logic

## Examples

### Simple string match for a NuGet restore failure

```json
{
    "ErrorMessage": "Failed to retrieve information",
    "BuildRetry": true,
    "ErrorPattern": "",
    "ExcludeConsoleLog": false
}
```

### Regex match for a test failure with variable details

```json
{
    "ErrorPattern": "\\[FAIL\\] System\\.Net\\.Http\\.Tests\\..+Timeout",
    "ErrorMessage": "",
    "BuildRetry": false,
    "ExcludeConsoleLog": false
}
```

### Multi-line match for an assertion failure in Helix logs

```json
{
    "ErrorMessage": ["Assert.True() Failure", "Actual:   False"],
    "BuildRetry": false,
    "ErrorPattern": "",
    "ExcludeConsoleLog": false
}
```

### Agent disconnect (infrastructure, retriable)

```json
{
    "ErrorMessage": "The agent did not connect within the alloted time",
    "BuildRetry": true,
    "ErrorPattern": "",
    "ExcludeConsoleLog": true
}
```
