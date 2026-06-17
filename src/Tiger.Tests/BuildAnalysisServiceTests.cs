using Xunit;

namespace Tiger.Tests;

public class BuildAnalysisServiceTests
{
    [Theory]
    [InlineData("[Known Build Error] Some issue title", "Some issue title")]
    [InlineData("[Known Build Error]Some issue title", "Some issue title")]
    [InlineData("  [Known Build Error]  Spaced title", "Spaced title")]
    [InlineData("[known build error] Case insensitive", "Case insensitive")]
    [InlineData("No prefix here", "No prefix here")]
    [InlineData("#1234: Regular title", "#1234: Regular title")]
    [InlineData("", "")]
    [InlineData("[Known Build Error] ", "")]
    public void StripKnownBuildErrorPrefix_StripsCorrectly(string input, string expected)
    {
        var result = BuildAnalysisService.StripKnownBuildErrorPrefix(input);
        Assert.Equal(expected, result);
    }
}
