using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Tiger;

/// <summary>
/// Polls configured AzDO org/project sources for completed builds,
/// tracks watermarks in SQLite, and invokes a callback for new builds.
/// </summary>
public sealed class BuildPoller : IDisposable
{
    private readonly TigerConfig _config;
    private readonly TigerDatabase _db;
    private readonly AzdoClientFactory _clientFactory;
    private readonly ServiceLog? _log;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    /// <summary>
    /// Called when new completed builds are discovered. Receives the org, project,
    /// and the list of new builds.
    /// </summary>
    public Func<string, string, List<AzdoBuild>, Task>? OnNewBuilds { get; set; }

    public bool IsRunning => _pollingTask is not null && !_pollingTask.IsCompleted;

    public BuildPoller(
        TigerConfig config,
        TigerDatabase db,
        AzdoClientFactory clientFactory,
        ServiceLog? log = null)
    {
        _config = config;
        _db = db;
        _clientFactory = clientFactory;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning) return;
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
                try { await _pollingTask; } catch (OperationCanceledException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _log?.Info("Poller", "Polling...");
            foreach (var source in _config.Sources)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await PollSourceAsync(source, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Error("Poller", $"Error polling {source.Organization}/{source.Project}: {ex.Message}");
                }
            }

            _log?.Info("Poller", $"Sleeping {_config.PollIntervalSeconds}s...");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task PollSourceAsync(AzdoSource source, CancellationToken ct)
    {
        var client = _clientFactory.Create(source.Organization, source.Project);

        // Fetch recent completed builds, filtered by repository if configured.
        // We use statusFilter=completed so only terminal builds are returned,
        // avoiding the old watermark race where in-progress builds caused the
        // watermark to advance past long-running builds.
        List<AzdoBuild> builds;
        if (source.Repositories.Count > 0)
        {
            builds = [];
            foreach (var repo in source.Repositories)
            {
                var repoBuilds = await client.GetBuildsForRepositoryAsync(repo, top: 50, statusFilter: "completed", repositoryType: source.RepositoryType, ct: ct);
                builds.AddRange(repoBuilds);
            }
        }
        else
        {
            builds = await client.GetRecentBuildsAsync(top: 50, statusFilter: "completed");
        }

        // Filter to builds not yet in the DB. Already-ingested builds are
        // skipped because INSERT OR IGNORE on tasks makes re-insertion a no-op.
        var newBuilds = FilterNewBuilds(source.Organization, builds);

        if (newBuilds.Count == 0) return;

        _log?.Info("Poller",
            $"Found {newBuilds.Count} new builds for {source.Organization}/{source.Project}");

        if (OnNewBuilds is not null)
        {
            await OnNewBuilds(source.Organization, source.Project, newBuilds);
        }

        _log?.Success("Poller",
            $"Ingested {newBuilds.Count} builds for {source.Organization}/{source.Project}");
    }

    /// <summary>
    /// Returns builds that are not yet in the database.
    /// </summary>
    internal List<AzdoBuild> FilterNewBuilds(string organization, List<AzdoBuild> builds)
    {
        var result = new List<AzdoBuild>();
        foreach (var build in builds)
        {
            if (!BuildExists(organization, build.Id))
            {
                result.Add(build);
            }
        }
        return result;
    }

    private bool BuildExists(string organization, int buildId)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = "SELECT 1 FROM builds WHERE organization = @org AND build_id = @buildId";
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            return cmd.ExecuteScalar() is not null;
        });
    }

    internal int GetWatermark(string organization, string project)
    {
        return _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                SELECT last_build_id FROM poll_watermarks
                WHERE organization = @org AND project = @proj
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@proj", project);
            var result = cmd.ExecuteScalar();
            return result is not null ? Convert.ToInt32(result) : 0;
        });
    }

    internal void SetWatermark(string organization, string project, int buildId)
    {
        _db.WithCommand(cmd =>
        {
            cmd.CommandText = """
                INSERT INTO poll_watermarks (organization, project, last_build_id, last_poll_time)
                VALUES (@org, @proj, @buildId, datetime('now'))
                ON CONFLICT (organization, project) DO UPDATE SET
                    last_build_id = @buildId,
                    last_poll_time = datetime('now')
                """;
            cmd.Parameters.AddWithValue("@org", organization);
            cmd.Parameters.AddWithValue("@proj", project);
            cmd.Parameters.AddWithValue("@buildId", buildId);
            cmd.ExecuteNonQuery();
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
