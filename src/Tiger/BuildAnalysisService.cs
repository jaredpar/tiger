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
    private readonly Channel<AnalysisRequest> _channel =
        Channel.CreateUnbounded<AnalysisRequest>();
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
        _logDir = Path.Combine(TigerUtils.GetConfigDirectory(), TigerUtils.AnalysisLogsDirectoryName);
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
        await foreach (var request in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessBuildAsync(request.Organization, request.BuildId, request.FullAnalysisCheck, request.ManualRequest, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("AnalysisAgent", $"Unexpected error analyzing build #{request.BuildId}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Event handler for TaskIngestionService.OnBuildIngested. Only queues recent
    /// builds (finished within the last 4 hours) to avoid bulk analysis during
    /// backfill. Use <see cref="RequestAnalysis"/> for on-demand analysis of
    /// any build regardless of age.
    /// </summary>
    public void OnBuildIngested(BuildIngestedEvent buildEvent)
    {
        if (buildEvent.FinishTime is not null &&
            DateTimeOffset.TryParse(buildEvent.FinishTime, out var finishTime) &&
            DateTimeOffset.UtcNow - finishTime > TimeSpan.FromHours(48))
        {
            _log?.Info("AnalysisAgent", $"Build #{buildEvent.BuildId} is too old for auto-analysis, skipping.");
            return;
        }

        _log?.Info("AnalysisAgent", $"Build ingestion complete for #{buildEvent.BuildId}, queuing for analysis check.");
        _channel.Writer.TryWrite(new AnalysisRequest(buildEvent.Organization, buildEvent.BuildId));
    }

    private async Task ProcessBuildAsync(string org, int buildId, bool fullAnalysisCheck, bool manualRequest, CancellationToken ct)
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

        // Only analyze failed builds on non-PR branches (unless manually requested)
        if (!manualRequest && result is not ("failed" or "partiallySucceeded"))
        {
            return;
        }

        if (!manualRequest && sourceBranch.StartsWith("refs/pull/", StringComparison.Ordinal))
        {
            return;
        }

        _log?.Info("AnalysisAgent", $"Analyzing {project}/{definitionName} build #{buildId}...");

        _db.InsertBuildAnalysis(org, buildId);

        try
        {
            _db.UpdateBuildAnalysis(org, buildId, "running");
            await AnalyzeBuildAsync(org, buildId, project, definitionName, fullAnalysisCheck, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("AnalysisAgent", $"Failed to analyze build #{buildId}: {ex.Message}");
            _db.UpdateBuildAnalysis(org, buildId, "failed", diagnosisSummary: ex.Message);
        }
    }

    private async Task AnalyzeBuildAsync(
        string org, int buildId, string project, string definitionName,
        bool fullAnalysisCheck, CancellationToken ct)
    {
        // Step 1: Check known issues (unless skipped)
        if (!fullAnalysisCheck)
        {
            var (knownIssueMatches, errorText) = CheckKnownIssues(org, buildId);
            if (knownIssueMatches.Count > 0)
            {
                var summary = string.Join("\n", knownIssueMatches.Select(m =>
                {
                    var title = StripKnownBuildErrorPrefix(m.Title);
                    var url = $"https://github.com/{m.Repository}/issues/{m.IssueNumber}";
                    return $"[{m.Repository}#{m.IssueNumber}]({url}): {title}";
                }));
                _log?.Info("AnalysisAgent", $"  Build #{buildId} matches known issue(s): {summary}");

                var structuredErrors = GatherStructuredErrors(org, buildId);
                var knownIssueLogPath = SaveKnownIssueLog(org, project, definitionName, buildId, knownIssueMatches, structuredErrors);
                _db.UpdateBuildAnalysis(org, buildId, "skipped",
                    category: "known-issue",
                    diagnosisSummary: $"Matches known issue(s): {summary}",
                    logPath: knownIssueLogPath);
                return;
            }
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

    private (List<KnownIssueMatch> Matches, string ErrorText) CheckKnownIssues(string org, int buildId)
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
            return ([], "");
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
            return ([], combinedErrors);
        }

        return (_knownIssues.FindMatches(repo, combinedErrors), combinedErrors);
    }

    /// <summary>
    /// Gathers structured error context for a build: timeline issues with job context
    /// and test failures with run/test identifiers. Used for the known issue log.
    /// </summary>
    private StructuredErrorContext GatherStructuredErrors(string org, int buildId)
    {
        var context = new StructuredErrorContext();

        context.TimelineIssues = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT record_name, record_type, parent_name, issue_type, issue_message, issue_category, log_url
                FROM build_timeline_issues
                WHERE organization = @org AND build_id = @buildId
                ORDER BY parent_name, record_name
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var issues = new List<StructuredTimelineIssue>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                issues.Add(new StructuredTimelineIssue
                {
                    RecordName = reader.GetString(0),
                    RecordType = reader.GetString(1),
                    JobName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IssueType = reader.GetString(3),
                    Message = reader.GetString(4),
                    Category = reader.IsDBNull(5) ? null : reader.GetString(5),
                    LogUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
            return issues;
        });

        context.TestFailures = _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT r.run_id, r.run_name, tr.test_case_title, tr.error_message, tr.stack_trace,
                       tr.helix_job_name, tr.helix_work_item_name
                FROM test_results tr
                JOIN test_runs r ON tr.organization = r.organization AND tr.run_id = r.run_id
                WHERE r.organization = @org AND r.build_id = @buildId
                  AND tr.outcome = 'Failed'
                ORDER BY r.run_name, tr.test_case_title
                LIMIT 50
                """;
            cmd.Parameters.AddWithValue("@org", org);
            cmd.Parameters.AddWithValue("@buildId", buildId);

            var failures = new List<StructuredTestFailure>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                failures.Add(new StructuredTestFailure
                {
                    RunId = reader.GetInt32(0),
                    RunName = reader.GetString(1),
                    TestName = reader.GetString(2),
                    ErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3),
                    StackTrace = reader.IsDBNull(4) ? null : reader.GetString(4),
                    HelixJobName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    HelixWorkItemName = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
            return failures;
        });

        return context;
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
            parsed.Category = StripMarkdownBold(categoryText.Split('\n')[0].Trim().ToLowerInvariant());
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
            parsed.Confidence = StripMarkdownBold(confidenceText.Split('\n')[0].Trim().ToLowerInvariant());
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

    private static string StripMarkdownBold(string text) =>
        text.Replace("**", "");

    internal static string StripKnownBuildErrorPrefix(string title)
    {
        const string prefix = "[Known Build Error]";
        var trimmed = title.TrimStart();
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[prefix.Length..].TrimStart();
        }
        return title;
    }

    private string SaveKnownIssueLog(
        string org, string project, string definitionName, int buildId,
        List<KnownIssueMatch> matches, StructuredErrorContext errors)
    {
        var logDir = Path.Combine(_logDir, org, project, definitionName);
        Directory.CreateDirectory(logDir);

        var logPath = Path.Combine(logDir, $"{buildId}.md");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Known Issue Match: {project}/{definitionName} #{buildId}");
        sb.AppendLine($"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Organization:** {org}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Matched Issues");
        sb.AppendLine();
        foreach (var match in matches)
        {
            var url = $"https://github.com/{match.Repository}/issues/{match.IssueNumber}";
            sb.AppendLine($"### [{match.Repository}#{match.IssueNumber}]({url}): {StripKnownBuildErrorPrefix(match.Title)}");
            if (match.ErrorMessage is not null)
            {
                sb.AppendLine($"- **ErrorMessage:** `{match.ErrorMessage}`");
            }
            if (match.ErrorPattern is not null)
            {
                sb.AppendLine($"- **ErrorPattern:** `{match.ErrorPattern}`");
            }
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Build Errors (Structured)");
        sb.AppendLine();
        sb.AppendLine("The following is structured JSON for LLM consumption. Each entry includes");
        sb.AppendLine("full context needed to investigate the error further.");
        sb.AppendLine();

        if (errors.TimelineIssues.Count > 0)
        {
            sb.AppendLine("### Timeline Issues");
            sb.AppendLine();
            sb.AppendLine("These are errors/warnings from the Azure DevOps build timeline (pipeline jobs/tasks).");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(
                errors.TimelineIssues.Select(i => new
                {
                    i.JobName,
                    i.RecordName,
                    i.RecordType,
                    i.IssueType,
                    i.Message,
                    i.Category,
                    i.LogUrl,
                }),
                JsonOptions.Indented));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (errors.TestFailures.Count > 0)
        {
            sb.AppendLine("### Test Failures");
            sb.AppendLine();
            sb.AppendLine("These are failed test results from Azure DevOps test runs associated with this build.");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(
                errors.TestFailures.Select(f => new
                {
                    f.RunId,
                    f.RunName,
                    f.TestName,
                    f.ErrorMessage,
                    f.StackTrace,
                    f.HelixJobName,
                    f.HelixWorkItemName,
                }),
                JsonOptions.Indented));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (errors.TimelineIssues.Count == 0 && errors.TestFailures.Count == 0)
        {
            sb.AppendLine("[dim]No structured error data available.[/]");
        }

        File.WriteAllText(logPath, sb.ToString());
        return logPath;
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
    /// Bypasses the recency filter — any build can be analyzed on demand.
    /// </summary>
    public void RequestAnalysis(string organization, int buildId, bool fullAnalysisCheck = false)
    {
        _db.DeleteBuildAnalysis(organization, buildId);
        _channel.Writer.TryWrite(new AnalysisRequest(organization, buildId, fullAnalysisCheck, ManualRequest: true));
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

internal record AnalysisRequest(string Organization, int BuildId, bool FullAnalysisCheck = false, bool ManualRequest = false);

internal class StructuredErrorContext
{
    public List<StructuredTimelineIssue> TimelineIssues { get; set; } = [];
    public List<StructuredTestFailure> TestFailures { get; set; } = [];
}

internal class StructuredTimelineIssue
{
    public required string RecordName { get; init; }
    public required string RecordType { get; init; }
    public string? JobName { get; init; }
    public required string IssueType { get; init; }
    public required string Message { get; init; }
    public string? Category { get; init; }
    public string? LogUrl { get; init; }
}

internal class StructuredTestFailure
{
    public required int RunId { get; init; }
    public required string RunName { get; init; }
    public required string TestName { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? HelixJobName { get; init; }
    public string? HelixWorkItemName { get; init; }
}
