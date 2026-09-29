namespace Tiger;

/// <summary>
/// Periodically removes builds and their associated data after the configured retention period.
/// </summary>
public sealed class BuildRetentionService : IDisposable
{
    private static readonly TimeSpan s_cleanupInterval = TimeSpan.FromMinutes(15);

    private readonly TigerConfig _config;
    private readonly TigerDatabase _db;
    private readonly ServiceLog? _log;
    private CancellationTokenSource? _cts;
    private Task _cleanupTask = Task.CompletedTask;

    public bool IsRunning => !_cleanupTask.IsCompleted;

    public BuildRetentionService(TigerConfig config, TigerDatabase db, ServiceLog? log = null)
    {
        _config = config;
        _db = db;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _cleanupTask = CleanupLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try
            {
                await _cleanupTask;
            }
            catch (OperationCanceledException)
            {
            }
            _cts.Dispose();
            _cts = null;
            _cleanupTask = Task.CompletedTask;
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var deletedCount = CleanupExpiredBuilds();
                _log?.Info("Retention", $"Deleted {deletedCount} build(s) older than {_config.BackfillDays} days.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Error("Retention", $"Failed to clean up expired builds: {ex.Message}");
            }

            try
            {
                await Task.Delay(s_cleanupInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal int CleanupExpiredBuilds()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_config.BackfillDays);
        return _db.DeleteBuildsOlderThan(cutoff);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
