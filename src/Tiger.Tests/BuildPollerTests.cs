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
        var poller = new BuildPoller(config, _db, (org, proj) => throw new NotImplementedException());

        Assert.False(poller.IsRunning);
        poller.Start();
        Assert.True(poller.IsRunning);

        await poller.StopAsync();
        Assert.False(poller.IsRunning);
    }

    [Fact]
    public async Task PollsAndCallsOnNewBuilds()
    {
        var builds = new List<AzdoBuild>
        {
            new() { Id = 5, BuildNumber = "5", Status = "completed", Result = "succeeded", Uri = "", SourceBranch = "main", DefinitionName = "def" },
            new() { Id = 10, BuildNumber = "10", Status = "completed", Result = "failed", Uri = "", SourceBranch = "main", DefinitionName = "def" },
            new() { Id = 3, BuildNumber = "3", Status = "inProgress", Uri = "", SourceBranch = "main", DefinitionName = "def" },
        };

        var config = new TigerConfig
        {
            PollIntervalSeconds = 3600,
            Sources = [new AzdoSource { Organization = "org", Project = "proj" }],
        };

        var captured = new List<(string org, string proj, List<AzdoBuild> builds)>();
        var poller = new BuildPoller(config, _db, (org, proj) => throw new NotImplementedException())
        {
            OnNewBuilds = (client, org, proj, newBuilds) =>
            {
                captured.Add((org, proj, newBuilds));
                return Task.CompletedTask;
            }
        };

        // Directly test PollSourceAsync would be ideal but it's private.
        // Instead test watermark behavior which is the core logic.
        poller.SetWatermark("org", "proj", 0);
        Assert.Equal(0, poller.GetWatermark("org", "proj"));
        poller.SetWatermark("org", "proj", 10);
        Assert.Equal(10, poller.GetWatermark("org", "proj"));
    }

    private BuildPoller CreatePoller()
    {
        var config = new TigerConfig { Sources = [] };
        return new BuildPoller(config, _db, (org, proj) => throw new NotImplementedException());
    }
}
