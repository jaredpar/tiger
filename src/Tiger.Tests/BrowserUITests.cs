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
        var console = new TestConsole().Width(120).Height(24);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=42";

        var lines = ui.CaptureContent(() =>
        {
            BuildBrowser.RenderBuildDetailHeader(
                ui,
                buildId: 42, defName: "runtime-CI", buildNumber: "20250601.1", result: "failed",
                branch: "refs/heads/main", prNumber: null, repoName: null,
                prAuthor: null, prTitle: null,
                finishTime: "2025-06-01T12:00:00Z", url: url);
            ui.RenderEmptyLine();
            BuildBrowser.RenderBuildDetailSections(
                ui,
                failedJobs: ["Build_Release", "Test_Windows"],
                failedTests:
                [
                    ("Windows x64", "MyNamespace.MyTest.TestMethod1", "Assert.Equal failed"),
                    ("Windows x64", "MyNamespace.MyTest.TestMethod2", ""),
                ]);
        });

        var expected = $"""
[bold]Build:[/] #42 — runtime-CI 20250601.1
[bold]Result:[/] [red]X failed[/]
[bold]Branch:[/] main
[bold]Finished:[/] {BrowserUI.FormatTime("2025-06-01T12:00:00Z")}
[bold]URL:[/] [link={url}][blue underline]{url}[/][/]

[bold underline]Timeline[/]
  [red]X[/] Build_Release
  [red]X[/] Test_Windows

[bold underline]Tests[/]
  [bold yellow]Windows x64[/]
    [red]X[/] MyNamespace.MyTest.TestMethod1
      [dim]Assert.Equal failed[/]
    [red]X[/] MyNamespace.MyTest.TestMethod2
""";

        AssertLines(expected, lines);
    }

    [Fact]
    public void RenderBuildDetail_PrBuild_DataNotAvailable()
    {
        var console = new TestConsole().Width(120).Height(24);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=100";

        var lines = ui.CaptureContent(() =>
        {
            BuildBrowser.RenderBuildDetailHeader(
                ui,
                buildId: 100, defName: "aspnetcore-CI", buildNumber: "20250601.2", result: "succeeded",
                branch: "refs/pull/999/merge", prNumber: 999, repoName: "dotnet/aspnetcore",
                prAuthor: "jaredpar", prTitle: "Fix all the things",
                finishTime: null, url: url);
            ui.RenderEmptyLine();
            BuildBrowser.RenderBuildDetailSections(
                ui,
                failedJobs: null,
                failedTests: null);
        });

        var prUrl = "https://github.com/dotnet/aspnetcore/pull/999";
        var expected = $"""
[bold]Build:[/] #100 — aspnetcore-CI 20250601.2
[bold]Result:[/] [green]+ succeeded[/]
[bold]PR:[/] #999 [blue]jaredpar[/] Fix all the things
[bold]PR URL:[/] [link={prUrl}][blue underline]PR #999[/][/]
[bold]URL:[/] [link={url}][blue underline]{url}[/][/]

[bold underline]Timeline[/]
  [yellow]Timeline not available yet[/]

[bold underline]Tests[/]
  [yellow]Tests not available yet[/]
""";

        AssertLines(expected, lines);
    }

    [Fact]
    public void RenderBuildDetail_AllTestsPassed_NoFailedJobs()
    {
        var console = new TestConsole().Width(120).Height(24);
        var ui = new PanelRenderer(console);
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=200";

        var lines = ui.CaptureContent(() =>
        {
            BuildBrowser.RenderBuildDetailHeader(
                ui,
                buildId: 200, defName: "sdk-CI", buildNumber: "20250601.3", result: "succeeded",
                branch: "refs/heads/release/9.0", prNumber: null, repoName: null,
                prAuthor: null, prTitle: null,
                finishTime: "2025-06-01T15:30:00Z", url: url);
            ui.RenderEmptyLine();
            BuildBrowser.RenderBuildDetailSections(
                ui,
                failedJobs: [],
                failedTests: []);
        });

        var expected = $"""
[bold]Build:[/] #200 — sdk-CI 20250601.3
[bold]Result:[/] [green]+ succeeded[/]
[bold]Branch:[/] release/9.0
[bold]Finished:[/] {BrowserUI.FormatTime("2025-06-01T15:30:00Z")}
[bold]URL:[/] [link={url}][blue underline]{url}[/][/]

[bold underline]Timeline[/]
  [green]No failed jobs[/]

[bold underline]Tests[/]
  [green]All tests passed[/]
""";

        AssertLines(expected, lines);
    }

    private static void AssertLines(string expected, List<string> actual)
    {
        var actualText = string.Join(Environment.NewLine, actual);
        var expectedText = expected.TrimStart(Environment.NewLine.ToCharArray()).TrimEnd();
        Assert.Equal(expectedText, actualText);
    }
}
