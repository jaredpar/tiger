using Azure.Core;

namespace Tiger;

/// <summary>
/// Creates <see cref="AzdoClient"/> instances for a given organization and project.
/// </summary>
public sealed class AzdoClientFactory
{
    private readonly Func<string, string, AzdoClient> _factory;

    public AzdoClientFactory(TokenCredential credential)
    {
        _factory = (org, proj) => AzdoClient.Create(credential, org, proj);
    }

    /// <summary>
    /// Creates a factory from a custom delegate. Useful for testing.
    /// </summary>
    public AzdoClientFactory(Func<string, string, AzdoClient> factory)
    {
        _factory = factory;
    }

    public AzdoClient Create(string organization, string project) =>
        _factory(organization, project);
}
