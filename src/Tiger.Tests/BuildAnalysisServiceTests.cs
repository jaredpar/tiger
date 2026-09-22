using Xunit;

namespace Tiger.Tests;

public class BuildAnalysisServiceTests
{
    internal const string FullDiagnosis = """
        [Build #123](https://dev.azure.com/org/proj/_build/results?buildId=123) failed.

        The surviving client was alive before shutdown.
        Killing the other client did not stop the daemon.
        The failure occurs while the survivor shuts down.
        The daemon-alive assertions had already passed.
        The expected exit code was zero, but it returned one.
        This points to the shutdown path, not termination.
        The shutdown race itself remains unconfirmed.
        Review the client logs before assigning a root cause.
        Preserve the distinction between fact and hypothesis.
        This final sentence is beyond the old summary limit.
        """;

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\n", false)]
    public void ParseResponse_PreservesFullDiagnosis(string newLine, bool followingSection)
    {
        var response = $"""
            ## Category
            test-failure
            ## Diagnosis
            {FullDiagnosis}
            """;
        if (followingSection)
        {
            response += "\n## Confidence\nmedium";
        }

        var parsed = BuildAnalysisService.ParseResponse(response.ReplaceLineEndings(newLine));

        Assert.Equal(FullDiagnosis.ReplaceLineEndings(newLine), parsed.DiagnosisSummary);
        Assert.Equal("test-failure", parsed.Category);
        Assert.Equal(followingSection ? "medium" : null, parsed.Confidence);
    }

    [Fact]
    public void ParseResponse_MissingDiagnosis()
    {
        var parsed = BuildAnalysisService.ParseResponse("## Category\ntest-failure");

        Assert.Null(parsed.DiagnosisSummary);
    }

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
