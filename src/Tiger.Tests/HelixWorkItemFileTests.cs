using Xunit;

namespace Tiger.Tests;

public class HelixWorkItemFileTests
{
    [Theory]
    [InlineData("console.6a500785.log", true)]
    [InlineData("console.abcdef01.log", true)]
    [InlineData("console.0000aaaa.log", true)]
    [InlineData("CONSOLE.6A500785.LOG", true)]
    [InlineData("Console.abc123.Log", true)]
    [InlineData("testhost.net472.x86_5476_20260519T011557_hangdump.dmp", false)]
    [InlineData("results.trx", false)]
    [InlineData("console.log", false)]
    [InlineData("console..log", false)]
    [InlineData("notconsole.6a500785.log", false)]
    [InlineData("console.6a500785.log.bak", false)]
    [InlineData("console.6a500785.txt", false)]
    public void IsConsoleLog_IdentifiesCorrectly(string fileName, bool expected)
    {
        var file = new HelixWorkItemFile { FileName = fileName, Uri = "https://example.com/file" };
        Assert.Equal(expected, file.IsConsoleLog);
    }
}
