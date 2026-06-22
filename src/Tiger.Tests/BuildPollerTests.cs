using Xunit;

namespace Tiger.Tests;

public class BuildPollerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TigerDatabase _db;

    public BuildPollerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tiger-test-{Guid.NewGuid()}.db");
        _db = TigerDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void GetWatermark_ReturnsZeroWhenNoEntry()
    {
        var poller = CreatePoller();
        Assert.Equal(0, poller.GetWatermark("org", "proj"));
    }

    [Fact]
    public void SetWatermark_InsertsAndReads()
    {
        var poller = CreatePoller();
        poller.SetWatermark("org", "proj", 42);
        Assert.Equal(42, poller.GetWatermark("org", "proj"));
    }

    [Fact]
    public void SetWatermark_Updates()
    {
        var poller = CreatePoller();
        poller.SetWatermark("org", "proj", 10);
        poller.SetWatermark("org", "proj", 20);
        Assert.Equal(20, poller.GetWatermark("org", "proj"));
    }

    [Fact]
    public void SetWatermark_IsolatesOrgProject()
    {
        var poller = CreatePoller();
        poller.SetWatermark("org1", "proj1", 100);
        poller.SetWatermark("org2", "proj2", 200);
        Assert.Equal(100, poller.GetWatermark("org1", "proj1"));
        Assert.Equal(200, poller.GetWatermark("org2", "proj2"));
        Assert.Equal(0, poller.GetWatermark("org1", "proj2"));
    }

    [Fact]
    public async Task StartStop_IsRunning()
    {
        var config = new TigerConfig
        {
            PollIntervalSeconds = 3600, // long interval so it doesn't actually poll
            Sources = [],               // no sources to poll
        };
        var poller = new BuildPoller(config, _db, new AzdoClientFactory((org, proj) => throw new NotImplementedException()));

        Assert.False(poller.IsRunning);
        poller.Start();
        Assert.True(poller.IsRunning);

        await poller.StopAsync();
        Assert.False(poller.IsRunning);
    }

    [Fact]
    public void FilterNewBuilds_NewBuildsAreIncluded()
    {
        var poller = CreatePoller();
        var builds = new List<AzdoBuild>
        {
            MakeBuild(1, finishTime: new DateTime(2025, 1, 1, 12, 0, 0)),
            MakeBuild(2, finishTime: new DateTime(2025, 1, 1, 13, 0, 0)),
        };

        var result = poller.FilterNewBuilds("org", builds);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterNewBuilds_AlreadyIngestedBuildsAreSkipped()
    {
        var poller = CreatePoller();
        var build = MakeBuild(1, finishTime: new DateTime(2025, 1, 1, 12, 0, 0));

        // Pre-insert the build
        var service = new BuildIngestionService(_db);
        service.InsertBuild("org", "proj", build);

        var result = poller.FilterNewBuilds("org", [build]);
        Assert.Empty(result);
    }

    [Fact]
    public void FilterNewBuilds_LongRunningBuildNotMissed()
    {
        // Simulates the scenario where a long-running build completes after
        // other builds with higher IDs have already been ingested.
        var poller = CreatePoller();
        var service = new BuildIngestionService(_db);

        // Build 100 was ingested in a previous poll cycle
        var earlyBuild = MakeBuild(100, finishTime: new DateTime(2025, 1, 1, 10, 0, 0));
        service.InsertBuild("org", "proj", earlyBuild);

        // Build 200 was also ingested (higher ID)
        var laterBuild = MakeBuild(200, finishTime: new DateTime(2025, 1, 1, 11, 0, 0));
        service.InsertBuild("org", "proj", laterBuild);

        // Build 150 was long-running and just completed — it's new
        var longRunning = MakeBuild(150, finishTime: new DateTime(2025, 1, 1, 12, 0, 0));

        // The API returns all three (completed), our filter should pick up only 150
        var result = poller.FilterNewBuilds("org", [earlyBuild, longRunning, laterBuild]);
        Assert.Single(result);
        Assert.Equal(150, result[0].Id);
    }

    private BuildPoller CreatePoller()
    {
        var config = new TigerConfig { Sources = [] };
        return new BuildPoller(config, _db, new AzdoClientFactory((org, proj) => throw new NotImplementedException()));
    }

    private static AzdoBuild MakeBuild(int id, DateTime? finishTime = null) => new()
    {
        Id = id,
        BuildNumber = $"2025.{id}",
        Status = "completed",
        Result = "failed",
        Uri = "",
        SourceBranch = "refs/heads/main",
        DefinitionName = "test-def",
        FinishTime = finishTime,
    };
}
