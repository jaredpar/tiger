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
    public void AsteriskBullet_TreatedSameAsDash()
    {
        var md = "* Asterisk bullet";

        var lines = MarkdownRenderer.ToMarkupLines(md);

        Assert.Contains(lines, l => l.Contains("[blue]•[/]") && l.Contains("Asterisk bullet"));
    }
}
