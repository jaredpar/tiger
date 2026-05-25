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
    private readonly Func<string, string, AzdoClient> _clientFactory;
    private readonly ServiceLog? _log;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    /// <summary>
    /// Called when new completed builds are discovered. Receives the AzdoClient,
    /// org, project, and the list of new builds.
    /// </summary>
    public Func<AzdoClient, string, string, List<AzdoBuild>, Task>? OnNewBuilds { get; set; }

    public bool IsRunning => _pollingTask is not null && !_pollingTask.IsCompleted;

    public BuildPoller(
        TigerConfig config,
        TigerDatabase db,
        Func<string, string, AzdoClient> clientFactory,
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

    private async Task PollSourceAsync(AzdoSource source, CancellationToken ct)
    {
        var watermark = GetWatermark(source.Organization, source.Project);
        var client = _clientFactory(source.Organization, source.Project);

        // Fetch recent completed builds, filtered by repository if configured
        List<AzdoBuild> builds;
        if (source.Repositories.Count > 0)
        {
            builds = [];
            foreach (var repo in source.Repositories)
            {
                var repoBuilds = await client.GetBuildsForRepositoryAsync(repo, top: 50);
                builds.AddRange(repoBuilds);
            }
        }
        else
        {
            builds = await client.GetRecentBuildsAsync(top: 50);
        }

        var newBuilds = builds
            .Where(b => b.Id > watermark && b.Status == "completed")
            .OrderBy(b => b.Id)
            .ToList();

        if (newBuilds.Count == 0) return;

        _log?.Info("Poller",
            $"Found {newBuilds.Count} new builds for {source.Organization}/{source.Project}");

        if (OnNewBuilds is not null)
        {
            await OnNewBuilds(client, source.Organization, source.Project, newBuilds);
        }

        // Update watermark to the highest build ID we processed
        var newWatermark = newBuilds.Max(b => b.Id);
        SetWatermark(source.Organization, source.Project, newWatermark);

        _log?.Success("Poller",
            $"Ingested {newBuilds.Count} builds for {source.Organization}/{source.Project}");
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
