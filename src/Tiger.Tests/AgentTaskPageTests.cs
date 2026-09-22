using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class AgentTaskPageTests
{
    [Theory]
    [InlineData("ClientTests.Shutdown", "ClientTests_Shutdown")]
    [InlineData("roslyn-CI #1607626", "roslyn_CI__1607626")]
    [InlineData("Investigate dependency update", "Investigate_dependency_update")]
    [InlineData("1234567890123456789012345678901234567890ignored", "1234567890123456789012345678901234567890")]
    public void GetSafeFileName_UsesGenericAgentName(string agentName, string expected)
    {
        Assert.Equal(expected, AgentTaskPage.GetSafeFileName(agentName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BuildPrompt_OptionalInstructionsOmitted_PreservesContext(string? instructions)
    {
        var context = """
            ## Diagnosis

            The **shutdown** path returned `1`.
            Preserve this final diagnosis line.
            """ + "\r\n \t\r\n";

        var expected = """
            # Build Analysis: CI #123

            ## Diagnosis

            The **shutdown** path returned `1`.
            Preserve this final diagnosis line.

            """;
        Assert.Equal(expected, AgentTaskPage.BuildPrompt("Build Analysis: CI #123", context, instructions),
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildPrompt_MultilineInstructions_FollowEntireAnalysisContext()
    {
        var context = AnalysisBrowser.BuildAgentContext(new BuildAnalysisInfo
        {
            Organization = "org",
            Project = "proj",
            BuildId = 123,
            Status = "complete",
            DefinitionName = "CI",
            BuildNumber = "123",
            SourceBranch = "refs/heads/main",
            CreatedAt = "2026-09-22",
            DiagnosisSummary = BuildAnalysisServiceTests.FullDiagnosis,
        });
        var instructions = " \t\r\nInvestigate the shutdown path.\n\n- Preserve **all** diagnostic evidence.\n  Do not skip the failing test.\r\n \t";

        var expected = """
            # Build Analysis: CI #123

            ## Diagnosis

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

            ## Instructions

            Investigate the shutdown path.

            - Preserve **all** diagnostic evidence.
              Do not skip the failing test.

            """;
        Assert.Equal(expected, AgentTaskPage.BuildPrompt("Build Analysis: CI #123", context, instructions),
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildPrompt_TestFailure_KeepsFailureContextBeforeInstructions()
    {
        var info = new BrowserUI.TestDetailInfo(
            "ClientTests.Shutdown", "org", "proj", 123, "Linux x64", 2,
            "Expected: 0\nActual: 1", "at ClientTests.Shutdown() in ClientTests.cs:line 42",
            "helix-job-123", "ClientTests");
        var context = BrowserUI.BuildTestAgentContext(info, "dotnet/runtime");

        var expected = """
            # Test Failure: ClientTests.Shutdown

            ## Failure Details

            - **Test:** `ClientTests.Shutdown`
            - **Repository:** dotnet/runtime
            - **Build:** [#123](https://dev.azure.com/org/proj/_build/results?buildId=123)
            - **Run:** Linux x64
            - **Failed in:** 2 build(s)
            - **Helix Job:** `helix-job-123`
            - **Helix Work Item:** `ClientTests`

            ## Error Message

            ```
            Expected: 0
            Actual: 1
            ```

            ## Stack Trace

            ```
            at ClientTests.Shutdown() in ClientTests.cs:line 42
            ```

            ## Instructions

            Fix the shutdown regression.
            Add a deterministic regression test.

            """;
        Assert.Equal(expected, AgentTaskPage.BuildPrompt("Test Failure: ClientTests.Shutdown", context,
            "\nFix the shutdown regression.\nAdd a deterministic regression test.\n"),
            ignoreLineEndingDifferences: true);
    }
}
