using System.Collections.Concurrent;
using Azure.Core;

namespace Tiger;

/// <summary>
/// Creates <see cref="AzdoClient"/> instances for a given organization and project.
/// Shares <see cref="AzdoRateLimitState"/> across all clients for the same organization
/// so rate-limit pressure from one request is visible to all.
/// </summary>
public sealed class AzdoClientFactory
{
    private readonly Func<string, string, AzdoRateLimitState, AzdoClient> _factory;
    private readonly ConcurrentDictionary<string, AzdoRateLimitState> _rateLimits = new(StringComparer.OrdinalIgnoreCase);

    public AzdoClientFactory(TokenCredential credential)
    {
        _factory = (org, proj, state) => AzdoClient.Create(credential, org, proj, state);
    }

    /// <summary>
    /// Creates a factory from a custom delegate. Useful for testing.
    /// </summary>
    public AzdoClientFactory(Func<string, string, AzdoClient> factory)
    {
        _factory = (org, proj, _) => factory(org, proj);
    }

    public AzdoClient Create(string organization, string project)
    {
        var state = _rateLimits.GetOrAdd(organization, _ => new AzdoRateLimitState());
        return _factory(organization, project, state);
    }

    /// <summary>
    /// Gets the rate-limit state for an organization. Returns null if no requests
    /// have been made to that org yet.
    /// </summary>
    public AzdoRateLimitState? GetRateLimitState(string organization) =>
        _rateLimits.TryGetValue(organization, out var state) ? state : null;

    /// <summary>
    /// Returns all tracked rate-limit states (one per organization that has had requests).
    /// </summary>
    public IEnumerable<AzdoRateLimitState> GetAllRateLimitStates() => _rateLimits.Values;
}
