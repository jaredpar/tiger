using Xunit;

namespace Tiger.Tests;

public class TigerConfigTests
{
    [Fact]
    public void BackfillDays_DefaultsToFourteen()
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), $"tiger-config-test-{Guid.NewGuid()}");

        var config = TigerConfig.Load(configDirectory);
        Assert.Equal(14, new TigerConfig().BackfillDays);
        Assert.Equal(14, config.BackfillDays);
    }
}
