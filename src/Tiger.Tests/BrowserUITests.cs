using Spectre.Console.Testing;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class BrowserUITests
{
    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/heads/release/8.0", "release/8.0")]
    [InlineData("refs/pull/12345/merge", "12345/merge")]
    [InlineData("main", "main")]
    [InlineData("", "")]
    public void SimplifyBranch_StripsPrefixes(string input, string expected)
    {
        Assert.Equal(expected, BrowserUI.SimplifyBranch(input));
    }

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/pull/99/merge", "99/merge")]
    [InlineData("main", "main")]
    public void FormatBranchField_ReturnsSimplifiedEscapedBranch(string input, string expected)
    {
        var result = BuildBrowser.FormatBranchField(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatBuildListItem_UsesSimplifiedBranch()
    {
        var line = BuildBrowser.FormatBuildListItem(
            buildId: 42,
            definitionName: "runtime-CI",
            result: "succeeded",
            branch: "refs/heads/main",
            prNumber: null,
            finishTime: null,
            ingestionStatus: "complete");

        var expected = """[green]+[/] 42 runtime-CI [dim]main[/] —""";
        Assert.Equal(expected, line);
    }

    [Fact]
    public void FormatBuildListItem_PullRequestBranch_UsesSimplifiedBranch()
    {
        var line = BuildBrowser.FormatBuildListItem(
            buildId: 100,
            definitionName: "aspnetcore-CI",
            result: "failed",
            branch: "refs/pull/5678/merge",
            prNumber: 5678,
            finishTime: null,
            ingestionStatus: "complete");

        var expected = """[red]X[/] 100 aspnetcore-CI [dim]5678/merge[/] — PR#5678""";
        Assert.Equal(expected, line);
    }

    [Fact]
    public void RenderBuildDetail_BranchBuild_AllDataAvailable()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(30);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=42";
        var finished = BrowserUI.FormatTime("2025-06-01T12:00:00Z");
        var lines = new List<string>();

        lines.AddRange(BuildBrowser.BuildDetailHeaderLines(
            buildId: 42, defName: "runtime-CI", buildNumber: "20250601.1", result: "failed",
            branch: "refs/heads/main", prNumber: null, repoName: null,
            prAuthor: null, prTitle: null,
            finishTime: "2025-06-01T12:00:00Z", url: url));
        lines.Add("");
        lines.AddRange(BuildBrowser.BuildDetailSectionLines(
            failedJobs: ["Build_Release", "Test_Windows"],
            failedTests:
            [
                ("Windows x64", "MyNamespace.MyTest.TestMethod1", "Assert.Equal failed"),
                ("Windows x64", "MyNamespace.MyTest.TestMethod2", ""),
            ]));

        ui.RenderDetailPanel(["Builds", "#42"], null, lines, "[blue]Esc[/] Back");

        var actual = PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        // Use FormatTime to get timezone-independent expected value; pad to 66 = RenderContentWidth(76) - "Finished: "(10)
        var finishedPadded = finished.PadRight(66);
        var expected = $"""
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > #42                                                         [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold]Build:[/] #42 — runtime-CI 20250601.1                                           [dim]║[/]
            [dim]║[/] [bold]Result:[/] [red]X failed[/]                                                             [dim]║[/]
            [dim]║[/] [bold]Branch:[/] main                                                                 [dim]║[/]
            [dim]║[/] [bold]Finished:[/] {finishedPadded} [dim]║[/]
            [dim]║[/] [bold]URL:[/] [link=https://dev.azure.com/dnceng/public/_build/results?buildId=42][blue underline]https://dev.azure.com/dnceng/public/_build/results?buildId=42[/][/]           [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Timeline[/]                                                                     [dim]║[/]
            [dim]║[/]   [red]X[/] Build_Release                                                            [dim]║[/]
            [dim]║[/]   [red]X[/] Test_Windows                                                             [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Tests[/]                                                                        [dim]║[/]
            [dim]║[/]   [bold yellow]Windows x64[/]                                                                [dim]║[/]
            [dim]║[/]     [red]X[/] MyNamespace.MyTest.TestMethod1                                         [dim]║[/]
            [dim]║[/]       [dim]Assert.Equal failed[/]                                                    [dim]║[/]
            [dim]║[/]     [red]X[/] MyNamespace.MyTest.TestMethod2                                         [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        var expectedAnsi = PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim());
        Assert.Equal(expectedAnsi, actual);
    }

    [Fact]
    public void RenderBuildDetail_PrBuild_DataNotAvailable()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(30);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=100";
        var lines = new List<string>();

        lines.AddRange(BuildBrowser.BuildDetailHeaderLines(
            buildId: 100, defName: "aspnetcore-CI", buildNumber: "20250601.2", result: "succeeded",
            branch: "refs/pull/999/merge", prNumber: 999, repoName: "dotnet/aspnetcore",
            prAuthor: "jaredpar", prTitle: "Fix all the things",
            finishTime: null, url: url));
        lines.Add("");
        lines.AddRange(BuildBrowser.BuildDetailSectionLines(
            failedJobs: null,
            failedTests: null));

        ui.RenderDetailPanel(["Builds", "#100"], null, lines, "[blue]Esc[/] Back");

        var actual = PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > #100                                                        [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold]Build:[/] #100 — aspnetcore-CI 20250601.2                                       [dim]║[/]
            [dim]║[/] [bold]Result:[/] [green]+ succeeded[/]                                                          [dim]║[/]
            [dim]║[/] [bold]PR:[/] #999 [blue]jaredpar[/] Fix all the things                                         [dim]║[/]
            [dim]║[/] [bold]PR URL:[/] [link=https://github.com/dotnet/aspnetcore/pull/999][blue underline]PR #999[/][/]                                                              [dim]║[/]
            [dim]║[/] [bold]URL:[/] [link=https://dev.azure.com/dnceng/public/_build/results?buildId=100][blue underline]https://dev.azure.com/dnceng/public/_build/results?buildId=100[/][/]          [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Timeline[/]                                                                     [dim]║[/]
            [dim]║[/]   [yellow]Timeline not available yet[/]                                                 [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Tests[/]                                                                        [dim]║[/]
            [dim]║[/]   [yellow]Tests not available yet[/]                                                    [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderBuildDetail_AllTestsPassed_NoFailedJobs()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(30);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=200";
        var finished = BrowserUI.FormatTime("2025-06-01T15:30:00Z");
        var lines = new List<string>();

        lines.AddRange(BuildBrowser.BuildDetailHeaderLines(
            buildId: 200, defName: "sdk-CI", buildNumber: "20250601.3", result: "succeeded",
            branch: "refs/heads/release/9.0", prNumber: null, repoName: null,
            prAuthor: null, prTitle: null,
            finishTime: "2025-06-01T15:30:00Z", url: url));
        lines.Add("");
        lines.AddRange(BuildBrowser.BuildDetailSectionLines(
            failedJobs: [],
            failedTests: []));

        ui.RenderDetailPanel(["Builds", "#200"], null, lines, "[blue]Esc[/] Back");

        var actual = PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        // Use FormatTime to get timezone-independent expected value; pad to 66 = RenderContentWidth(76) - "Finished: "(10)
        var finishedPadded = finished.PadRight(66);
        var expected = $"""
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > #200                                                        [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold]Build:[/] #200 — sdk-CI 20250601.3                                              [dim]║[/]
            [dim]║[/] [bold]Result:[/] [green]+ succeeded[/]                                                          [dim]║[/]
            [dim]║[/] [bold]Branch:[/] release/9.0                                                          [dim]║[/]
            [dim]║[/] [bold]Finished:[/] {finishedPadded} [dim]║[/]
            [dim]║[/] [bold]URL:[/] [link=https://dev.azure.com/dnceng/public/_build/results?buildId=200][blue underline]https://dev.azure.com/dnceng/public/_build/results?buildId=200[/][/]          [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Timeline[/]                                                                     [dim]║[/]
            [dim]║[/]   [green]No failed jobs[/]                                                             [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] [bold underline]Tests[/]                                                                        [dim]║[/]
            [dim]║[/]   [green]All tests passed[/]                                                           [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }
}
