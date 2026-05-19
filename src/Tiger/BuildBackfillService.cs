using Microsoft.Extensions.Logging;

namespace Tiger;

/// <summary>
/// Runs on startup to fill gaps in the build database. When tiger is shut down
/// and restarted, there may be builds that completed during the downtime.
/// This service queries AzDO for all completed builds since the last poll time
/// and ingests them into SQLite.
/// </summary>
public sealed class BuildBackfillService
{
    private readonly TigerConfig _config;
    private readonly TigerDatabase _db;
    private readonly BuildIngestionService _ingestion;
    private readonly Func<string, string, AzdoClient> _clientFactory;
    private readonly ServiceLog _log;

    public BuildBackfillService(
        TigerConfig config,
        TigerDatabase db,
        BuildIngestionService ingestion,
        Func<string, string, AzdoClient> clientFactory,
        ServiceLog log)
    {
        _config = config;
        _db = db;
        _ingestion = ingestion;
        _clientFactory = clientFactory;
        _log = log;
    }

    /// <summary>
    /// Backfills all configured sources.
    /// Returns the total number of builds ingested.
    /// </summary>
    public async Task<int> BackfillAsync(CancellationToken ct = default)
    {
        _log.Info("Backfill", "Starting backfill...");
        var totalIngested = 0;

        foreach (var source in _config.Sources)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var count = await BackfillSourceAsync(source, ct);
                totalIngested += count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error("Backfill",
                    $"Failed for {source.Organization}/{source.Project}: {ex.Message}");
            }
        }

        _log.Success("Backfill", $"Complete — {totalIngested} builds ingested");
        return totalIngested;
    }

    private async Task<int> BackfillSourceAsync(AzdoSource source, CancellationToken ct)
    {
        var lastPollTime = GetLastPollTime(source.Organization, source.Project);
        var since = lastPollTime ?? DateTime.UtcNow - TimeSpan.FromDays(_config.BackfillDays);

        _log.Info("Backfill",
            $"{source.Organization}/{source.Project} — fetching builds since {TigerUtils.FormatLocalTime(since)}");

        var client = _clientFactory(source.Organization, source.Project);

        // If repositories are configured, query per-repo; otherwise query all
        List<AzdoBuild> builds;
        if (source.Repositories.Count > 0)
        {
            builds = [];
            foreach (var repo in source.Repositories)
            {
                _log.Info("Backfill", $"  Querying {repo}...");
                var repoBuilds = await client.GetCompletedBuildsSinceAsync(since, repositoryId: repo, ct: ct);
                builds.AddRange(repoBuilds);
            }
        }
        else
        {
            builds = await client.GetCompletedBuildsSinceAsync(since, ct: ct);
        }

        // Filter out builds we already have
        var existingIds = GetExistingBuildIds(source.Organization, source.Project);
        var newBuilds = builds.Where(b => !existingIds.Contains(b.Id)).ToList();

        if (newBuilds.Count == 0)
        {
            _log.Info("Backfill", $"{source.Organization}/{source.Project} — no new builds");
            return 0;
        }

        _log.Info("Backfill",
            $"{source.Organization}/{source.Project} — ingesting {newBuilds.Count} new builds");

        // Ingest in batches
        var ingested = 0;
        foreach (var batch in Batch(newBuilds, 10))
        {
            ct.ThrowIfCancellationRequested();
            await _ingestion.IngestBuildsAsync(client, source.Organization, source.Project, batch);
            ingested += batch.Count;
            _log.Info("Backfill",
                $"{source.Organization}/{source.Project} — {ingested}/{newBuilds.Count} builds ingested");
        }

        // Update watermark to the highest build ID if it's newer
        var maxBuildId = newBuilds.Max(b => b.Id);
        var currentWatermark = GetWatermark(source.Organization, source.Project);
        if (maxBuildId > currentWatermark)
        {
            SetWatermark(source.Organization, source.Project, maxBuildId);
        }

        _log.Success("Backfill",
            $"{source.Organization}/{source.Project} — done ({ingested} builds)");
        return ingested;
    }

    private DateTime? GetLastPollTime(string organization, string project)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT last_poll_time FROM poll_watermarks
            WHERE organization = @org AND project = @proj
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        var result = cmd.ExecuteScalar();
        if (result is string s && DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    private HashSet<int> GetExistingBuildIds(string organization, string project)
    {
        var ids = new HashSet<int>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT build_id FROM builds
            WHERE organization = @org AND project = @proj
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    private int GetWatermark(string organization, string project)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT last_build_id FROM poll_watermarks
            WHERE organization = @org AND project = @proj
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        var result = cmd.ExecuteScalar();
        return result is not null ? Convert.ToInt32(result) : 0;
    }

    private void SetWatermark(string organization, string project, int buildId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO poll_watermarks (organization, project, last_build_id, last_poll_time)
            VALUES (@org, @proj, @buildId, datetime('now'))
            ON CONFLICT (organization, project) DO UPDATE SET
                last_build_id = MAX(last_build_id, @buildId),
                last_poll_time = datetime('now')
            """;
        cmd.Parameters.AddWithValue("@org", organization);
        cmd.Parameters.AddWithValue("@proj", project);
        cmd.Parameters.AddWithValue("@buildId", buildId);
        cmd.ExecuteNonQuery();
    }

    private static List<List<T>> Batch<T>(List<T> items, int batchSize)
    {
        var batches = new List<List<T>>();
        for (var i = 0; i < items.Count; i += batchSize)
            batches.Add(items.GetRange(i, Math.Min(batchSize, items.Count - i)));
        return batches;
    }
}
