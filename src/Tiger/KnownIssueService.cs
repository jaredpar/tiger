using System.Diagnostics;
using System.Text.Json;

namespace Tiger;

/// <summary>
/// Fetches and stores known build issues from GitHub repos.
/// Issues are identified by the "Known Build Error" label and contain a JSON blob
/// with error matching patterns (ErrorMessage/ErrorPattern).
/// Polls periodically (every 15 minutes) to keep the cache fresh.
/// </summary>
public sealed class KnownIssueService : IDisposable
{
    private readonly TigerDatabase _db;
    private readonly TigerConfig _config;
    private readonly ServiceLog? _log;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    private const int PollIntervalMinutes = 15;

    public bool IsRunning => _pollingTask is not null && !_pollingTask.IsCompleted;

    public KnownIssueService(TigerConfig config, TigerDatabase db, ServiceLog? log = null)
    {
        _config = config;
        _db = db;
        _log = log;
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
        var repos = _config.Sources.SelectMany(s => s.Repositories).Distinct().ToList();

        while (!ct.IsCancellationRequested)
        {
            foreach (var repo in repos)
            {
                if (ct.IsCancellationRequested)
                    break;
                try
                {
                    await RefreshKnownIssuesAsync(repo, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Error("KnownIssues", $"Failed to refresh {repo}: {ex.Message}");
                }
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

    /// <summary>
    /// Fetches open known issues for a repository via the gh CLI and stores them in the DB.
    /// Issues no longer open are marked as closed. Issues closed for more than 7 days are purged.
    /// </summary>
    public async Task RefreshKnownIssuesAsync(string repository, CancellationToken ct = default)
    {
        _log?.Info("KnownIssues", $"Refreshing known issues for {repository}...");

        var issues = await FetchIssuesFromGitHub(repository, ct);
        if (issues is null)
            return;

        var openIssueNumbers = new HashSet<int>();

        _db.WithTransaction((conn, tx) =>
        {
            // Upsert all currently-open issues
            foreach (var issue in issues)
            {
                var parsed = ParseKnownIssueBody(issue.Body);
                if (parsed is null)
                    continue;

                openIssueNumbers.Add(issue.Number);

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO known_issues
                        (repository, issue_number, title, error_message, error_pattern, build_retry, exclude_console_log, state, closed_at)
                    VALUES
                        (@repo, @number, @title, @errorMessage, @errorPattern, @buildRetry, @excludeConsoleLog, 'open', NULL)
                    ON CONFLICT (repository, issue_number) DO UPDATE SET
                        title = @title,
                        error_message = @errorMessage,
                        error_pattern = @errorPattern,
                        build_retry = @buildRetry,
                        exclude_console_log = @excludeConsoleLog,
                        state = 'open',
                        closed_at = NULL,
                        fetched_at = datetime('now')
                    """;
                cmd.Parameters.AddWithValue("@repo", repository);
                cmd.Parameters.AddWithValue("@number", issue.Number);
                cmd.Parameters.AddWithValue("@title", issue.Title);
                cmd.Parameters.AddWithValue("@errorMessage", (object?)parsed.ErrorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@errorPattern", (object?)parsed.ErrorPattern ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@buildRetry", parsed.BuildRetry ? 1 : 0);
                cmd.Parameters.AddWithValue("@excludeConsoleLog", parsed.ExcludeConsoleLog ? 1 : 0);
                cmd.ExecuteNonQuery();
            }

            // Mark issues that are no longer open as closed (if not already closed)
            using (var markCmd = conn.CreateCommand())
            {
                markCmd.Transaction = tx;
                var placeholders = string.Join(",", openIssueNumbers.Select((_, i) => $"@n{i}"));
                var whereClause = openIssueNumbers.Count > 0
                    ? $"AND issue_number NOT IN ({placeholders})"
                    : "";
                markCmd.CommandText = $"""
                    UPDATE known_issues
                    SET state = 'closed', closed_at = COALESCE(closed_at, datetime('now'))
                    WHERE repository = @repo AND state = 'open' {whereClause}
                    """;
                markCmd.Parameters.AddWithValue("@repo", repository);
                var i = 0;
                foreach (var num in openIssueNumbers)
                {
                    markCmd.Parameters.AddWithValue($"@n{i}", num);
                    i++;
                }
                markCmd.ExecuteNonQuery();
            }

            // Purge issues that have been closed for more than 7 days
            using (var purgeCmd = conn.CreateCommand())
            {
                purgeCmd.Transaction = tx;
                purgeCmd.CommandText = """
                    DELETE FROM known_issues
                    WHERE repository = @repo AND state = 'closed'
                      AND closed_at < datetime('now', '-7 days')
                    """;
                purgeCmd.Parameters.AddWithValue("@repo", repository);
                purgeCmd.ExecuteNonQuery();
            }
        });

        _log?.Info("KnownIssues", $"  {repository}: {openIssueNumbers.Count} open known issue(s)");
    }

    /// <summary>
    /// Checks if an error message matches any known issue for the given repository.
    /// Returns the matching known issues (issue number + title).
    /// </summary>
    public List<KnownIssueMatch> FindMatches(string repository, string errorText)
    {
        return _db.WithCommand(cmd =>
        {
            var matches = new List<KnownIssueMatch>();
            cmd.CommandText = """
                SELECT issue_number, title, error_message, error_pattern
                FROM known_issues
                WHERE repository = @repo
                """;
            cmd.Parameters.AddWithValue("@repo", repository);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var issueNumber = reader.GetInt32(0);
                var title = reader.GetString(1);
                var errorMessage = reader.IsDBNull(2) ? null : reader.GetString(2);
                var errorPattern = reader.IsDBNull(3) ? null : reader.GetString(3);

                if (IsMatch(errorText, errorMessage, errorPattern))
                {
                    matches.Add(new KnownIssueMatch(repository, issueNumber, title));
                }
            }

            return matches;
        });
    }

    private static bool IsMatch(string errorText, string? errorMessage, string? errorPattern)
    {
        if (errorMessage is not null)
        {
            return IsMessageMatch(errorText, errorMessage);
        }

        if (errorPattern is not null)
        {
            return IsPatternMatch(errorText, errorPattern);
        }

        return false;
    }

    /// <summary>
    /// ErrorMessage can be a single string or a JSON array of strings.
    /// For arrays, all strings must match in order (each on a subsequent line).
    /// </summary>
    private static bool IsMessageMatch(string errorText, string errorMessage)
    {
        // Try to parse as JSON array first
        if (errorMessage.TrimStart().StartsWith('['))
        {
            try
            {
                var messages = JsonSerializer.Deserialize<string[]>(errorMessage);
                if (messages is not null)
                    return IsOrderedContainsMatch(errorText, messages);
            }
            catch (JsonException)
            {
                // Fall through to single-string match
            }
        }

        // Single string: simple contains check
        return errorText.Contains(errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ErrorPattern can be a single regex or a JSON array of regexes.
    /// For arrays, all patterns must match in order (each on a subsequent line).
    /// </summary>
    private static bool IsPatternMatch(string errorText, string errorPattern)
    {
        // Try to parse as JSON array first
        if (errorPattern.TrimStart().StartsWith('['))
        {
            try
            {
                var patterns = JsonSerializer.Deserialize<string[]>(errorPattern);
                if (patterns is not null)
                    return IsOrderedRegexMatch(errorText, patterns);
            }
            catch (JsonException)
            {
                // Fall through to single pattern
            }
        }

        // Single pattern: regex match against any line
        try
        {
            var regex = new System.Text.RegularExpressions.Regex(errorPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
            var lines = errorText.Split('\n');
            return lines.Any(line => regex.IsMatch(line));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// All messages must be found in order, each matching a subsequent line (contains check).
    /// </summary>
    private static bool IsOrderedContainsMatch(string errorText, string[] messages)
    {
        var lines = errorText.Split('\n');
        var searchStart = 0;
        foreach (var msg in messages)
        {
            var found = false;
            for (var i = searchStart; i < lines.Length; i++)
            {
                if (lines[i].Contains(msg, StringComparison.OrdinalIgnoreCase))
                {
                    searchStart = i + 1;
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    /// <summary>
    /// All patterns must match in order, each matching a subsequent line (regex).
    /// </summary>
    private static bool IsOrderedRegexMatch(string errorText, string[] patterns)
    {
        var lines = errorText.Split('\n');
        var searchStart = 0;
        foreach (var pattern in patterns)
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1));
                var found = false;
                for (var i = searchStart; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        searchStart = i + 1;
                        found = true;
                        break;
                    }
                }
                if (!found)
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private async Task<List<GitHubIssue>?> FetchIssuesFromGitHub(string repository, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("gh",
                $"issue list --repo {repository} --label \"Known Build Error\" --state open --json number,title,body --limit 100")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                _log?.Warning("KnownIssues", "Failed to start gh process");
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                _log?.Warning("KnownIssues", $"gh issue list failed (exit {process.ExitCode}): {stderr.Trim()}");
                return null;
            }

            return JsonSerializer.Deserialize<List<GitHubIssue>>(output, JsonOptions.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Warning("KnownIssues", $"Failed to fetch issues: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the known issue JSON blob from the issue body.
    /// Looks for a json code block containing ErrorMessage/ErrorPattern.
    /// </summary>
    internal static KnownIssueParsed? ParseKnownIssueBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // Find JSON code block: ```json ... ```
        var jsonStart = body.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart < 0)
            return null;

        jsonStart = body.IndexOf('\n', jsonStart);
        if (jsonStart < 0)
            return null;
        jsonStart++;

        var jsonEnd = body.IndexOf("```", jsonStart, StringComparison.Ordinal);
        if (jsonEnd < 0)
            return null;

        var jsonText = body[jsonStart..jsonEnd].Trim();
        if (string.IsNullOrEmpty(jsonText))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            string? errorMessage = null;
            string? errorPattern = null;
            var buildRetry = false;
            var excludeConsoleLog = false;

            if (root.TryGetProperty("ErrorMessage", out var emProp))
            {
                errorMessage = emProp.ValueKind == JsonValueKind.Array
                    ? emProp.GetRawText()
                    : emProp.GetString();
            }

            if (root.TryGetProperty("ErrorPattern", out var epProp))
            {
                errorPattern = epProp.ValueKind == JsonValueKind.Array
                    ? epProp.GetRawText()
                    : epProp.GetString();
            }

            if (root.TryGetProperty("BuildRetry", out var brProp))
                buildRetry = brProp.GetBoolean();

            if (root.TryGetProperty("ExcludeConsoleLog", out var eclProp))
                excludeConsoleLog = eclProp.GetBoolean();

            if (errorMessage is null && errorPattern is null)
                return null;

            return new KnownIssueParsed(errorMessage, errorPattern, buildRetry, excludeConsoleLog);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GitHubIssue(
        [property: System.Text.Json.Serialization.JsonPropertyName("number")] int Number,
        [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
        [property: System.Text.Json.Serialization.JsonPropertyName("body")] string? Body);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public sealed record KnownIssueParsed(
    string? ErrorMessage,
    string? ErrorPattern,
    bool BuildRetry,
    bool ExcludeConsoleLog);

public sealed record KnownIssueMatch(
    string Repository,
    int IssueNumber,
    string Title);
