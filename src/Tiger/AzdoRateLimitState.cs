using System.Net.Http.Headers;

namespace Tiger;

/// <summary>
/// Tracks AzDO rate-limit headers across all requests for an organization.
/// Thread-safe — updated by concurrent HTTP requests, read by the work loop.
/// </summary>
public sealed class AzdoRateLimitState
{
    private readonly object _lock = new();
    private double _remaining = double.MaxValue;
    private double _limit = double.MaxValue;
    private DateTime _retryAfter = DateTime.MinValue;

    /// <summary>Fraction of quota remaining (0.0 – 1.0). 1.0 means no pressure.</summary>
    public double RemainingFraction
    {
        get
        {
            lock (_lock)
            {
                return _limit > 0 ? Math.Clamp(_remaining / _limit, 0, 1) : 1.0;
            }
        }
    }

    /// <summary>True if we've been told to back off via Retry-After.</summary>
    public bool ShouldDelay
    {
        get
        {
            lock (_lock)
            {
                return DateTime.UtcNow < _retryAfter;
            }
        }
    }

    /// <summary>How long until we can send again. Zero if not throttled.</summary>
    public TimeSpan DelayRemaining
    {
        get
        {
            lock (_lock)
            {
                var remaining = _retryAfter - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>
    /// Called after every HTTP response to update rate-limit state from headers.
    /// </summary>
    public void Update(HttpResponseHeaders headers)
    {
        lock (_lock)
        {
            if (headers.TryGetValues("X-RateLimit-Remaining", out var remVals)
                && double.TryParse(remVals.FirstOrDefault(), out var rem))
            {
                _remaining = rem;
            }

            if (headers.TryGetValues("X-RateLimit-Limit", out var limVals)
                && double.TryParse(limVals.FirstOrDefault(), out var lim))
            {
                _limit = lim;
            }

            if (headers.TryGetValues("Retry-After", out var retryVals)
                && double.TryParse(retryVals.FirstOrDefault(), out var secs))
            {
                _retryAfter = DateTime.UtcNow.AddSeconds(secs);
            }
        }
    }
}
