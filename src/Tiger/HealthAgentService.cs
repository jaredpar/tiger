using GitHub.Copilot.SDK;

namespace Tiger;

/// <summary>
/// Background service that periodically invokes an LLM to evaluate CI health
/// for each (repository, definition) combination. Produces findings and maintains
/// a "state of the build" document.
///
/// Logs are stored on disk at ~/.tiger/health/{repo}/{definition}/{timestamp}.md
/// Only the last 10 logs per combination are retained.
/// </summary>
public sealed class HealthAgentService : IDisposable
{
    private readonly TigerConfig _config;
    private readonly TigerDatabase _db;
    private readonly ServiceLog? _log;
    private readonly string _healthDir;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private const int PollIntervalMinutes = 15;
    private const int MaxLogsPerCombo = 10;
    private const int LookbackDays = 3;

    public bool IsRunning => _pollingTask is not null && !_pollingTask.IsCompleted;

    public HealthAgentService(TigerConfig config, TigerDatabase db, ServiceLog? log = null)
    {
        _config = config;
        _db = db;
        _log = log;
        _healthDir = Path.Combine(TigerUtils.GetConfigDirectory(), "health");
    }

    public void Start()
    {
        if (IsRunning)
            return;
        _cts = new CancellationTokenSource();
        _pollingTask = PollLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_pollingTask is not null)
            {
                try
                {
                    await _pollingTask;
                }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunHealthChecksAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("HealthAgent", $"Unexpected error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(PollIntervalMinutes), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunHealthChecksAsync(CancellationToken ct)
    {
        var combos = GetMonitoredCombinations();
        _log?.Info("HealthAgent", $"Running health checks for {combos.Count} combination(s)...");

        foreach (var (repo, definition) in combos)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await EvaluateHealthAsync(repo, definition, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Warning("HealthAgent", $"Failed to evaluate {repo}/{definition}: {ex.Message}");
            }
        }
    }

    private async Task EvaluateHealthAsync(string repository, string definition, CancellationToken ct)
    {
        _log?.Info("HealthAgent", $"Evaluating {repository} / {definition}...");

        var buildData = GatherBuildData(repository, definition);
        var previousState = LoadState(repository, definition);
        var prompt = BuildPrompt(repository, definition, buildData, previousState);

        // Invoke the LLM
        var result = await InvokeLlmAsync(prompt, ct);
        if (result is null)
            return;

        var (transcript, response) = result.Value;

        // Parse findings and state from response (the non-reasoning output)
        var (findings, newState) = ParseResponse(response);

        // Save the full log to disk (interleaved reasoning + messages)
        var logPath = SaveLog(repository, definition, prompt, transcript);

        // Update state on disk
        SaveState(repository, definition, newState ?? response);

        // Prune old logs
        PruneOldLogs(repository, definition);

        _log?.Info("HealthAgent", $"  {repository} / {definition} — complete, log: {Path.GetFileName(logPath)}");
    }

    private string BuildPrompt(string repository, string definition, BuildHealthData data, string? previousState)
    {
        var sb = new System.Text.StringBuilder();
        var tz = TimeZoneInfo.Local;
        sb.AppendLine($"# Health Evaluation: {repository} / {definition}");
        sb.AppendLine();
        sb.AppendLine("## Pipeline Context");
        sb.AppendLine($"- GitHub Repository: {repository}");
        sb.AppendLine($"- AzDO Pipeline: {definition}");
        if (data.Organization is not null)
        {
            sb.AppendLine($"- AzDO Organization: {data.Organization}");
            sb.AppendLine($"- AzDO Project: {data.Project}");
            if (data.DefinitionId is not null)
            {
                sb.AppendLine($"- Definition ID: {data.DefinitionId}");
                sb.AppendLine($"- Pipeline URL: https://dev.azure.com/{data.Organization}/{data.Project}/_build?definitionId={data.DefinitionId}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Analyze the last {LookbackDays} days of CI builds for this repository and pipeline.");
        sb.AppendLine($"All times are in local time ({tz.DisplayName}).");
        sb.AppendLine($"Current time: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        // Build summary
        sb.AppendLine("## Recent Builds");
        sb.AppendLine($"Total: {data.TotalBuilds}, Failed: {data.FailedBuilds}, Succeeded: {data.SucceededBuilds}, Partial: {data.PartialBuilds}");
        sb.AppendLine();

        if (data.RecentFailures.Count > 0)
        {
            sb.AppendLine("## Recent Failures");
            sb.AppendLine("These builds failed or partially succeeded. Use the (organization, project, build_id) primary key to query more details from the database.");
            sb.AppendLine();
            foreach (var f in data.RecentFailures)
            {
                var branchType = f.SourceBranch.StartsWith("refs/pull/") ? "PR" : "CI";
                var localTime = ToLocalTimeString(f.FinishTime);
                sb.AppendLine($"- Build ({f.Organization}, {f.Project}, {f.BuildId}) — {f.Result} — {branchType} — {localTime}");
            }
            sb.AppendLine();
        }

        if (data.FailingTests.Count > 0)
        {
            sb.AppendLine("## Failing Tests (CI vs PR breakdown)");
            sb.AppendLine("Tests grouped by failure count in CI builds (refs/heads/*) vs PR builds (refs/pull/*).");
            sb.AppendLine("CI failures are more significant for infra health since they run on merged code.");
            sb.AppendLine();
            foreach (var t in data.FailingTests)
            {
                sb.AppendLine($"- {t.TestName} — CI: {t.CiFailures}, PR: {t.PrFailures}");
            }
            sb.AppendLine();
        }

        if (data.TimelineErrors.Count > 0)
        {
            sb.AppendLine("## Timeline Errors (infrastructure)");
            foreach (var e in data.TimelineErrors.Take(15))
            {
                sb.AppendLine($"- [{e.IssueType}] {e.RecordName}: {e.Message}");
            }
            sb.AppendLine();
        }

        if (data.KnownIssues.Count > 0)
        {
            sb.AppendLine("## Active Known Issues");
            sb.AppendLine("These are GitHub issues labeled 'Known Build Error' with matching rules.");
            sb.AppendLine();
            foreach (var ki in data.KnownIssues)
            {
                sb.AppendLine($"### #{ki.IssueNumber}: {ki.Title}");
                if (ki.ErrorMessage is not null)
                {
                    sb.AppendLine($"  Error text (substring match): {ki.ErrorMessage}");
                }
                if (ki.ErrorPattern is not null)
                {
                    sb.AppendLine($"  Error pattern (regex): {ki.ErrorPattern}");
                }
                if (ki.ExcludeConsoleLog)
                {
                    sb.AppendLine($"  Matches in helix console logs (not just build logs)");
                }
                sb.AppendLine();
            }
        }

        if (previousState is not null)
        {
            sb.AppendLine("## Previous State of the Build");
            sb.AppendLine(previousState);
            sb.AppendLine();
        }

        sb.AppendLine("""
            ## Instructions

            You have access to the `tiger-data` skill which lets you query the SQLite database at `~/.tiger/tiger.db`
            for additional details about builds, test results, timeline issues, and helix work items. Use it to dig
            into any questions you have about specific builds or test patterns.

            Based on the data above, produce:

            1. **Findings** — A list of actionable issues requiring attention:
               - Flaky tests (tests that pass and fail intermittently across CI builds)
               - Infrastructure issues (repeated infra errors in timeline)
               - New persistent failures (tests that started failing in CI and haven't recovered)
               - Known issue correlations (are known issues actively hitting builds?)
               - Tests failing only in CI (not PR) suggest possible merge-order or timing issues

            2. **State of the Build** — Update the previous state (or create a new one) with:
               - Overall health: GREEN / YELLOW / RED
               - Active problems and when they started
               - Recently resolved issues
               - Trends (getting better/worse)

            Format your response as:
            
            ## Findings
            (your findings here)

            ## State of the Build
            (updated state document here)
            """);

        return sb.ToString();
    }

    private async Task<(string Transcript, string Response)?> InvokeLlmAsync(string prompt, CancellationToken ct)
    {
        try
        {
            await using var client = new CopilotClient();
            await client.StartAsync();

            var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
            var systemMessageBuilder = new System.Text.StringBuilder();
            systemMessageBuilder.AppendLine("""
                You are a CI health analysis agent. You evaluate build and test data to identify 
                problems, flaky tests, infrastructure issues, and trends. Be concise and actionable.
                Focus on issues that require human attention. Don't report on things that are fine.

                You have access to a SQLite database via the tiger-data skill. Use it to query for
                additional details about specific builds, test results, helix work items, and timeline
                issues when the summary data isn't sufficient to draw conclusions.

                Key distinctions:
                - CI builds (refs/heads/*) run on merged code. Failures here are high-signal for infra health.
                - PR builds (refs/pull/*) run on unmerged code. Repeated failures in the same PR are likely code issues, not infra.
                - Tests failing in CI but not PR suggest merge-order, timing, or environment issues.
                - Tests failing in both CI and PR across many builds are likely flaky.
                """);

            // Include skill files so the agent knows the DB schema and can query it
            if (Directory.Exists(skillsDir))
            {
                foreach (var file in Directory.GetFiles(skillsDir, "*.md", SearchOption.AllDirectories))
                {
                    systemMessageBuilder.AppendLine();
                    systemMessageBuilder.AppendLine(File.ReadAllText(file));
                }
            }

            var systemMessage = systemMessageBuilder.ToString();

            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = "claude-opus-4.6",
                Streaming = false,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                SystemMessage = new SystemMessageConfig { Content = systemMessage },
            });

            var responseTcs = new TaskCompletionSource<(string, string)>();
            var transcript = new System.Text.StringBuilder();
            var responseText = new System.Text.StringBuilder();
            var lastEventKind = TranscriptEventKind.None;

            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantReasoningEvent reasoning:
                        if (lastEventKind != TranscriptEventKind.Reasoning && transcript.Length > 0)
                        {
                            transcript.AppendLine();
                        }
                        foreach (var line in (reasoning.Data.Content ?? "").Split('\n'))
                        {
                            transcript.AppendLine($"*{line.TrimEnd('\r')}*");
                        }
                        lastEventKind = TranscriptEventKind.Reasoning;
                        break;
                    case AssistantMessageEvent msg:
                        if (lastEventKind != TranscriptEventKind.Message && lastEventKind != TranscriptEventKind.None)
                        {
                            transcript.AppendLine();
                        }
                        transcript.Append(msg.Data.Content);
                        responseText.Append(msg.Data.Content);
                        lastEventKind = TranscriptEventKind.Message;
                        break;
                    case ToolExecutionStartEvent toolStart:
                        if (lastEventKind != TranscriptEventKind.None)
                        {
                            transcript.AppendLine();
                        }
                        transcript.AppendLine($"> **Tool:** `{toolStart.Data.ToolName}`");
                        var args = toolStart.Data.Arguments?.ToString();
                        if (!string.IsNullOrWhiteSpace(args))
                        {
                            // Truncate long args for readability
                            if (args.Length > 500)
                            {
                                args = args[..500] + "...";
                            }
                            transcript.AppendLine($"> **Input:** `{args}`");
                        }
                        lastEventKind = TranscriptEventKind.Tool;
                        break;
                    case ToolExecutionCompleteEvent toolComplete:
                        var status = toolComplete.Data.Success ? "✓" : "✗";
                        transcript.AppendLine($"> **Result ({status}):**");
                        if (toolComplete.Data.Result?.Content is not null)
                        {
                            var content = toolComplete.Data.Result.Content;
                            if (content.Length > 1000)
                            {
                                content = content[..1000] + "\n> _(truncated)_";
                            }
                            foreach (var line in content.Split('\n'))
                            {
                                transcript.AppendLine($"> {line.TrimEnd('\r')}");
                            }
                        }
                        else if (toolComplete.Data.Error is not null)
                        {
                            transcript.AppendLine($"> Error: {toolComplete.Data.Error}");
                        }
                        transcript.AppendLine();
                        lastEventKind = TranscriptEventKind.Tool;
                        break;
                    case SessionIdleEvent:
                        responseTcs.TrySetResult((transcript.ToString(), responseText.ToString()));
                        break;
                    case SessionErrorEvent err:
                        responseTcs.TrySetException(new InvalidOperationException(err.Data.Message));
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            return await responseTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("HealthAgent", $"LLM invocation failed: {ex.Message}");
            return null;
        }
    }

    private (string? Findings, string? State) ParseResponse(string response)
    {
        string? findings = null;
        string? state = null;

        var findingsIdx = response.IndexOf("## Findings", StringComparison.OrdinalIgnoreCase);
        var stateIdx = response.IndexOf("## State of the Build", StringComparison.OrdinalIgnoreCase);

        if (findingsIdx >= 0 && stateIdx > findingsIdx)
        {
            findings = response[(findingsIdx + "## Findings".Length)..stateIdx].Trim();
            state = response[(stateIdx + "## State of the Build".Length)..].Trim();
        }
        else if (stateIdx >= 0)
        {
            state = response[(stateIdx + "## State of the Build".Length)..].Trim();
        }

        return (findings, state);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string ToLocalTimeString(string isoUtcTime)
    {
        if (DateTime.TryParse(isoUtcTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            var local = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            return local.ToString("yyyy-MM-dd HH:mm");
        }
        return isoUtcTime;
    }

    // ── Data gathering ──────────────────────────────────────────────

    private BuildHealthData GatherBuildData(string repository, string definition)
    {
        var since = DateTime.UtcNow.AddDays(-LookbackDays).ToString("o");
        var data = new BuildHealthData();

        // Pipeline identity (org, project, definition_id)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT organization, project, definition_id FROM builds
                WHERE repository_name = @repo AND definition_name = @def
                ORDER BY finish_time DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@def", definition);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                data.Organization = reader.GetString(0);
                data.Project = reader.GetString(1);
                data.DefinitionId = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            }
        }

        // Build counts
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT result, COUNT(*) FROM builds
                WHERE repository_name = @repo AND definition_name = @def AND finish_time >= @since
                GROUP BY result
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@def", definition);
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var result = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                var count = reader.GetInt32(1);
                data.TotalBuilds += count;
                switch (result)
                {
                    case "failed": data.FailedBuilds = count; break;
                    case "succeeded": data.SucceededBuilds = count; break;
                    case "partiallySucceeded": data.PartialBuilds = count; break;
                }
            }
        }

        // Recent failures
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT organization, project, build_id, result, finish_time, source_branch FROM builds
                WHERE repository_name = @repo AND definition_name = @def AND finish_time >= @since
                  AND result IN ('failed', 'partiallySucceeded')
                ORDER BY finish_time DESC LIMIT 10
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@def", definition);
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.RecentFailures.Add(new BuildFailureInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5)));
            }
        }

        // Top failing tests (split by CI vs PR)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT tr.test_case_title,
                    SUM(CASE WHEN b.source_branch LIKE 'refs/heads/%' THEN 1 ELSE 0 END) as ci_failures,
                    SUM(CASE WHEN b.source_branch LIKE 'refs/pull/%' THEN 1 ELSE 0 END) as pr_failures
                FROM test_results tr
                JOIN test_runs trn ON tr.organization = trn.organization
                    AND tr.project = trn.project AND tr.run_id = trn.run_id
                JOIN builds b ON trn.organization = b.organization
                    AND trn.project = b.project AND trn.build_id = b.build_id
                WHERE b.repository_name = @repo AND b.definition_name = @def
                  AND b.finish_time >= @since AND tr.outcome = 'Failed'
                GROUP BY tr.test_case_title
                ORDER BY (ci_failures + pr_failures) DESC LIMIT 30
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@def", definition);
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.FailingTests.Add(new FailingTestInfo(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
            }
        }

        // Timeline errors
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ti.record_name, ti.issue_type, ti.issue_message
                FROM build_timeline_issues ti
                JOIN builds b ON ti.organization = b.organization
                    AND ti.project = b.project AND ti.build_id = b.build_id
                WHERE b.repository_name = @repo AND b.definition_name = @def
                  AND b.finish_time >= @since AND ti.issue_type = 'error'
                ORDER BY b.finish_time DESC LIMIT 15
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            cmd.Parameters.AddWithValue("@def", definition);
            cmd.Parameters.AddWithValue("@since", since);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.TimelineErrors.Add(new TimelineErrorInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        // Known issues (with full detail)
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT issue_number, title, error_message, error_pattern, exclude_console_log
                FROM known_issues
                WHERE repository = @repo AND state = 'open'
                """;
            cmd.Parameters.AddWithValue("@repo", repository);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.KnownIssues.Add(new KnownIssueInfo(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4) != 0));
            }
        }

        return data;
    }

    private List<(string Repo, string Definition)> GetMonitoredCombinations()
    {
        var combos = new List<(string, string)>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT repository_name, definition_name FROM builds
            WHERE repository_name IS NOT NULL
              AND finish_time >= datetime('now', '-7 days')
            ORDER BY repository_name, definition_name
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            combos.Add((reader.GetString(0), reader.GetString(1)));
        }
        return combos;
    }

    // ── File I/O ────────────────────────────────────────────────────

    private string GetComboDir(string repository, string definition)
    {
        var safeRepo = repository.Replace("/", "_").Replace("\\", "_");
        var safeDef = definition.Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
        return Path.Combine(_healthDir, safeRepo, safeDef);
    }

    private string SaveLog(string repository, string definition, string prompt, string transcript)
    {
        var dir = GetComboDir(repository, definition);
        Directory.CreateDirectory(dir);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(dir, $"{timestamp}.md");
        var content = $"""
            # Health Report — {repository} / {definition}
            Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}

            ## Prompt (input to LLM)
            {prompt}

            ## Agent Output
            Reasoning is shown in *italics*. Tool calls are shown in blockquotes (>). Normal text is the agent's response.

            {transcript}
            """;
        File.WriteAllText(path, content);
        return path;
    }

    private string? LoadState(string repository, string definition)
    {
        var dir = GetComboDir(repository, definition);
        var statePath = Path.Combine(dir, "state.md");
        if (File.Exists(statePath))
            return File.ReadAllText(statePath);
        return null;
    }

    private void SaveState(string repository, string definition, string state)
    {
        var dir = GetComboDir(repository, definition);
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.md");
        File.WriteAllText(statePath, state);
    }

    private void PruneOldLogs(string repository, string definition)
    {
        var dir = GetComboDir(repository, definition);
        if (!Directory.Exists(dir))
            return;

        var logs = Directory.GetFiles(dir, "????-??-??_??-??-??.md")
            .OrderByDescending(f => f)
            .Skip(MaxLogsPerCombo)
            .ToList();

        foreach (var log in logs)
        {
            try
            {
                File.Delete(log);
            }
            catch { }
        }
    }

    /// <summary>
    /// Gets the list of recent health run log paths for a given combination.
    /// </summary>
    public List<HealthRunInfo> GetRecentRuns(string? repository = null, string? definition = null)
    {
        var runs = new List<HealthRunInfo>();
        if (!Directory.Exists(_healthDir))
            return runs;

        var repoDirs = Directory.GetDirectories(_healthDir);
        foreach (var repoDir in repoDirs)
        {
            var repoName = Path.GetFileName(repoDir).Replace("_", "/");
            if (repository is not null && !repoName.Equals(repository, StringComparison.OrdinalIgnoreCase))
                continue;

            var defDirs = Directory.GetDirectories(repoDir);
            foreach (var defDir in defDirs)
            {
                var defName = Path.GetFileName(defDir).Replace("_", " ");
                if (definition is not null && !defName.Equals(definition, StringComparison.OrdinalIgnoreCase))
                    continue;

                var logs = Directory.GetFiles(defDir, "????-??-??_??-??-??.md")
                    .OrderByDescending(f => f);

                foreach (var log in logs)
                {
                    var fileName = Path.GetFileNameWithoutExtension(log);
                    runs.Add(new HealthRunInfo(repoName, defName, fileName, log));
                }
            }
        }

        return runs.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>
    /// Gets the current "state of the build" for a combination.
    /// </summary>
    public string? GetCurrentState(string repository, string definition)
    {
        return LoadState(repository, definition);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

// ── Supporting types ────────────────────────────────────────────────

internal enum TranscriptEventKind { None, Reasoning, Message, Tool }

public sealed record HealthRunInfo(string Repository, string Definition, string Timestamp, string LogPath);

internal sealed class BuildHealthData
{
    public string? Organization { get; set; }
    public string? Project { get; set; }
    public int? DefinitionId { get; set; }
    public int TotalBuilds { get; set; }
    public int FailedBuilds { get; set; }
    public int SucceededBuilds { get; set; }
    public int PartialBuilds { get; set; }
    public List<BuildFailureInfo> RecentFailures { get; } = [];
    public List<FailingTestInfo> FailingTests { get; } = [];
    public List<TimelineErrorInfo> TimelineErrors { get; } = [];
    public List<KnownIssueInfo> KnownIssues { get; } = [];
}

internal sealed record BuildFailureInfo(string Organization, string Project, int BuildId, string Result, string FinishTime, string SourceBranch);
internal sealed record FailingTestInfo(string TestName, int CiFailures, int PrFailures);
internal sealed record TimelineErrorInfo(string RecordName, string IssueType, string Message);
internal sealed record KnownIssueInfo(int IssueNumber, string Title, string? ErrorMessage, string? ErrorPattern, bool ExcludeConsoleLog);
