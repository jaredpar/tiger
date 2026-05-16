namespace Tiger;

/// <summary>
/// Abstraction over build data that can be satisfied by either
/// live AzDO queries or the local SQLite cache.
/// </summary>
public interface IBuildDataSource
{
    /// <summary>Get recent builds, optionally filtered by definition ID.</summary>
    Task<List<AzdoBuild>> GetRecentBuildsAsync(int? definitionId = null, int top = 10);

    /// <summary>Get builds for a specific GitHub repository.</summary>
    Task<List<AzdoBuild>> GetBuildsForRepositoryAsync(string repository, int top = 10, string? reasonFilter = null);

    /// <summary>Get builds associated with a pull request.</summary>
    Task<List<AzdoBuild>> GetBuildsForPullRequestAsync(string repository, int prNumber, int top = 10);

    /// <summary>Get failed test results for a build.</summary>
    Task<List<AzdoTestResult>> GetTestFailuresAsync(int buildId);

    /// <summary>Get test summary (pass/fail/skip counts) grouped by job.</summary>
    Task<List<AzdoJobTestSummary>> GetTestSummaryByJobAsync(int buildId);
}
