using System.Collections.Concurrent;

namespace Tiger;

/// <summary>
/// Thread-safe in-memory log for background services (backfill, poller, etc.).
/// Services write entries; the dashboard status view reads them.
/// </summary>
public sealed class ServiceLog
{
    private readonly ConcurrentQueue<ServiceLogEntry> _entries = new();
    private const int MaxEntries = 500;

    /// <summary>
    /// Raised when a new entry is added. The event fires on the writer's thread.
    /// </summary>
    public event Action? EntryAdded;

    public void Log(string service, string message, ServiceLogLevel level = ServiceLogLevel.Info)
    {
        _entries.Enqueue(new ServiceLogEntry(DateTime.UtcNow, service, message, level));

        // Trim old entries
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);

        EntryAdded?.Invoke();
    }

    public void Info(string service, string message) => Log(service, message, ServiceLogLevel.Info);
    public void Success(string service, string message) => Log(service, message, ServiceLogLevel.Success);
    public void Warning(string service, string message) => Log(service, message, ServiceLogLevel.Warning);
    public void Error(string service, string message) => Log(service, message, ServiceLogLevel.Error);

    /// <summary>
    /// Returns a snapshot of recent entries (newest last).
    /// </summary>
    public List<ServiceLogEntry> GetRecent(int count = 50) =>
        _entries.ToArray().TakeLast(count).ToList();
}

public enum ServiceLogLevel { Info, Success, Warning, Error }

public record ServiceLogEntry(DateTime Timestamp, string Service, string Message, ServiceLogLevel Level);
