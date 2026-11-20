using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tiger.Commands;

public class AzdoWaitCommand : AsyncCommand<AzdoWaitCommand.Settings>
{
    public class Settings : AzdoBuildSettings
    {
        [CommandOption("--job-name")]
        [Description("Wait for all jobs whose display names contain this text (case-insensitive)")]
        public string? JobName { get; set; }

        [CommandOption("--job-id")]
        [Description("Wait for the job with this timeline record GUID; combined with --job-name, both must match")]
        public string? JobId { get; set; }

        [CommandOption("--poll-interval")]
        [Description("Seconds between checks (default: 10)")]
        public int PollIntervalSeconds { get; set; } = 10;

        [CommandOption("--timeout")]
        [Description("Maximum seconds to wait (default: no timeout)")]
        public int? TimeoutSeconds { get; set; }

        public override ValidationResult Validate()
        {
            if (BuildId <= 0)
            {
                return ValidationResult.Error("The build ID must be positive.");
            }
            if (JobName is not null && string.IsNullOrWhiteSpace(JobName))
            {
                return ValidationResult.Error("--job-name must not be empty or whitespace.");
            }
            if (JobId is not null && !Guid.TryParse(JobId, out _))
            {
                return ValidationResult.Error("--job-id must be a timeline record GUID.");
            }
            if (PollIntervalSeconds <= 0)
            {
                return ValidationResult.Error("--poll-interval must be positive.");
            }
            if (TimeoutSeconds <= 0)
            {
                return ValidationResult.Error("--timeout must be positive.");
            }
            return ValidationResult.Success();
        }
    }

    internal sealed record WaitResult(
        [property: JsonPropertyName("build")] AzdoBuild Build,
        [property: JsonPropertyName("jobs")] IReadOnlyList<AzdoTimelineRecord> Jobs);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        try
        {
            var result = await WaitAsync(settings.CreateClient(), settings, Console.Error, ct);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions.Indented));
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or TimeoutException or JsonException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions.Indented));
            return 1;
        }
    }

    internal static async Task<WaitResult> WaitAsync(
        AzdoClient client,
        Settings settings,
        TextWriter progress,
        CancellationToken ct = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var validation = settings.Validate();
        if (!validation.Successful)
        {
            throw new ArgumentException(validation.Message, nameof(settings));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (settings.TimeoutSeconds is int seconds)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }
        var token = timeout.Token;
        delay ??= Task.Delay;
        var jobId = settings.JobId is null ? (Guid?)null : Guid.Parse(settings.JobId);
        var filterJobs = settings.JobName is not null || jobId is not null;
        string? previousProgress = null;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var build = await client.GetBuildAsync(settings.BuildId, token);
                var buildCompleted = string.Equals(build.Status, "completed", StringComparison.OrdinalIgnoreCase);
                List<AzdoTimelineRecord> jobs = [];
                List<AzdoTimelineRecord> pendingSources = [];
                // Queued builds may not have a timeline yet.
                if (filterJobs && !string.Equals(build.Status, "notStarted", StringComparison.OrdinalIgnoreCase))
                {
                    var timeline = await client.GetTimelineAsync(settings.BuildId, token);
                    jobs = timeline.Records
                        .Where(IsJob)
                        .Where(r => settings.JobName is null || r.Name.Contains(settings.JobName, StringComparison.OrdinalIgnoreCase))
                        .Where(r => jobId is null || Guid.TryParse(r.Id, out var id) && id == jobId)
                        .OrderBy(r => r.Order)
                        .ThenBy(r => r.Id, StringComparer.Ordinal)
                        .ToList();
                    // A record ID identifies a single job, but a name can match jobs that AzDO has not
                    // created yet: Job records only appear once their stage/phase starts running.
                    if (jobId is null)
                    {
                        pendingSources = GetUnexpandedJobSources(timeline.Records);
                    }
                }

                var snapshot = JsonSerializer.Serialize(new
                {
                    buildId = build.Id,
                    status = build.Status,
                    result = build.Result,
                    jobs = jobs.Select(j => new { id = j.Id, name = j.Name, state = j.State, result = j.Result }),
                    pendingJobSources = pendingSources.Select(r => new { id = r.Id, name = r.Name, type = r.RecordType, state = r.State }),
                }, JsonOptions.Indented);
                if (snapshot != previousProgress)
                {
                    await progress.WriteLineAsync(snapshot.AsMemory(), token);
                    previousProgress = snapshot;
                }

                if (!filterJobs && buildCompleted ||
                    filterJobs && jobs.Count > 0 && pendingSources.Count == 0 && jobs.All(IsCompleted))
                {
                    return new WaitResult(build, jobs);
                }
                if (buildCompleted)
                {
                    throw new InvalidOperationException(jobs.Count == 0
                        ? $"Build {settings.BuildId} completed without any jobs matching the supplied filters."
                        : $"Build {settings.BuildId} completed but matching jobs are not marked completed.");
                }

                await delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {settings.TimeoutSeconds} seconds waiting for build {settings.BuildId}.");
        }
    }

    private static bool IsJob(AzdoTimelineRecord r) =>
        string.Equals(r.RecordType, "Job", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompleted(AzdoTimelineRecord r) =>
        string.Equals(r.State, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stage and Phase records that have not completed and do not yet have any Job records beneath
    /// them. These can still produce new jobs (including ones matching a name filter).
    /// </summary>
    internal static List<AzdoTimelineRecord> GetUnexpandedJobSources(IReadOnlyList<AzdoTimelineRecord> records)
    {
        var parentsWithJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byId = records.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var job in records.Where(IsJob))
        {
            var parentId = job.ParentId;
            while (parentId is not null && parentsWithJobs.Add(parentId) && byId.TryGetValue(parentId, out var parent))
            {
                parentId = parent.ParentId;
            }
        }

        return records
            .Where(r => string.Equals(r.RecordType, "Stage", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.RecordType, "Phase", StringComparison.OrdinalIgnoreCase))
            .Where(r => !IsCompleted(r) && !parentsWithJobs.Contains(r.Id))
            .OrderBy(r => r.Order)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }
}
