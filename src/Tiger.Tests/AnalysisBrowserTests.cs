using Spectre.Console.Testing;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class AnalysisBrowserTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\n")]
    [InlineData(true, "\r\n")]
    public void BuildAgentContext_OmitsAnalysisDetailsAndPreservesFullDiagnosis(bool legacySummary, string newLine)
    {
        var logPath = Path.GetTempFileName();
        try
        {
            var diagnosis = BuildAnalysisServiceTests.FullDiagnosis.ReplaceLineEndings(newLine);
            File.WriteAllText(logPath, $"""
                # Build Analysis
                ## Prompt
                ## Diagnosis
                Prompt instructions must not replace the actual diagnosis.
                ## Transcript
                ## Diagnosis
                {diagnosis}
                ## Category
                test-failure
                """.ReplaceLineEndings(newLine));
            var analysis = new BuildAnalysisInfo
            {
                Organization = "org name",
                Project = "proj/name",
                BuildId = 123,
                Status = "complete",
                DefinitionName = "CI [linux]",
                BuildNumber = "20260922.7",
                SourceBranch = "refs/pull/456/merge",
                Category = "test-failure",
                Confidence = "high",
                CreatedAt = "2026-09-22T10:00:00Z",
                CompletedAt = "2026-09-22T10:05:00Z",
                LogPath = logPath,
                DiagnosisSummary = legacySummary ? diagnosis[..500] + "..." : diagnosis,
            };

            var expected = """
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

                """;
            Assert.Equal(expected, AnalysisBrowser.BuildAgentContext(analysis),
                ignoreLineEndingDifferences: true);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildAgentContext_WithoutDiagnosisStartsWithDiagnosisHeading(bool missingLog)
    {
        var analysis = new BuildAnalysisInfo
        {
            Organization = "org",
            Project = "proj",
            BuildId = 123,
            Status = "pending",
            DefinitionName = "CI",
            BuildNumber = "123",
            SourceBranch = "refs/heads/main",
            CreatedAt = "2026-09-22",
            LogPath = missingLog ? Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md") : null,
        };

        var expected = """
            ## Diagnosis


            """;
        Assert.Equal(expected, AnalysisBrowser.BuildAgentContext(analysis),
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void BuildAgentContext_PreservesKnownIssueLinksAndDescriptions()
    {
        var analysis = new BuildAnalysisInfo
        {
            Organization = "org",
            Project = "proj",
            BuildId = 123,
            Status = "skipped",
            DefinitionName = "CI",
            BuildNumber = "123",
            SourceBranch = "refs/heads/main",
            CreatedAt = "2026-09-22",
            Category = "known-issue",
            DiagnosisSummary = """
                Known Build Error: [dotnet/runtime#456](https://github.com/dotnet/runtime/issues/456): **Shutdown race** in `Client.Stop`
                [dotnet/roslyn#789](https://github.com/dotnet/roslyn/issues/789): Timeout [linux]
                """,
        };

        var expected = """
            ## Known Issues

            Known Build Error: [dotnet/runtime#456](https://github.com/dotnet/runtime/issues/456): **Shutdown race** in `Client.Stop`
            [dotnet/roslyn#789](https://github.com/dotnet/roslyn/issues/789): Timeout [linux]

            """;
        Assert.Equal(expected, AnalysisBrowser.BuildAgentContext(analysis),
            ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildDetailCommands_AgentTaskIsAvailableWithoutAnalysisService(bool canAnalyze)
    {
        var commands = AnalysisBrowser.BuildDetailCommands(canAnalyze);
        var expected = canAnalyze
            ? """
                Re-run|R|1
                Force full|F|2
                View log|V|3
                Build detail|B|4
                Agent Task|A|5
                """
            : """
                View log|V|3
                Build detail|B|4
                Agent Task|A|5
                """;
        Assert.Equal(expected, string.Join(Environment.NewLine,
            commands.Select(command => $"{command.Label}|{command.Hotkey}|{command.ReturnValue}")),
            ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetailMenu_RendersAgentTaskWithAndWithoutAnalysisService(bool canAnalyze)
    {
        var console = new TestConsole().EmitAnsiSequences().Width(100).Height(11);
        var renderer = new PanelRenderer(console);
        renderer.RenderDetailPanel(["Analysis"], null, [],
            PanelRenderer.BuildCommandBarString(AnalysisBrowser.BuildDetailCommands(canAnalyze)));

        var expected = canAnalyze
            ? """
                [dim]╔══════════════════════════════════════════════════════════════════════════════════════════════════╗[/]
                [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Analysis                                                                                 [dim]║[/]
                [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
                [dim]║[/] [blue][[R]][/]e-run  [blue][[F]][/]orce full  [blue][[V]][/]iew log  [blue][[B]][/]uild detail  [blue][[A]][/]gent Task  [blue]Esc[/] Back                       [dim]║[/]
                [dim]╚══════════════════════════════════════════════════════════════════════════════════════════════════╝[/]
                """
            : """
                [dim]╔══════════════════════════════════════════════════════════════════════════════════════════════════╗[/]
                [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Analysis                                                                                 [dim]║[/]
                [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]║[/]                                                                                                  [dim]║[/]
                [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
                [dim]║[/] [blue][[V]][/]iew log  [blue][[B]][/]uild detail  [blue][[A]][/]gent Task  [blue]Esc[/] Back                                               [dim]║[/]
                [dim]╚══════════════════════════════════════════════════════════════════════════════════════════════════╝[/]
                """;
        Assert.Equal(PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n")),
            PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void Diagnosis_RendersMarkdownAndWrapsParagraphs()
    {
        var analysis = CreateAnalysis("""
            [Build #123](https://dev.azure.com/org/proj/_build/results?buildId=123) failed in `test_job` with **exit code 1**.

            The daemon-alive assertions had already passed. The failure occurs during shutdown, not when killing the other client.
            """);
        var console = new TestConsole().EmitAnsiSequences().Width(64).Height(12);
        var renderer = new PanelRenderer(console) { TruncationEnabled = false };

        renderer.RenderDetailPanel(["Analysis"], null,
            AnalysisBrowser.BuildDiagnosisLines(analysis, renderer.ContentWidth), "[blue]Esc[/] Back");

        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Analysis                                             [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold underline]Diagnosis[/]                                                    [dim]║[/]
            [dim]║[/]   [link=https://dev.azure.com/org/proj/_build/results?buildId=123][blue underline]Build #123[/][/] failed in [grey]test_job[/] with [bold]exit code 1[/].            [dim]║[/]
            [dim]║[/]                                                              [dim]║[/]
            [dim]║[/]   The daemon-alive assertions had already passed. The        [dim]║[/]
            [dim]║[/] failure occurs during shutdown, not when killing the other   [dim]║[/]
            [dim]║[/] client.                                                      [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n")),
            PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void Diagnosis_RendersEntireLongDiagnosis()
    {
        var analysis = CreateAnalysis(BuildAnalysisServiceTests.FullDiagnosis);
        var console = new TestConsole().EmitAnsiSequences().Width(64).Height(20);
        var renderer = new PanelRenderer(console) { TruncationEnabled = false };

        renderer.RenderDetailPanel(["Analysis"], null,
            AnalysisBrowser.BuildDiagnosisLines(analysis, renderer.ContentWidth), "[blue]Esc[/] Back");

        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Analysis                                             [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold underline]Diagnosis[/]                                                    [dim]║[/]
            [dim]║[/]   [link=https://dev.azure.com/org/proj/_build/results?buildId=123][blue underline]Build #123[/][/] failed.                                         [dim]║[/]
            [dim]║[/]                                                              [dim]║[/]
            [dim]║[/]   The surviving client was alive before shutdown.            [dim]║[/]
            [dim]║[/]   Killing the other client did not stop the daemon.          [dim]║[/]
            [dim]║[/]   The failure occurs while the survivor shuts down.          [dim]║[/]
            [dim]║[/]   The daemon-alive assertions had already passed.            [dim]║[/]
            [dim]║[/]   The expected exit code was zero, but it returned one.      [dim]║[/]
            [dim]║[/]   This points to the shutdown path, not termination.         [dim]║[/]
            [dim]║[/]   The shutdown race itself remains unconfirmed.              [dim]║[/]
            [dim]║[/]   Review the client logs before assigning a root cause.      [dim]║[/]
            [dim]║[/]   Preserve the distinction between fact and hypothesis.      [dim]║[/]
            [dim]║[/]   This final sentence is beyond the old summary limit.       [dim]║[/]
            [dim]║[/]                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(PanelRendererTests.MarkupToAnsi(expected.ReplaceLineEndings("\n")),
            PanelRendererTests.StripChrome(console.Output).ReplaceLineEndings("\n").Trim());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GetDiagnosisSummary_RecoversLegacySummaryFromTranscript(string newLine)
    {
        var diagnosis = BuildAnalysisServiceTests.FullDiagnosis.ReplaceLineEndings(newLine);
        var logPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(logPath, $"""
                # Build Analysis
                ## Prompt
                ## Diagnosis
                This is a prompt instruction, not the diagnosis.
                ## Transcript
                ## Diagnosis
                {diagnosis}
                ## Category
                test-failure
                """.ReplaceLineEndings(newLine));
            var analysis = CreateAnalysis(diagnosis[..500] + "...", logPath);

            Assert.Equal(BuildAnalysisServiceTests.FullDiagnosis.ReplaceLineEndings("\n"),
                AnalysisBrowser.GetDiagnosisSummary(analysis));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Theory]
    [InlineData("## Prompt\n## Diagnosis\nUnrelated diagnosis.")]
    [InlineData("## Transcript\n## Diagnosis\nUnrelated diagnosis.")]
    [InlineData("## Transcript\nNo diagnosis section.")]
    public void GetDiagnosisSummary_PreservesSummaryWhenLogCannotRecoverIt(string log)
    {
        var logPath = Path.GetTempFileName();
        var summary = BuildAnalysisServiceTests.FullDiagnosis[..500] + "...";
        try
        {
            File.WriteAllText(logPath, "# Build Analysis\n" + log);

            Assert.Equal(summary, AnalysisBrowser.GetDiagnosisSummary(CreateAnalysis(summary, logPath)));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public void GetDiagnosisSummary_PreservesSummaryWithoutLog()
    {
        var summary = BuildAnalysisServiceTests.FullDiagnosis[..500] + "...";

        Assert.Equal(summary, AnalysisBrowser.GetDiagnosisSummary(CreateAnalysis(summary)));
        Assert.Equal(summary, AnalysisBrowser.GetDiagnosisSummary(
            CreateAnalysis(summary, Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.md"))));
    }

    private static BuildAnalysisInfo CreateAnalysis(string diagnosis, string? logPath = null) => new()
    {
        Organization = "org",
        Project = "proj",
        BuildId = 123,
        Status = "complete",
        DefinitionName = "CI",
        BuildNumber = "123",
        SourceBranch = "refs/heads/main",
        CreatedAt = "2026-09-22",
        Category = "test-failure",
        DiagnosisSummary = diagnosis,
        LogPath = logPath,
    };
}
