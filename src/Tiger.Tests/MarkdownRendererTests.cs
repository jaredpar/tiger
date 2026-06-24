using Spectre.Console;
using Xunit;

namespace Tiger.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Headers_RenderedWithFormatting()
    {
        var md = """
            # Top Header
            ## Second Header
            ### Third Header
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Contains(lines, l => l.Contains("[bold blue underline]") && l.Contains("Top Header"));
        Assert.Contains(lines, l => l.Contains("[bold underline]") && l.Contains("Second Header"));
        Assert.Contains(lines, l => l.Contains("[bold]") && l.Contains("Third Header"));
    }

    [Fact]
    public void BulletPoints_RenderedWithMarkers()
    {
        var md = """
            - First item
            - Second item
              - Nested item
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Contains(lines, l => l.Contains("[blue]•[/]") && l.Contains("First item"));
        Assert.Contains(lines, l => l.Contains("[blue]•[/]") && l.Contains("Second item"));
        Assert.Contains(lines, l => l.Contains("[dim]•[/]") && l.Contains("Nested item"));
    }

    [Fact]
    public void CodeBlock_RenderedWithBorders()
    {
        var md = """
            ```
            var x = 42;
            ```
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Equal("[dim]┌────────────────────────────────────────[/]", lines[0]);
        Assert.Contains(lines, l => l.Contains("[grey]") && l.Contains("var x = 42;"));
        Assert.Equal("[dim]└────────────────────────────────────────[/]", lines[2]);
    }

    [Fact]
    public void HorizontalRule_RenderedAsDimLine()
    {
        var lines = MarkdownRenderer.ToMarkupLines("---");

        Assert.Single(lines);
        Assert.Equal("[dim]────────────────────────────────────────[/]", lines[0]);
    }

    [Fact]
    public void InlineBold_Formatted()
    {
        var result = MarkdownRenderer.FormatInlineMarkup("This is **bold** text");

        Assert.Contains("[bold]bold[/]", result);
        Assert.Contains("This is", result);
        Assert.Contains("text", result);
    }

    [Fact]
    public void InlineItalic_Formatted()
    {
        var result = MarkdownRenderer.FormatInlineMarkup("This is *italic* text");

        Assert.Contains("[italic]italic[/]", result);
    }

    [Fact]
    public void InlineCode_Formatted()
    {
        var result = MarkdownRenderer.FormatInlineMarkup("Use `dotnet build` to compile");

        Assert.Contains("[grey]dotnet build[/]", result);
    }

    [Fact]
    public void SpecialCharacters_Escaped()
    {
        var md = "- Item with [brackets] and more";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        // Brackets should be escaped so Spectre doesn't interpret them as tags
        Assert.Contains(lines, l => l.Contains("[[brackets]]"));
    }

    [Fact]
    public void EmptyLines_PreservedAsBlank()
    {
        var md = "Line one\n\nLine two";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Equal(3, lines.Count);
        Assert.Equal("", lines[1]);
    }

    [Fact]
    public void RegularText_IndentedWithInlineFormatting()
    {
        var md = "Just some regular text";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Single(lines);
        Assert.StartsWith("  ", lines[0]);
        Assert.Contains("Just some regular text", lines[0]);
    }

    [Fact]
    public void CodeBlock_EscapesMarkupCharacters()
    {
        var md = """
            ```
            if (x[0] > 0) { }
            ```
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);

        // The brackets inside code should be escaped
        var codeLine = lines[1];
        Assert.Contains("[[0]]", codeLine);
    }

    [Fact]
    public void BoldAndItalicTogether()
    {
        var result = MarkdownRenderer.FormatInlineMarkup("**bold** and *italic*");

        Assert.Contains("[bold]bold[/]", result);
        Assert.Contains("[italic]italic[/]", result);
    }

    [Fact]
    public void InlineLink_RenderedAsSpectreLink()
    {
        var result = MarkdownRenderer.FormatInlineMarkup(
            "See [#40006](https://github.com/dotnet/sdk/issues/40006) for details");

        Assert.Contains("[link=https://github.com/dotnet/sdk/issues/40006]", result);
        Assert.Contains("[blue underline]#40006[/]", result);
        Assert.Contains("for details", result);
    }

    [Fact]
    public void InlineLink_MultipleLinks()
    {
        var result = MarkdownRenderer.FormatInlineMarkup(
            "[A](https://a.com) and [B](https://b.com)");

        Assert.Contains("[link=https://a.com]", result);
        Assert.Contains("[link=https://b.com]", result);
        Assert.Contains("[blue underline]A[/]", result);
        Assert.Contains("[blue underline]B[/]", result);
    }

    [Fact]
    public void InlineLink_WithBoldText()
    {
        var result = MarkdownRenderer.FormatInlineMarkup(
            "**Error** in [build](https://dev.azure.com/build/123)");

        Assert.Contains("[bold]Error[/]", result);
        Assert.Contains("[link=https://dev.azure.com/build/123]", result);
    }

    [Fact]
    public void AsteriskBullet_TreatedSameAsDash()
    {
        var md = "* Asterisk bullet";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Contains(lines, l => l.Contains("[blue]•[/]") && l.Contains("Asterisk bullet"));
    }

    [Fact]
    public void IsTableRow_ValidRows()
    {
        Assert.True(MarkdownRenderer.IsTableRow("| A | B |"));
        Assert.True(MarkdownRenderer.IsTableRow("| A | B | C |"));
        Assert.True(MarkdownRenderer.IsTableRow("|A|B|"));
    }

    [Fact]
    public void IsTableRow_InvalidRows()
    {
        Assert.False(MarkdownRenderer.IsTableRow("not a table"));
        Assert.False(MarkdownRenderer.IsTableRow("| only one pipe"));
        Assert.False(MarkdownRenderer.IsTableRow("no pipes here"));
    }

    [Fact]
    public void IsTableSeparator_ValidSeparators()
    {
        Assert.True(MarkdownRenderer.IsTableSeparator("|---|---|"));
        Assert.True(MarkdownRenderer.IsTableSeparator("| --- | --- |"));
        Assert.True(MarkdownRenderer.IsTableSeparator("|:---|---:|"));
        Assert.True(MarkdownRenderer.IsTableSeparator("|:---:|:---:|"));
    }

    [Fact]
    public void IsTableSeparator_InvalidSeparators()
    {
        Assert.False(MarkdownRenderer.IsTableSeparator("| A | B |"));
        Assert.False(MarkdownRenderer.IsTableSeparator("not a separator"));
        Assert.False(MarkdownRenderer.IsTableSeparator("| text | --- |"));
    }

    [Fact]
    public void SplitTableRow_SplitsCells()
    {
        var cells = MarkdownRenderer.SplitTableRow("| A | B | C |");

        Assert.Equal(3, cells.Length);
        Assert.Equal(" A ", cells[0]);
        Assert.Equal(" B ", cells[1]);
        Assert.Equal(" C ", cells[2]);
    }

    [Fact]
    public void ParseTable_BasicTable()
    {
        var lines = new[]
        {
            "| Metric | Value |",
            "|--------|-------|",
            "| Success rate | 50% |",
            "| Total | 10 |",
        };

        var (headers, rows, nextIndex) = MarkdownRenderer.ParseTable(lines, 0);

        Assert.Equal(2, headers.Length);
        Assert.Equal("Metric", headers[0]);
        Assert.Equal("Value", headers[1]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Success rate", rows[0][0]);
        Assert.Equal("50%", rows[0][1]);
        Assert.Equal("Total", rows[1][0]);
        Assert.Equal("10", rows[1][1]);
        Assert.Equal(4, nextIndex);
    }

    [Fact]
    public void ParseTable_StopsAtNonTableRow()
    {
        var lines = new[]
        {
            "| A | B |",
            "|---|---|",
            "| 1 | 2 |",
            "",
            "Some other text",
        };

        var (headers, rows, nextIndex) = MarkdownRenderer.ParseTable(lines, 0);

        Assert.Equal(2, headers.Length);
        Assert.Single(rows);
        Assert.Equal(3, nextIndex);
    }

    [Fact]
    public void ParseTable_FewerCellsThanHeaders()
    {
        var lines = new[]
        {
            "| A | B | C |",
            "|---|---|---|",
            "| 1 | 2 |",
        };

        var (headers, rows, nextIndex) = MarkdownRenderer.ParseTable(lines, 0);

        Assert.Equal(3, headers.Length);
        Assert.Single(rows);
        Assert.Equal("1", rows[0][0]);
        Assert.Equal("2", rows[0][1]);
        Assert.Equal("", rows[0][2]);
    }

    [Fact]
    public void ParseTable_InlineMarkdownInCells()
    {
        var lines = new[]
        {
            "| Name | Status |",
            "|------|--------|",
            "| **bold** | `code` |",
        };

        var (_, rows, _) = MarkdownRenderer.ParseTable(lines, 0);

        // ParseTable returns raw text (no markup transformation)
        Assert.Equal("**bold**", rows[0][0]);
        Assert.Equal("`code`", rows[0][1]);
    }

    [Fact]
    public void ParseTable_StartingAtOffset()
    {
        var lines = new[]
        {
            "Some preamble",
            "| X | Y |",
            "|---|---|",
            "| 1 | 2 |",
        };

        var (headers, rows, nextIndex) = MarkdownRenderer.ParseTable(lines, 1);

        Assert.Equal(2, headers.Length);
        Assert.Equal("X", headers[0]);
        Assert.Single(rows);
        Assert.Equal(4, nextIndex);
    }

    [Fact]
    public void HeaderWithLink_RendersAsClickableLink()
    {
        var md = "### [dotnet/roslyn#83775](https://github.com/dotnet/roslyn/issues/83775): Razor AddImport";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Single(lines);
        Assert.Contains("[link=https://github.com/dotnet/roslyn/issues/83775]", lines[0]);
        Assert.Contains("[blue underline]dotnet/roslyn#83775[/]", lines[0]);
        Assert.Contains("Razor AddImport", lines[0]);
    }

    [Fact]
    public void KnownIssueSummaryLine_RendersLinkInRegularText()
    {
        var md = "Matches known issue(s): [dotnet/roslyn#83775](https://github.com/dotnet/roslyn/issues/83775): Razor AddImport";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Single(lines);
        Assert.Contains("[link=https://github.com/dotnet/roslyn/issues/83775]", lines[0]);
        Assert.Contains("[blue underline]dotnet/roslyn#83775[/]", lines[0]);
    }

    [Fact]
    public void CodeBlock_PreservedAcrossMultipleLines()
    {
        var md = "Before\n```json\n{\"key\": \"value\"}\n```\nAfter";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        // Should have: "Before", opening fence, JSON content, closing fence, "After"
        Assert.Contains(lines, l => l.Contains("Before"));
        Assert.Contains(lines, l => l == "[dim]┌────────────────────────────────────────[/]");
        Assert.Contains(lines, l => l.Contains("[grey]") && l.Contains("key"));
        Assert.Contains(lines, l => l == "[dim]└────────────────────────────────────────[/]");
        Assert.Contains(lines, l => l.Contains("After"));
    }

    [Fact]
    public void FormatInlineMarkup_PreservesBackslashes()
    {
        var input = """C:\h\w\AF600976\w\B51709B0\e\bincore""";
        var result = MarkdownRenderer.FormatInlineMarkup(input);
        Assert.Equal("""C:\h\w\AF600976\w\B51709B0\e\bincore""", result);
    }

    [Fact]
    public void ToMarkupLines_PreservesBackslashesInCodeBlock()
    {
        var md = "```\nC:\\h\\w\\AF600976\\e\\bincore\n```";
        var lines = MarkdownRenderer.ToMarkupLines(md);
        var expected = """
            [dim]┌────────────────────────────────────────[/]
            [dim]│[/] [grey]C:\h\w\AF600976\e\bincore[/]
            [dim]└────────────────────────────────────────[/]
            """;
        Assert.Equal(expected, string.Join(Environment.NewLine, lines), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void RenderToLines_CodeBlockStateTrackedAcrossLines()
    {
        // This tests the fix for the Render code block state tracking bug.
        // Error messages inside code blocks must be rendered with Markup.Escape
        // (the [grey] code-block style), not through FormatInlineMarkup.
        var markdown = "## Error\n\n```\nC:\\h\\w\\AF600976\\e\\bincore\n```";
        var lines = MarkdownRenderer.RenderToLines(markdown);
        var expected = """
            [bold underline]Error[/]

            [dim]┌────────────────────────────────────────[/]
            [dim]│[/] [grey]C:\h\w\AF600976\e\bincore[/]
            [dim]└────────────────────────────────────────[/]
            """;
        Assert.Equal(expected, string.Join(Environment.NewLine, lines), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void RenderToLines_CodeBlockEndsCorrectly()
    {
        var markdown = "before\n```\ncode line\n```\nafter";
        var lines = MarkdownRenderer.RenderToLines(markdown);
        var expected = """
              before
            [dim]┌────────────────────────────────────────[/]
            [dim]│[/] [grey]code line[/]
            [dim]└────────────────────────────────────────[/]
              after
            """;
        Assert.Equal(expected, string.Join(Environment.NewLine, lines), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void Blockquote_RenderedWithBarAndItalic()
    {
        var md = "> Generated: 2026-06-22 14:15 | Window: 3 days";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Single(lines);
        Assert.Equal("  [dim]│[/] [italic]Generated: 2026-06-22 14:15 | Window: 3 days[/]", lines[0]);
    }

    [Fact]
    public void NumberedList_RenderedWithBlueNumbers()
    {
        var md = "1. First item\n2. Second item with **bold**";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        var expected = """
            [blue]1.[/] First item
            [blue]2.[/] Second item with [bold]bold[/]
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n").Trim(),
            string.Join("\n", lines.Select(l => l.TrimStart())));
    }

    [Fact]
    public void Strikethrough_RenderedWithStrikethroughMarkup()
    {
        var result = MarkdownRenderer.FormatInlineMarkup("~~resolved issue~~");

        Assert.Equal("[strikethrough]resolved issue[/]", result);
    }

    [Fact]
    public void HealthReport_RendersAllElements()
    {
        // Real-world health report content from a health agent run.
        // Uses RenderToLines which properly tracks code blocks and skips tables.
        var md = """
            # Health Report — dotnet/roslyn / roslyn-CI

            > Generated: 2026-06-22 2:14 PM PT | Window: 3 days

            **Overall Health: 🟡 YELLOW**

            **CI pass rate (main):** 3 succeeded / 5 failed in last 3 days.

            ### Active Problems

            | Problem | Since | Severity | Trend |
            |---------|-------|----------|-------|
            | Helix work item crashes | ~10+ days ago | 🟡 Medium | **Chronic** — infrastructure issue |
            | `Fuzz_2` test host crash | Jun 22 | 🟠 Watch | **New** — first occurrence |

            ### Known Issue Correlations
            - None of the 6 active known issues matched failures in this window.

            ### Recently Resolved
            - ~~Main branch failure streak (Jun 19–22)~~: Partially resolved.

            ### Recommended Actions
            1. **Monitor `Fuzz_2` crash**: If it recurs, investigate the dump file.
            2. **Investigate recurring 120-min timeout** on `Test_Windows_CoreClr_Debug_Single_Machine`.

            ### Trends
            - **Unstable YELLOW**: Main branch alternates between green and infrastructure-caused failures.
            - **Upgrade to GREEN** if: Next 2+ main builds pass without timeout/crash issues.
            """;

        var lines = MarkdownRenderer.RenderToLines(md);
        var actual = string.Join("\n", lines);

        var expected = """
            [bold blue underline]Health Report — dotnet/roslyn / roslyn-CI[/]

              [dim]│[/] [italic]Generated: 2026-06-22 2:14 PM PT | Window: 3 days[/]

              [bold]Overall Health: 🟡 YELLOW[/]

              [bold]CI pass rate (main):[/] 3 succeeded / 5 failed in last 3 days.

            [bold]Active Problems[/]


            [bold]Known Issue Correlations[/]
              [blue]•[/] None of the 6 active known issues matched failures in this window.

            [bold]Recently Resolved[/]
              [blue]•[/] [strikethrough]Main branch failure streak (Jun 19–22)[/]: Partially resolved.

            [bold]Recommended Actions[/]
              [blue]1.[/] [bold]Monitor [grey]Fuzz_2[/] crash[/]: If it recurs, investigate the dump file.
              [blue]2.[/] [bold]Investigate recurring 120-min timeout[/] on [grey]Test_Windows_CoreClr_Debug_Single_Machine[/].

            [bold]Trends[/]
              [blue]•[/] [bold]Unstable YELLOW[/]: Main branch alternates between green and infrastructure-caused failures.
              [blue]•[/] [bold]Upgrade to GREEN[/] if: Next 2+ main builds pass without timeout/crash issues.
            """;
        Assert.Equal(
            expected.ReplaceLineEndings("\n").Trim(),
            actual.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void ToMarkupLines_RendersTableAsText()
    {
        var md = """
            | Problem | Severity | Trend |
            |---------|----------|-------|
            | Helix crashes | Medium | Chronic |
            | Test timeout | High | New |
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);
        var actual = string.Join("\n", lines);

        var expected = """
              [bold]Problem      [/] [dim]│[/] [bold]Severity[/] [dim]│[/] [bold]Trend  [/]
              [dim]──────────────┼──────────┼────────[/]
              Helix crashes [dim]│[/] Medium   [dim]│[/] Chronic
              Test timeout  [dim]│[/] High     [dim]│[/] New    
            """;
        Assert.Equal(
            expected.ReplaceLineEndings("\n").Trim(),
            actual.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void RenderTableAsText_FormatsColumnsCorrectly()
    {
        var headers = new[] { "Name", "Status" };
        var rows = new List<string[]>
        {
            new[] { "Alpha", "OK" },
            new[] { "Beta release", "Failed" },
        };

        var lines = MarkdownRenderer.RenderTableAsText(headers, rows);
        var actual = string.Join("\n", lines);

        var expected = """
              [bold]Name        [/] [dim]│[/] [bold]Status[/]
              [dim]─────────────┼───────[/]
              Alpha        [dim]│[/] OK    
              Beta release [dim]│[/] Failed
            """;
        Assert.Equal(
            expected.ReplaceLineEndings("\n").Trim(),
            actual.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void ToMarkupLines_ConstrainsTableToMaxWidth()
    {
        // Real-world table that's much wider than a typical terminal
        var md = """
            | Problem | Since | Severity | Trend |
            |---------|-------|----------|-------|
            | Test_Windows_CoreClr_Debug_Single_Machine 120-min timeout | Jun 20 | High | Persistent — 5 hits in 3 days |
            | Razor AddImport code action tests (12 tests) | Jun 22 | Medium | Active — tracked by #83775 |
            """;

        var constrained = MarkdownRenderer.ToMarkupLines(md, 80);
        var actual = string.Join("\n", constrained);

        var expected = """
              [bold]Problem                             [/] [dim]│[/] [bold]Since [/] [dim]│[/] [bold]Severity[/] [dim]│[/] [bold]Trend              [/]
              [dim]─────────────────────────────────────┼────────┼──────────┼────────────────────[/]
              Test_Windows_CoreClr_Debug_Single_Ma [dim]│[/] Jun 20 [dim]│[/] High     [dim]│[/] Persistent — 5     
              chine 120-min timeout                [dim]│[/]        [dim]│[/]          [dim]│[/] hits in 3 days     
              Razor AddImport code action tests    [dim]│[/] Jun 22 [dim]│[/] Medium   [dim]│[/] Active — tracked   
              (12 tests)                           [dim]│[/]        [dim]│[/]          [dim]│[/] by #83775          
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void RenderTableAsText_WordWrapsWhenConstrained()
    {
        var headers = new[] { "Name", "Description" };
        var rows = new List<string[]>
        {
            new[] { "Alpha", "A very long description that should be truncated to fit the width" },
        };

        // maxWidth = 40: available for columns = 40 - 2 (indent) - 3 (separator) = 35
        // "Name" (natural=5) is ≤12 so stays at 5; "Description" gets remainder = 30
        var lines = MarkdownRenderer.RenderTableAsText(headers, rows, 40);
        var actual = string.Join("\n", lines);

        var expected = """
              [bold]Name [/] [dim]│[/] [bold]Description                   [/]
              [dim]──────┼───────────────────────────────[/]
              Alpha [dim]│[/] A very long description that  
                    [dim]│[/] should be truncated to fit    
                    [dim]│[/] the width                     
            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void Emoji_PreservedInBoldText()
    {
        // Emoji characters like 🟡 must pass through to ANSI output unchanged.
        // The MarkupToAnsi helper renders via Spectre to capture real ANSI codes.
        var md = "**Overall Health: 🟡 YELLOW**";
        var lines = MarkdownRenderer.ToMarkupLines(md);

        var actual = string.Join("\n", lines);
        // Bold inline: "  [bold]Overall Health: 🟡 YELLOW[/]"
        Assert.Equal("  [bold]Overall Health: 🟡 YELLOW[/]", actual);

        // Verify the emoji survives ANSI rendering
        var ansi = RenderMarkupToAnsi(actual.Trim());
        Assert.Equal("\x1b[1mOverall Health: 🟡 YELLOW\x1b[0m", ansi);
    }

    [Fact]
    public void Emoji_PreservedInPlainText()
    {
        var md = "Status: 🟢 GREEN — all passing";
        var lines = MarkdownRenderer.ToMarkupLines(md);

        var actual = string.Join("\n", lines);
        Assert.Equal("  Status: 🟢 GREEN — all passing", actual);

        var ansi = RenderMarkupToAnsi(actual.Trim());
        Assert.Equal("Status: 🟢 GREEN — all passing", ansi);
    }

    [Fact]
    public void Emoji_PreservedInTableCells()
    {
        var md = """
            | Status | Meaning |
            |--------|---------|
            | 🟢 | Passing |
            | 🟡 | Degraded |
            | 🔴 | Failing |
            """;

        var lines = MarkdownRenderer.ToMarkupLines(md);
        var actual = string.Join("\n", lines);

        // Emoji in cells are preserved after inline formatting
        var expected = """
              [bold]Status[/] [dim]│[/] [bold]Meaning [/]
              [dim]───────┼─────────[/]
              🟢     [dim]│[/] Passing 
              🟡     [dim]│[/] Degraded
              🔴     [dim]│[/] Failing 
            """;
        Assert.Equal(
            expected.ReplaceLineEndings("\n").Trim(),
            actual.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void Emoji_PreservedInBulletPoints()
    {
        var md = "- 🟡 Medium severity issue";
        var lines = MarkdownRenderer.ToMarkupLines(md);

        var actual = string.Join("\n", lines);
        Assert.Equal("  [blue]•[/] 🟡 Medium severity issue", actual);

        var ansi = RenderMarkupToAnsi(actual.Trim());
        Assert.Equal("\x1b[38;5;12m•\x1b[0m 🟡 Medium severity issue", ansi);
    }

    /// <summary>
    /// Renders a Spectre markup string to raw ANSI escape sequences.
    /// </summary>
    private static string RenderMarkupToAnsi(string markupText)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
        });
        return console.ToAnsi(new Markup(markupText));
    }

    [Fact]
    public void FormatInlineMarkup_LinksWithQueryStrings_ProduceValidMarkup()
    {
        // Real-world line from health reports with links containing query strings
        var line = "5 succeeded / 10 failed. Latest 2 builds ([1477359](https://dev.azure.com/dnceng-public/public/_build/results?buildId=1477359), [1477299](https://dev.azure.com/dnceng-public/public/_build/results?buildId=1477299)) both **succeeded**.";

        var result = MarkdownRenderer.FormatInlineMarkup(line);

        // Must not throw when rendered by Spectre
        var markup = new Markup($"  {result}");
        Assert.NotNull(markup);
    }

    [Fact]
    public void ToMarkupLines_RealHealthReport_AllLinesAreValidMarkup()
    {
        // Content from a real health report — exercises links in table cells,
        // backtick-quoted identifiers, bold text, and emoji
        var md = """
            > Generated: 2026-06-23 08:48 | Window: 3 days

            **Overall Health: 🟡 YELLOW**

            **CI pass rate (main):** 5 succeeded / 10 failed since Jun 20. Latest 2 builds ([1477359](https://dev.azure.com/dnceng-public/public/_build/results?buildId=1477359), [1477299](https://dev.azure.com/dnceng-public/public/_build/results?buildId=1477299)) both **succeeded** (Jun 23 ~8 AM PT).

            ### Active Problems

            | Problem | Since | Severity | Trend |
            |---------|-------|----------|-------|
            | `Test_Windows_CoreClr_Debug_Single_Machine` 120-min timeout | Jun 19 | 🔴 High | **Persistent** — 4 hits across 4 days on different NetCore-Public agents; systemic queue capacity issue |
            | `Test_Linux_Debug` Helix work item crashes | Jun 22 | 🔴 High | **Active** — 5 CI builds affected; test crashes (exit code 1) in multiple work items |
            | Razor AddImport code action tests (12 tests) | Jun 22 | 🟡 Medium | **Active** — tracked by [#83775](https://github.com/dotnet/roslyn/issues/83775), only on Linux legs |
            | "Helix completed without specifying a job id" | Jun 23 | 🟡 Low | **New** — single PR occurrence, monitoring |

            ### Known Issue Correlations
            - **#83775** actively matching Razor AddImport failures (2 CI + 1 PR build)
            - **#83972** (Pool leak) — matched 1 occurrence in CI (workitem_0 in build 1476004)
            - No other known issues triggered

            ### Recommended Actions
            1. **🔴 File Known Build Error for timeout**: Pattern `ran longer than the maximum time of 120 minutes` with `BuildRetry: true`
            2. **🔴 Investigate Linux Helix work item crashes**: 5 CI builds failed due to work item crashes on Linux
            3. **🟡 Razor AddImport**: Tracked by #83775 — no additional action needed
            """;

        // Test at multiple widths to catch wrapping issues
        foreach (var width in new[] { 80, 100, 116, 150, 200 })
        {
            var lines = MarkdownRenderer.ToMarkupLines(md, width);

            for (var i = 0; i < lines.Count; i++)
            {
                try
                {
                    _ = new Markup(lines[i]);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Width={width}, Line {i} produced invalid markup: {ex.Message}\nLine: {lines[i]}");
                }
            }

            // Also test wrapping at various panel widths
            foreach (var wrapWidth in new[] { 70, 100, 116, 150 })
            {
                foreach (var line in lines)
                {
                    var wrapped = Tiger.Commands.PanelRenderer.WrapMarkupLine(line, wrapWidth);
                    for (var j = 0; j < wrapped.Count; j++)
                    {
                        try
                        {
                            _ = new Markup(wrapped[j]);
                        }
                        catch (Exception ex)
                        {
                            Assert.Fail($"Width={width}, WrapWidth={wrapWidth}: invalid markup: {ex.Message}\nOriginal: {line}\nWrapped[{j}]: {wrapped[j]}");
                        }
                    }
                }
            }
        }
    }
}
