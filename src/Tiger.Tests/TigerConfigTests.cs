using Xunit;

namespace Tiger.Tests;

public class TigerConfigTests
{
    [Fact]
    public void BackfillDays_DefaultsToFourteen()
    {
        using var directory = new TempDirectory();

        var config = TigerConfig.Load(directory.Path);
        Assert.Equal(14, new TigerConfig().BackfillDays);
        Assert.Equal(14, config.BackfillDays);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsDefaultWithoutPrompt()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(TigerConfig.GetConfigPath(directory.Path), "");
        var config = TigerConfig.Load(directory.Path);

        AssertDefaultConfig(config);
    }

    [Fact]
    public void Load_WhitespaceFile_ReturnsDefaultWithoutPrompt()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(TigerConfig.GetConfigPath(directory.Path), " \r\n\t");
        var config = TigerConfig.Load(directory.Path);

        AssertDefaultConfig(config);
    }

    private static void AssertDefaultConfig(TigerConfig config)
    {
        Assert.Equal(300, config.PollIntervalSeconds);
        Assert.Equal(14, config.BackfillDays);
        var source = Assert.Single(config.Sources);
        Assert.Equal("dnceng-public", source.Organization);
        Assert.Equal("public", source.Project);
        Assert.Equal("GitHub", source.RepositoryType);
        Assert.Equal("dotnet/roslyn", Assert.Single(source.Repositories));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tiger-config-test-{Guid.NewGuid()}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
