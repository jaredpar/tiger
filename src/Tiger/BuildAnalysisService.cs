using System.Threading.Channels;
using GitHub.Copilot.SDK;

namespace Tiger;

/// <summary>
/// Background service that automatically analyzes failed CI builds using an LLM.
/// For each failed build it:
/// 1. Checks for known issue matches (short-circuits if found)
/// 2. Gathers context: test failures, timeline errors, helix logs
/// 3. Invokes an LLM to diagnose the failure and suggest fixes
/// 4. Stores results in build_analyses table and logs to disk
///
/// Logs are stored at ~/.tiger/analysis-logs/{org}/{project}/{definition}/{buildId}.md
/// </summary>
public sealed class BuildAnalysisService : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly KnownIssueService _knownIssues;
    private readonly Channel<(string Organization, int BuildId)> _channel =
        Channel.CreateUnbounded<(string, int)>();
    private readonly ServiceLog? _log;
    private readonly string _logDir;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public bool IsRunning => _workerTask is not null && !_workerTask.IsCompleted;

    public BuildAnalysisService(
        TigerDatabase db,
        AzdoClientFactory clientFactory,
        KnownIssueService knownIssues,
        ServiceLog? log = null)
    {
        _db = db;
        _clientFactory = clientFactory;
        _knownIssues = knownIssues;
        _log = log;
        _logDir = Path.Combine(TigerUtils.GetConfigDirectory(), "analysis-logs");
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _workerTask = ProcessLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_workerTask is not null)
            {
                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        await foreach (var (org, buildId) in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessBuildAsync(org, buildId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("AnalysisAgent", $"Unexpected error analyzing build #{buildId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Event handler for IngestionWorker.OnBuildIngested. Writes to the internal
    /// channel so the worker loop picks it up.
    /// </summary>
    public void OnBuildIngested(string organization, int buildId)
    {
        _log?.Info("AnalysisAgent", $"Build ingestion complete for #{buildId}, queuing for analysis check.");
        _channel.Writer.TryWrite((organization, buildId));
    }

    private async Task ProcessBuildAsync(string org, int buildId, CancellationToken ct)
    {
        // Look up build info and filter — only analyze failed/partiallySucceeded non-PR builds
        var buildInfo = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT project, definition_name, result, source_branch
                FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                    reader.GetString(3));
            }
            return ((string, string, string, string)?)null;
        });

        if (buildInfo is null)
        {
            return;
        }

        var (project, definitionName, result, sourceBranch) = buildInfo.Value;

        // Only analyze failed builds on non-PR branches
        if (result is not ("failed" or "partiallySucceeded"))
        {
            return;
        }

        if (sourceBranch.StartsWith("refs/pull/", StringComparison.Ordinal))
        {
            return;
        }

        _log?.Info("AnalysisAgent", $"Analyzing {project}/{definitionName} build #{buildId}...");

        _db.InsertBuildAnalysis(org, buildId);

        try
        {
            _db.UpdateBuildAnalysis(org, buildId, "running");
            await AnalyzeBuildAsync(org, buildId, project, definitionName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("AnalysisAgent", $"Failed to analyze build #{buildId}: {ex.Message}");
            _db.UpdateBuildAnalysis(org, buildId, "failed", diagnosisSummary: ex.Message);
        }
    }

    private async Task AnalyzeBuildAsync(
        string org, int buildId, string project, string definitionName,
        CancellationToken ct)
    {
        // Step 1: Check known issues
        var knownIssueMatches = CheckKnownIssues(org, buildId);
        if (knownIssueMatches.Count > 0)
        {
            var summary = string.Join(", ", knownIssueMatches.Select(m => $"#{m.IssueNumber}: {m.Title}"));
            _log?.Info("AnalysisAgent", $"  Build #{buildId} matches known issue(s): {summary}");
            _db.UpdateBuildAnalysis(org, buildId, "skipped",
                category: "known-issue",
                diagnosisSummary: $"Matches known issue(s): {summary}");
            return;
        }

        // Step 2: Gather context
        var context = GatherContext(org, buildId, project, definitionName);

        // Step 3: Build prompt
        var prompt = BuildPrompt(org, buildId, project, definitionName, context);

        // Step 4: Invoke LLM
        var result = await InvokeLlmAsync(prompt, ct);
        if (result is null)
        {
            _db.UpdateBuildAnalysis(org, buildId, "failed",
                diagnosisSummary: "LLM invocation failed or timed out");
            return;
        }

        var (transcript, response) = result.Value;

        // Step 5: Parse the response
        var parsed = ParseResponse(response);

        // Step 6: Save log to disk
        var logPath = SaveLog(org, project, definitionName, buildId, prompt, transcript);

        // Step 7: Update the database
        _db.UpdateBuildAnalysis(org, buildId, "complete",
            category: parsed.Category,
            confidence: parsed.Confidence,
            diagnosisSummary: parsed.DiagnosisSummary,
            logPath: logPath);

        _log?.Info("AnalysisAgent", $"  Build #{buildId} analysis complete: {parsed.Category} ({parsed.Confidence})");
    }

    private List<KnownIssueMatch> CheckKnownIssues(string org, int buildId)
    {
        // Gather all error text from the build: timeline issues + test failures
        var errorText = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT COALESCE(GROUP_CONCAT(issue_message, char(10)), '') 
                FROM build_timeline_issues
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteScalar() as string ?? "";
        });

        var testErrors = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT COALESCE(GROUP_CONCAT(tr.error_message, char(10)), '')
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE r.organization = @org AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteScalar() as string ?? "";
        });

        var combinedErrors = errorText + "\n" + testErrors;
        if (string.IsNullOrWhiteSpace(combinedErrors))
        {
            return [];
        }

        // Get repository name for this build
        var repo = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT repository_name FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteScalar() as string;
        });

        if (repo is null)
        {
            return [];
        }

        return _knownIssues.FindMatches(repo, combinedErrors);
    }

    private BuildAnalysisContext GatherContext(
        string org, int buildId, string project, string definitionName)
    {
        var context = new BuildAnalysisContext();

        // Build metadata
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT build_number, source_branch, source_version, result, finish_time,
                       repository_name, definition_name
                FROM builds
                WHERE organization = @org AND build_id = @buildId
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                context.BuildNumber = reader.GetString(0);
                context.SourceBranch = reader.GetString(1);
                context.SourceVersion = reader.IsDBNull(2) ? null : reader.GetString(2);
                context.Result = reader.GetString(3);
                context.FinishTime = reader.IsDBNull(4) ? null : reader.GetString(4);
                context.RepositoryName = reader.GetString(5);
                context.DefinitionName = reader.GetString(6);
            }
        });

        // Timeline issues (errors/warnings from the build timeline)
        context.TimelineIssues = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT record_name, issue_type, issue_message
                FROM build_timeline_issues
                WHERE organization = @org AND build_id = @buildId
                ORDER BY record_name
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var issues = new List<(string RecordName, string IssueType, string Message)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                issues.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return issues;
        });

        // Test failures (top 20 to keep prompt manageable)
        context.TestFailures = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT tr.test_case_title, tr.error_message, tr.stack_trace,
                       tr.helix_job_name, tr.helix_work_item_name
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE r.organization = @org AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
                LIMIT 20
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var failures = new List<TestFailureInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                failures.Add(new TestFailureInfo
                {
                    TestName = reader.GetString(0),
                    ErrorMessage = reader.IsDBNull(1) ? null : reader.GetString(1),
                    StackTrace = reader.IsDBNull(2) ? null : reader.GetString(2),
                    HelixJobName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    HelixWorkItemName = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
            return failures;
        });

        // Recent history: was the previous build also failing?
        context.RecentResults = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT build_id, build_number, result, finish_time
                FROM builds
                WHERE organization = @org AND definition_name = @def
                  AND build_id != @buildId
                  AND source_branch NOT LIKE 'refs/pull/%'
                ORDER BY finish_time DESC
                LIMIT 5
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@def", definitionName);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var results = new List<(int BuildId, string BuildNumber, string Result, string? FinishTime)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            return results;
        });

        return context;
    }

    private static string BuildPrompt(
        string org, int buildId, string project, string definitionName,
        BuildAnalysisContext context)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# Build Failure Analysis: {project}/{definitionName} Build #{buildId}");
        sb.AppendLine();
        sb.AppendLine("## Build Information");
        sb.AppendLine($"- Organization: {org}");
        sb.AppendLine($"- Project: {project}");
        sb.AppendLine($"- Definition: {definitionName}");
        sb.AppendLine($"- Build Number: {context.BuildNumber}");
        sb.AppendLine($"- Result: {context.Result}");
        sb.AppendLine($"- Branch: {context.SourceBranch}");
        sb.AppendLine($"- Commit: {context.SourceVersion ?? "unknown"}");
        sb.AppendLine($"- Finished: {context.FinishTime ?? "unknown"}");
        sb.AppendLine($"- Repository: {context.RepositoryName}");
        sb.AppendLine();

        // Recent history
        if (context.RecentResults.Count > 0)
        {
            sb.AppendLine("## Recent Build History (same definition, non-PR)");
            foreach (var (id, number, result, time) in context.RecentResults)
            {
                sb.AppendLine($"- Build #{id} ({number}): {result} at {time ?? "unknown"}");
            }
            sb.AppendLine();
        }

        // Timeline issues
        if (context.TimelineIssues.Count > 0)
        {
            sb.AppendLine("## Timeline Issues");
            foreach (var (recordName, issueType, message) in context.TimelineIssues)
            {
                sb.AppendLine($"### {recordName} ({issueType})");
                sb.AppendLine(message);
                sb.AppendLine();
            }
        }

        // Test failures
        if (context.TestFailures.Count > 0)
        {
            sb.AppendLine($"## Test Failures ({context.TestFailures.Count} shown)");
            foreach (var failure in context.TestFailures)
            {
                sb.AppendLine($"### {failure.TestName}");
                if (failure.ErrorMessage is not null)
                {
                    sb.AppendLine($"**Error:** {failure.ErrorMessage}");
                }
                if (failure.StackTrace is not null)
                {
                    // Truncate very long stack traces
                    var stack = failure.StackTrace.Length > 2000
                        ? failure.StackTrace[..2000] + "\n... (truncated)"
                        : failure.StackTrace;
                    sb.AppendLine("```");
                    sb.AppendLine(stack);
                    sb.AppendLine("```");
                }
                if (failure.HelixJobName is not null)
                {
                    sb.AppendLine($"- Helix Job: {failure.HelixJobName}");
                    sb.AppendLine($"- Helix Work Item: {failure.HelixWorkItemName}");
                    sb.AppendLine($"- Console Log: {HelixClient.GetConsoleUrl(failure.HelixJobName, failure.HelixWorkItemName ?? "")}");
                }
                sb.AppendLine();
            }
        }

        if (context.TimelineIssues.Count == 0 && context.TestFailures.Count == 0)
        {
            sb.AppendLine("## No Specific Errors Found");
            sb.AppendLine("The build failed but no timeline issues or test failures were captured.");
            sb.AppendLine("This may indicate an infrastructure issue, a build step failure not captured by the test framework,");
            sb.AppendLine("or incomplete data ingestion.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<(string Transcript, string Response)?> InvokeLlmAsync(
        string prompt, CancellationToken ct)
    {
        try
        {
            await using var client = new CopilotClient();
            await client.StartAsync();

            var skillsDir = Path.Combine(AppContext.BaseDirectory, "skills");
            var systemMessageBuilder = new System.Text.StringBuilder();
            systemMessageBuilder.AppendLine("""
                You are a CI build failure analysis agent. Your job is to diagnose why a build failed
                and provide actionable guidance. Be concise and specific.

                For each build failure you analyze, produce a structured response with these sections:

                ## Diagnosis
                What went wrong. Be specific about the root cause.

                ## Category
                One of: test-failure, build-error, infrastructure, timeout, flaky-test, configuration, unknown

                ## Confidence
                One of: high, medium, low — how confident you are in the diagnosis.

                ## Diagnosability
                If the failure was hard to diagnose, suggest specific improvements that would make
                similar failures easier to diagnose in the future (better error messages, additional
                logging, test isolation, etc.). If the failure was straightforward, say "No improvements needed."

                ## Suggested Fix
                A concrete suggestion for fixing the issue, or "No fix suggested" if you can't determine one.
                If you suggest a code change, be specific about which file and what to change.
                """);

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
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            return await responseTcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("AnalysisAgent", $"LLM invocation failed: {ex.Message}");
            return null;
        }
    }

    private static AnalysisParsedResponse ParseResponse(string response)
    {
        var parsed = new AnalysisParsedResponse();

        // Extract Category
        var categoryIdx = response.IndexOf("## Category", StringComparison.OrdinalIgnoreCase);
        if (categoryIdx >= 0)
        {
            var afterCategory = response[(categoryIdx + "## Category".Length)..];
            var nextSection = afterCategory.IndexOf("\n## ", StringComparison.OrdinalIgnoreCase);
            var categoryText = nextSection >= 0
                ? afterCategory[..nextSection].Trim()
                : afterCategory.Trim();
            parsed.Category = categoryText.Split('\n')[0].Trim().ToLowerInvariant();
        }

        // Extract Confidence
        var confidenceIdx = response.IndexOf("## Confidence", StringComparison.OrdinalIgnoreCase);
        if (confidenceIdx >= 0)
        {
            var afterConfidence = response[(confidenceIdx + "## Confidence".Length)..];
            var nextSection = afterConfidence.IndexOf("\n## ", StringComparison.OrdinalIgnoreCase);
            var confidenceText = nextSection >= 0
                ? afterConfidence[..nextSection].Trim()
                : afterConfidence.Trim();
            parsed.Confidence = confidenceText.Split('\n')[0].Trim().ToLowerInvariant();
        }

        // Extract Diagnosis as the summary
        var diagnosisIdx = response.IndexOf("## Diagnosis", StringComparison.OrdinalIgnoreCase);
        if (diagnosisIdx >= 0)
        {
            var afterDiagnosis = response[(diagnosisIdx + "## Diagnosis".Length)..];
            var nextSection = afterDiagnosis.IndexOf("\n## ", StringComparison.OrdinalIgnoreCase);
            var diagnosisText = nextSection >= 0
                ? afterDiagnosis[..nextSection].Trim()
                : afterDiagnosis.Trim();
            // Take first 500 chars as summary
            parsed.DiagnosisSummary = diagnosisText.Length > 500
                ? diagnosisText[..500] + "..."
                : diagnosisText;
        }

        return parsed;
    }

    private string SaveLog(
        string org, string project, string definitionName, int buildId,
        string prompt, string transcript)
    {
        var logDir = Path.Combine(_logDir, org, project, definitionName);
        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, $"{buildId}.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Build Analysis: {project}/{definitionName} #{buildId}");
        sb.AppendLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Organization:** {org}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Prompt");
        sb.AppendLine();
        sb.AppendLine(prompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Transcript");
        sb.AppendLine();
        sb.AppendLine(transcript);

        File.WriteAllText(logPath, sb.ToString());
        return logPath;
    }

    /// <summary>
    /// Manually queue a build for analysis. Deletes any existing analysis and re-inserts as pending.
    /// </summary>
    public void RequestAnalysis(string organization, int buildId)
    {
        _db.DeleteBuildAnalysis(organization, buildId);
        _channel.Writer.TryWrite((organization, buildId));
        _log?.Info("AnalysisAgent", $"Re-queued build #{buildId} for analysis.");
    }

    public void Dispose()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }
}

internal class BuildAnalysisContext
{
    public string? BuildNumber { get; set; }
    public string? SourceBranch { get; set; }
    public string? SourceVersion { get; set; }
    public string? Result { get; set; }
    public string? FinishTime { get; set; }
    public string? RepositoryName { get; set; }
    public string? DefinitionName { get; set; }
    public List<(string RecordName, string IssueType, string Message)> TimelineIssues { get; set; } = [];
    public List<TestFailureInfo> TestFailures { get; set; } = [];
    public List<(int BuildId, string BuildNumber, string Result, string? FinishTime)> RecentResults { get; set; } = [];
}

internal class TestFailureInfo
{
    public required string TestName { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? HelixJobName { get; init; }
    public string? HelixWorkItemName { get; init; }
}

internal class AnalysisParsedResponse
{
    public string? Category { get; set; }
    public string? Confidence { get; set; }
    public string? DiagnosisSummary { get; set; }
}
