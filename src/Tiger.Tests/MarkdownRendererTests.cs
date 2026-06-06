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
}
