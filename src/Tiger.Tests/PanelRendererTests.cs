using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public partial class PanelRendererTests
{
    /// <summary>
    /// The exact ANSI sequence emitted by TestConsole.Clear():
    /// \x1b[2J (erase display) + \x1b[3J (erase scrollback) + \x1b[1;1H (cursor home)
    /// </summary>
    private const string ClearSequence = "\x1b[2J\x1b[3J\x1b[1;1H";

    /// <summary>
    /// Terminal chrome sequences emitted by Spectre.Console around rendered content.
    /// </summary>
    private const string CursorHide = "\x1b[?25l";
    private const string CursorShow = "\x1b[?25h";

    /// <summary>
    /// Strips all known terminal chrome (clear, cursor hide/show)
    /// from TestConsole.EmitAnsiSequences() output, leaving only content ANSI.
    /// Also normalizes OSC hyperlink IDs which are non-deterministic.
    /// </summary>
    internal static string StripChrome(string text) =>
        NormalizeLinkIds(
            text.Replace(ClearSequence, "")
                .Replace(CursorHide, "")
                .Replace(CursorShow, ""));

    /// <summary>
    /// Normalizes OSC 8 hyperlink sequences by removing the id=NNN parameter.
    /// Spectre generates non-deterministic IDs for [link] markup.
    /// Format: \x1b]8;id=123456;URL\x1b\ → \x1b]8;;URL\x1b\
    /// </summary>
    [GeneratedRegex(@"\x1b\]8;id=\d+;")]
    private static partial Regex OscLinkIdRegex();

    private static string NormalizeLinkIds(string text) =>
        OscLinkIdRegex().Replace(text, "\x1b]8;;");

    /// <summary>
    /// Converts a multi-line string containing Spectre markup to the equivalent
    /// ANSI-encoded string. Each line is independently converted. Use this to
    /// write expected panel output in readable Spectre markup form and compare
    /// against TestConsole().EmitAnsiSequences() output.
    /// </summary>
    internal static string MarkupToAnsi(string markupText)
    {
        var lines = markupText.Split('\n');
        var result = string.Join("\n", lines.Select(line =>
        {
            if (string.IsNullOrEmpty(line))
            {
                return line;
            }

            var console = new TestConsole().EmitAnsiSequences().Width(400).Height(24);
            console.Markup(line);
            return console.Output;
        }));
        return NormalizeLinkIds(result);
    }

    /// <summary>
    /// Extracts the last complete panel from accumulated output.
    /// Useful for scroll tests where console.Output contains multiple renders.
    /// </summary>
    internal static string LastPanel(string strippedOutput)
    {
        var lastBorder = strippedOutput.LastIndexOf('╔');
        if (lastBorder < 0)
        {
            return strippedOutput.Trim();
        }

        var lastEscape = strippedOutput.LastIndexOf('\x1b', lastBorder);
        var start = lastEscape >= 0 ? lastEscape : lastBorder;
        return strippedOutput[start..].Trim();
    }

    /// <summary>
    /// Asserts that every bordered line in the panel output has the same
    /// character width. This catches padding bugs where Markup.Remove().Length
    /// doesn't match the actual visible width (e.g. Unicode wide chars).
    /// </summary>
    internal static void AssertPanelAlignment(string strippedOutput)
    {
        var lines = strippedOutput.Split('\n');
        int? expectedWidth = null;
        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            // Any line containing box-drawing border characters is a panel line
            if (line.Contains('║') || line.Contains('╔') || line.Contains('╠') || line.Contains('╚'))
            {
                var rightBorder = Math.Max(
                    Math.Max(line.LastIndexOf('║'), line.LastIndexOf('╗')),
                    Math.Max(line.LastIndexOf('╣'), line.LastIndexOf('╝')));
                if (rightBorder >= 0)
                {
                    line = line[..(rightBorder + 1)];
                }

                if (expectedWidth == null)
                {
                    expectedWidth = line.Length;
                }
                else
                {
                    Assert.True(
                        expectedWidth.Value == line.Length,
                        $"Panel line {lineNumber} has width {line.Length}, expected {expectedWidth.Value}.\nLine: \"{line}\"");
                }
            }
        }

        Assert.NotNull(expectedWidth);
    }

    private static PanelRenderer CreateRenderer(int width = 80, int height = 24)
    {
        var console = new TestConsole().Width(width).Height(height);
        return new PanelRenderer(console);
    }

    // ── Content Rendering ────────────────────────────────────────────

    [Fact]
    public void RenderDetailPanel_RendersAllContentLines()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(["Test"], null, ["Line 1", "Line 2", "", "Line 3"], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailPanel_SectionTitle_RendersFormatted()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(["Test"], null, [PanelRenderer.FormatSectionTitle("My Section")], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold underline]My Section[/]                                                                   [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailPanel_Field_RendersLabelAndValue()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(["Test"], null, [PanelRenderer.FormatField("Status", "running")], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold]Status:[/] running                                                              [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailPanel_Content_AppearsInOutput()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(["Test"], null, ["stored"], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] stored                                                                       [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    // ── Layout Calculations ─────────────────────────────────────────

    [Theory]
    [InlineData(24, false, 18)]  // 24 - 3 header - 3 footer = 18
    [InlineData(24, true, 17)]   // 24 - 4 header (with context) - 3 footer = 17
    [InlineData(10, false, 5)]   // minimum is 5
    [InlineData(30, false, 24)]  // 30 - 3 - 3 = 24
    public void GetDetailAvailableHeight_CalculatesCorrectly(int terminalHeight, bool hasContext, int expected)
    {
        var renderer = CreateRenderer(width: 80, height: terminalHeight);
        Assert.Equal(expected, renderer.GetDetailAvailableHeight(hasContext));
    }

    [Fact]
    public void ContentWidth_Is_Width_Minus_4()
    {
        var renderer = CreateRenderer(width: 100, height: 24);
        Assert.Equal(96, renderer.ContentWidth);
    }

    [Fact]
    public void ContentWidth_HasMinimum40()
    {
        var renderer = CreateRenderer(width: 30, height: 24);
        Assert.Equal(40, renderer.ContentWidth);
    }

    // ── Truncation ──────────────────────────────────────────────────

    [Fact]
    public void TruncateToFit_ShortContent_Unchanged()
    {
        var renderer = CreateRenderer(width: 80);
        var result = renderer.TruncateToFit("short text");
        Assert.Equal("short text", result);
    }

    [Fact]
    public void TruncateToFit_LongContent_Truncated()
    {
        var renderer = CreateRenderer(width: 20); // ContentWidth = 40 (minimum)
        var longText = new string('x', 50);
        var result = renderer.TruncateToFit(longText);

        var plainResult = Markup.Remove(result);
        Assert.True(plainResult.Length <= 40);
        Assert.EndsWith("...", plainResult);
    }

    [Fact]
    public void TruncateToFit_WithMarkup_UsesPlainTextLength()
    {
        var renderer = CreateRenderer(width: 50); // ContentWidth = 46
        // This markup has short plain text but long markup
        var content = "[bold]short[/]";
        var result = renderer.TruncateToFit(content);
        // Plain text "short" is 5 chars, fits in 46
        Assert.Equal(content, result);
    }

    [Fact]
    public void TruncateToFit_LongMarkupContent_TruncatesBasedOnPlainText()
    {
        var renderer = CreateRenderer(width: 24); // ContentWidth = 40 (minimum)
        // Plain text will be 50 chars, exceeds 40
        var longText = $"[red]{new string('y', 50)}[/]";
        var result = renderer.TruncateToFit(longText);

        var plainResult = Markup.Remove(result);
        Assert.True(plainResult.Length <= 40);
        Assert.EndsWith("...", plainResult);
    }

    // ── Word Wrapping ───────────────────────────────────────────────

    [Fact]
    public void WrapMarkupLine_ShortLine_ReturnsUnchanged()
    {
        var result = PanelRenderer.WrapMarkupLine("hello world", 40);
        var display = string.Join(Environment.NewLine, result);
        Assert.Equal("hello world", display);
    }

    [Fact]
    public void WrapMarkupLine_ExactFit_ReturnsUnchanged()
    {
        var result = PanelRenderer.WrapMarkupLine("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", 40);
        var display = string.Join(Environment.NewLine, result);
        Assert.Equal("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", display);
    }

    [Fact]
    public void WrapMarkupLine_PlainTextWrapsAtWordBoundary()
    {
        var result = PanelRenderer.WrapMarkupLine("aaa bbb ccc ddd", 7);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            aaa bbb
            ccc ddd
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_PlainTextWrapsMultipleLines()
    {
        var result = PanelRenderer.WrapMarkupLine("the quick brown fox jumps over the lazy dog", 20);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            the quick brown fox
            jumps over the lazy
            dog
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_PreservesMarkupAcrossWraps()
    {
        var result = PanelRenderer.WrapMarkupLine("[bold]hello world goodbye[/]", 11);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            [bold]hello world[/]
            [bold]goodbye[/]
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_NestedMarkupPreserved()
    {
        var result = PanelRenderer.WrapMarkupLine("[bold][red]aaa bbb ccc ddd[/][/]", 7);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            [bold][red]aaa bbb[/][/]
            [bold][red]ccc ddd[/][/]
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_LongWordForcesBreak()
    {
        var result = PanelRenderer.WrapMarkupLine("xxxxxxxxxxxxxxx", 10);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            xxxxxxxxxx
            xxxxx
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_EscapedBrackets_TreatedAsText()
    {
        var result = PanelRenderer.WrapMarkupLine("[[hello]]", 40);
        var display = string.Join(Environment.NewLine, result);
        Assert.Equal("[[hello]]", display);
    }

    [Fact]
    public void WrapMarkupLine_MixedMarkupAndText()
    {
        var result = PanelRenderer.WrapMarkupLine("[bold]Status:[/] this is a long value that should wrap around", 30);
        var display = string.Join(Environment.NewLine, result);
        var expected = """
            [bold]Status:[/] this is a long value
            that should wrap around
            """;
        Assert.Equal(expected.Trim(), display);
    }

    [Fact]
    public void WrapMarkupLine_EmptyString_ReturnsSingleEmpty()
    {
        var result = PanelRenderer.WrapMarkupLine("", 40);
        var display = string.Join(Environment.NewLine, result);
        Assert.Equal("", display);
    }

    [Fact]
    public void RenderPanelLine_WrapsAndPreservesBorders_WhenTruncationDisabled()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(44).Height(24);
        var renderer = new PanelRenderer(console);
        renderer.TruncationEnabled = false;

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            ["the quick brown fox jumps over the lazy dog eats food"],
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                             [dim]║[/]
            [dim]╠══════════════════════════════════════════╣[/]
            [dim]║[/] the quick brown fox jumps over the lazy  [dim]║[/]
            [dim]║[/] dog eats food                            [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]╠══════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                 [dim]║[/]
            [dim]╚══════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderPanelLine_TruncatesAndPreservesBorders_WhenTruncationEnabled()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(44).Height(24);
        var renderer = new PanelRenderer(console);
        renderer.TruncationEnabled = true;

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            ["the quick brown fox jumps over the lazy dog eats food"],
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                             [dim]║[/]
            [dim]╠══════════════════════════════════════════╣[/]
            [dim]║[/] the quick brown fox jumps over the la... [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]║[/]                                          [dim]║[/]
            [dim]╠══════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                 [dim]║[/]
            [dim]╚══════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderPanelLine_WrappedDiagnosisPreservesBorders()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(54).Height(24);
        var renderer = new PanelRenderer(console);
        renderer.TruncationEnabled = false;

        var diagLines = new List<string>
        {
            "[bold underline]Diagnosis[/]",
            "Build failed due to missing NuGet package Microsoft.Extensions.Logging version 8.0.0 which is required by the project.",
        };

        renderer.RenderDetailPanel(
            ["Analysis", "Build #123"],
            null,
            diagLines,
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Analysis > Build #123                      [dim]║[/]
            [dim]╠════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold underline]Diagnosis[/]                                          [dim]║[/]
            [dim]║[/] Build failed due to missing NuGet package          [dim]║[/]
            [dim]║[/] Microsoft.Extensions.Logging version 8.0.0 which   [dim]║[/]
            [dim]║[/] is required by the project.                        [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]║[/]                                                    [dim]║[/]
            [dim]╠════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                           [dim]║[/]
            [dim]╚════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    // ── Tokenizer ───────────────────────────────────────────────────

    [Fact]
    public void TokenizeMarkup_PlainText()
    {
        var tokens = PanelRenderer.TokenizeMarkup("hello world");
        Assert.Single(tokens);
        Assert.False(tokens[0].IsTag);
        Assert.Equal("hello world", tokens[0].Text);
    }

    [Fact]
    public void TokenizeMarkup_TagsAndText()
    {
        var tokens = PanelRenderer.TokenizeMarkup("[bold]hello[/]");
        var display = string.Join("", tokens.Select(t => t.IsTag ? $"<{t.Text}>" : t.Text));
        Assert.Equal("<[bold]>hello<[/]>", display);
    }

    [Fact]
    public void TokenizeMarkup_EscapedBrackets()
    {
        var tokens = PanelRenderer.TokenizeMarkup("[[text]]");
        var display = string.Join("", tokens.Select(t => t.Text));
        Assert.Equal("[text]", display);
        Assert.True(tokens.All(t => !t.IsTag));
    }

    // ── Hotkey Formatting ───────────────────────────────────────────

    [Fact]
    public void FormatHotkeyLabel_HighlightsFirstMatchingChar()
    {
        var item = new CommandBarItem("Builds", ConsoleKey.B, 1);
        var result = PanelRenderer.FormatHotkeyLabel(item);
        Assert.Equal("[blue][[B]][/]uilds", result);
    }

    [Fact]
    public void FormatHotkeyLabel_CaseInsensitiveMatch()
    {
        var item = new CommandBarItem("refresh", ConsoleKey.R, 2);
        var result = PanelRenderer.FormatHotkeyLabel(item);
        Assert.Equal("[blue][[r]][/]efresh", result);
    }

    [Fact]
    public void FormatHotkeyLabel_MiddleOfWord()
    {
        var item = new CommandBarItem("Agent task", ConsoleKey.A, 3);
        var result = PanelRenderer.FormatHotkeyLabel(item);
        Assert.Equal("[blue][[A]][/]gent task", result);
    }

    [Fact]
    public void FormatHotkeyLabel_NoMatch_ReturnsLabelUnchanged()
    {
        var item = new CommandBarItem("Builds", ConsoleKey.Z, 1);
        var result = PanelRenderer.FormatHotkeyLabel(item);
        Assert.Equal("Builds", result);
    }

    // ── BuildCommandBarString ───────────────────────────────────────

    [Fact]
    public void BuildCommandBarString_FormatsMultipleCommands()
    {
        var commands = new List<CommandBarItem>
        {
            new("Tests", ConsoleKey.T, 1),
            new("Jobs", ConsoleKey.J, 2),
        };
        var result = PanelRenderer.BuildCommandBarString(commands);
        Assert.Contains("[blue][[T]][/]ests", result);
        Assert.Contains("[blue][[J]][/]obs", result);
        Assert.Contains("[blue]Esc[/] Back", result);
    }

    [Fact]
    public void BuildCommandBarString_EmptyList_JustEscBack()
    {
        var commands = new List<CommandBarItem>();
        var result = PanelRenderer.BuildCommandBarString(commands);
        Assert.Equal("  [blue]Esc[/] Back", result);
    }

    // ── Frame Rendering (integration with TestConsole) ──────────────

    [Fact]
    public void RenderDetailFrame_IncludesBreadcrumbs()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailFrame(["Builds", "#123"], null, ["Content here"], 0, "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > #123                                                        [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Content here                                                                 [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailFrame_ShowsContext()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailFrame(["Tests"], "3 failures", ["data"], 0, "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Tests                                                                [dim]║[/]
            [dim]║[/] 3 failures                                                                   [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] data                                                                         [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailFrame_PaginatesContent()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(12);
        // Height 12: available = 12 - 3 header - 3 footer = 6 lines
        var renderer = new PanelRenderer(console);
        var lines = Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList();

        renderer.RenderDetailFrame(["Test"], null, lines, 0, "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 0                                                                       [dim]║[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](1-6/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
        Assert.DoesNotContain("Line 6", actual);
    }

    [Fact]
    public void RenderDetailFrame_ScrollOffset_ShowsLaterContent()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(12);
        var renderer = new PanelRenderer(console);
        var lines = Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList();

        renderer.RenderDetailFrame(["Test"], null, lines, 5, "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]║[/] Line 6                                                                       [dim]║[/]
            [dim]║[/] Line 7                                                                       [dim]║[/]
            [dim]║[/] Line 8                                                                       [dim]║[/]
            [dim]║[/] Line 9                                                                       [dim]║[/]
            [dim]║[/] Line 10                                                                      [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](6-11/20 Up/Dn)[/]                                                    [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
        Assert.DoesNotContain("Line 4", actual);
    }

    // ── HandleDetailScroll ─────────────────────────────────────────

    private static ConsoleKeyInfo MakeKey(ConsoleKey key) =>
        new('\0', key, false, false, false);

    [Fact]
    public void HandleDetailScroll_DownArrow_ScrollsContent()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(12);
        // Height 12: available = 12 - 3 header - 3 footer = 6 lines
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList(),
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var initialExpected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 0                                                                       [dim]║[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](1-6/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(initialExpected.ReplaceLineEndings("\n").Trim()), actual);

        // Scroll down one line
        var handled = renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        Assert.True(handled);

        var lastPanel = LastPanel(StripChrome(console.Output).ReplaceLineEndings("\n"));
        var scrolledExpected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]║[/] Line 6                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](2-7/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(scrolledExpected.ReplaceLineEndings("\n").Trim()), lastPanel);
    }

    [Fact]
    public void HandleDetailScroll_ReturnsFalse_ForNonScrollKey()
    {
        var console = new TestConsole().Width(80).Height(12);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList(),
            "[blue]Esc[/] Back");

        var handled = renderer.HandleDetailScroll(MakeKey(ConsoleKey.T));
        Assert.False(handled);
    }

    [Fact]
    public void HandleDetailScroll_ReturnsFalse_WhenContentFitsScreen()
    {
        var console = new TestConsole().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            ["Short content"],
            "[blue]Esc[/] Back");

        var handled = renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        Assert.False(handled);
    }

    [Fact]
    public void HandleDetailScroll_PreservesOffset_AcrossMultipleScrolls()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(12);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList(),
            "[blue]Esc[/] Back");

        // Scroll down 3 times
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));

        var actual = LastPanel(StripChrome(console.Output).ReplaceLineEndings("\n"));
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]║[/] Line 6                                                                       [dim]║[/]
            [dim]║[/] Line 7                                                                       [dim]║[/]
            [dim]║[/] Line 8                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](4-9/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void HandleDetailScroll_ReRendering_ResetsOffset()
    {
        // Verifies that calling RenderDetailPanel again resets scroll to 0.
        // This is why callers must guard re-renders behind a needsRender flag.
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(12);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList(),
            "[blue]Esc[/] Back");

        // Scroll down
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        var scrolledPanel = LastPanel(StripChrome(console.Output).ReplaceLineEndings("\n"));
        var scrolledExpected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]║[/] Line 6                                                                       [dim]║[/]
            [dim]║[/] Line 7                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](3-8/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(scrolledExpected.ReplaceLineEndings("\n").Trim()), scrolledPanel);

        // Re-render (simulates a toggle change)
        renderer.RenderDetailPanel(
            ["Test"],
            null,
            Enumerable.Range(0, 20).Select(i => $"Line {i}").ToList(),
            "[blue]Esc[/] Back");

        var rerenderedPanel = LastPanel(StripChrome(console.Output).ReplaceLineEndings("\n"));
        var resetExpected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 0                                                                       [dim]║[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](1-6/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(resetExpected.ReplaceLineEndings("\n").Trim()), rerenderedPanel);

        // Scrolling down once from reset should give 2-7, not 4-9
        renderer.HandleDetailScroll(MakeKey(ConsoleKey.DownArrow));
        var lastPanel = LastPanel(StripChrome(console.Output).ReplaceLineEndings("\n"));
        var afterResetScrollExpected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] Line 1                                                                       [dim]║[/]
            [dim]║[/] Line 2                                                                       [dim]║[/]
            [dim]║[/] Line 3                                                                       [dim]║[/]
            [dim]║[/] Line 4                                                                       [dim]║[/]
            [dim]║[/] Line 5                                                                       [dim]║[/]
            [dim]║[/] Line 6                                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back  [dim](2-7/20 Up/Dn)[/]                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(afterResetScrollExpected.ReplaceLineEndings("\n").Trim()), lastPanel);
    }

    // ── No Unicode in rendered output ───────────────────────────────

    [Fact]
    public void FormatHotkeyLabel_ProducesOnlyAsciiAndSpectreMarkup()
    {
        var items = new[]
        {
            new CommandBarItem("Builds", ConsoleKey.B, 1),
            new CommandBarItem("Tests", ConsoleKey.T, 2),
            new CommandBarItem("Helix", ConsoleKey.H, 3),
            new CommandBarItem("Agent task", ConsoleKey.A, 4),
        };

        foreach (var item in items)
        {
            var result = PanelRenderer.FormatHotkeyLabel(item);
            var plain = Markup.Remove(result);
            AssertAsciiOnly(plain, $"FormatHotkeyLabel({item.Label})");
        }
    }

    [Fact]
    public void BuildCommandBarString_ProducesAsciiPlainText()
    {
        var commands = new List<CommandBarItem>
        {
            new("Builds", ConsoleKey.B, 1),
            new("Tests", ConsoleKey.T, 2),
        };
        var result = PanelRenderer.BuildCommandBarString(commands);
        var plain = Markup.Remove(result);
        AssertAsciiOnly(plain, "BuildCommandBarString");
    }

    private static void AssertAsciiOnly(string text, string context)
    {
        for (var i = 0; i < text.Length; i++)
        {
            Assert.True(text[i] <= 127,
                $"Non-ASCII char U+{(int)text[i]:X4} ('{text[i]}') at position {i} in {context}: \"{text}\"");
        }
    }

    // ── Helix Work Item Display Format ──────────────────────────────

    [Fact]
    public void HelixWorkItem_SectionTitle_IncludesFailed()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);

        renderer.RenderDetailPanel(["Test"], null, [PanelRenderer.FormatSectionTitle("Failed Helix Work Items (2)")], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                 [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold underline]Failed Helix Work Items (2)[/]                                                  [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void HelixWorkItem_Format_IncludesExitCode()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(120).Height(24);
        var renderer = new PanelRenderer(console);

        var wi = "workitem1";
        var job = "job-abc123";
        int? exitCode = 1;
        var isDeadletter = false;
        var exitInfo = exitCode is not null ? $" exit {exitCode}" : "";
        var extra = isDeadletter ? " [red]deadletter[/]" : "";
        var color = (exitCode ?? 1) == 0 ? "green" : "red";

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            [$"  [{color}]X[/] {Markup.Escape(wi)}  [dim]{Markup.Escape(job)}[/]{exitInfo}{extra}"],
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                                                         [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/]   [red]X[/] workitem1  [dim]job-abc123[/] exit 1                                                                                     [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                                                             [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void HelixWorkItem_Deadletter_Format_IncludesExitCodeAndDeadletter()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(120).Height(24);
        var renderer = new PanelRenderer(console);

        var wi = "workitem2";
        var job = "job-def456";
        int? exitCode = -1;
        var isDeadletter = true;
        var exitInfo = exitCode is not null ? $" exit {exitCode}" : "";
        var extra = isDeadletter ? " [red]deadletter[/]" : "";
        var color = (exitCode ?? 1) == 0 ? "green" : "red";

        renderer.RenderDetailPanel(
            ["Test"],
            null,
            [$"  [{color}]X[/] {Markup.Escape(wi)}  [dim]{Markup.Escape(job)}[/]{exitInfo}{extra}"],
            "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Test                                                                                                         [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/]   [red]X[/] workitem2  [dim]job-def456[/] exit -1 [red]deadletter[/]                                                                         [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]║[/]                                                                                                                      [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                                                             [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    // ── Cursor Redraw (partial update) ──────────────────────────────

    [Fact]
    public void SelectInPanel_CursorMove_PreservesSeparator()
    {
        // Simulates: list with context, press Down, then Escape
        // Verifies the partial redraw targets correct rows (not the separator)
        var console = new TestConsole().Width(80).Height(24);
        // Push keys: Down to move cursor, then Escape to exit
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        console.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(console);
        var items = new List<string> { "Item A", "Item B", "Item C" };
        var commands = new List<CommandBarItem> { new("Back", ConsoleKey.Escape, -1) };

        var result = renderer.SelectInPanel(
            ["Builds"],
            "[dim]3 items[/]",
            items,
            commands);

        Assert.Equal(-1, result); // escaped

        var output = console.Output;

        // Verify the partial redraw wrote "Item B" with ">" prefix (cursor moved to it)
        var lastItemB = output.LastIndexOf("Item B");
        Assert.True(lastItemB > 0);
        // The last "Item B" should have > before it (the cursor indicator)
        var segmentAroundB = output[(lastItemB - 10)..lastItemB];
        Assert.Contains(">", segmentAroundB);
    }

    [Fact]
    public void SelectInPanel_CursorMove_SetPosition_Uses1BasedRows()
    {
        // Verifies that SetPosition is called with 1-based row coordinates.
        // With context, the layout is:
        //   Row 1 (1-based): top border
        //   Row 2: header
        //   Row 3: context
        //   Row 4: mid separator
        //   Row 5: first list item (initially selected)
        //   Row 6: second list item
        //   Row 7: third list item
        //
        // After pressing Down, partial redraw should call:
        //   SetPosition(0, 5) to deselect first item
        //   SetPosition(0, 6) to select second item
        // NOT row 4 (the separator) or row 3!

        var spy = new SpyConsole(width: 80, height: 24);
        spy.Inner.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        spy.Inner.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(spy);
        var items = new List<string> { "Item A", "Item B", "Item C" };
        var commands = new List<CommandBarItem> { new("Back", ConsoleKey.Escape, -1) };

        renderer.SelectInPanel(["Builds"], "[dim]3 items[/]", items, commands);

        // Verify SetPosition calls target the correct 1-based rows
        Assert.True(spy.SetPositionCalls.Count >= 2,
            $"Expected at least 2 SetPosition calls, got {spy.SetPositionCalls.Count}");

        // First call: deselect first item at row 5 (1-based)
        var (col1, row1) = spy.SetPositionCalls[0];
        Assert.Equal(5, row1); // row 5 = first list item (1-based)

        // Second call: select second item at row 6 (1-based)
        var (col2, row2) = spy.SetPositionCalls[1];
        Assert.Equal(6, row2); // row 6 = second list item (1-based)

        // Crucially: no SetPosition should target row 4 (the separator)
        Assert.DoesNotContain(spy.SetPositionCalls, call => call.Line == 4);
    }

    [Fact]
    public void SelectInPanel_CursorMove_NoContext_SetPosition_Uses1BasedRows()
    {
        // Without context, layout is:
        //   Row 1: top border
        //   Row 2: header
        //   Row 3: mid separator
        //   Row 4: first list item
        //   Row 5: second list item
        //
        // After Down, SetPosition should target rows 4 and 5.

        var spy = new SpyConsole(width: 80, height: 24);
        spy.Inner.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        spy.Inner.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(spy);
        var items = new List<string> { "Item A", "Item B", "Item C" };
        var commands = new List<CommandBarItem> { new("Back", ConsoleKey.Escape, -1) };

        renderer.SelectInPanel(["Test"], null, items, commands);

        Assert.True(spy.SetPositionCalls.Count >= 2);

        // First call: deselect first item at row 4 (1-based, no context)
        Assert.Equal(4, spy.SetPositionCalls[0].Line);
        // Second call: select second item at row 5
        Assert.Equal(5, spy.SetPositionCalls[1].Line);
        // No call should target row 3 (the separator)
        Assert.DoesNotContain(spy.SetPositionCalls, call => call.Line == 3);
    }

    [Fact]
    public void SelectInPanel_BottomBorder_NoTrailingNewline()
    {
        // Verifies that the frame doesn't emit a trailing newline after the bottom border.
        // A trailing newline would cause terminal scroll when the frame fills the screen,
        // which breaks absolute cursor positioning for partial redraws.
        var console = new TestConsole().Width(80).Height(24);
        console.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(console);
        var items = new List<string> { "Item A", "Item B" };
        var commands = new List<CommandBarItem> { new("Back", ConsoleKey.Escape, -1) };

        renderer.SelectInPanel(["Test"], null, items, commands);

        var output = console.Output;

        // The output should NOT end with a newline — the bottom border
        // is rendered with Markup (not MarkupLine) to prevent scroll
        Assert.False(output.EndsWith("\n"), "Frame should not end with trailing newline");
    }

    // ── PromptInPanel ────────────────────────────────────────────────

    [Fact]
    public void PromptInPanel_PromptTextNotOverwritten()
    {
        // Verifies the prompt text line is rendered intact — the "> " input cursor
        // must NOT overwrite part of the prompt text (regression: was writing at row -4
        // which landed on the prompt line instead of the empty input line at row -3).
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        // Press Escape immediately to exit the prompt
        console.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = renderer.PromptInPanel(["Builds", "Filter"], "Definition pattern (e.g. ci, roslyn-CI*)");

        Assert.Null(result);

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var panel = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > Filter                                                      [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [bold]Definition pattern (e.g. ci, roslyn-CI*)[/]                                     [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Enter[/] Confirm  [blue]Esc[/] Cancel                                                    [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        var expected = MarkupToAnsi(panel.ReplaceLineEndings("\n").Trim()) + "\x1b[5;2H" + MarkupToAnsi("[blue]>[/]");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PromptInPanel_CursorPosition_TargetsInputLine()
    {
        // Verifies SetPosition targets the empty input line (row 5 with no currentValue)
        // Layout: row1=border, row2=header, row3=separator, row4=prompt, row5=input line
        var spy = new SpyConsole(width: 80, height: 24);
        spy.Inner.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(spy);
        renderer.PromptInPanel(["Builds", "Filter"], "Definition pattern");

        // SetPosition should target row 5 (the empty input line), NOT row 4 (prompt text)
        Assert.Contains(spy.SetPositionCalls, call => call.Line == 5);
        Assert.DoesNotContain(spy.SetPositionCalls, call => call.Line == 4);
    }

    [Fact]
    public void PromptInPanel_EnterReturnsTypedText()
    {
        var console = new TestConsole().Width(80).Height(24);
        // Type "roslyn" then press Enter
        foreach (var c in "roslyn")
        {
            console.Input.PushKey(new ConsoleKeyInfo(c, ConsoleKey.A, false, false, false));
        }
        console.Input.PushKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = renderer.PromptInPanel(["Builds", "Filter"], "Definition pattern");

        Assert.Equal("roslyn", result);
    }

    [Fact]
    public void PromptInPanel_EmptyInput_ReturnsNull()
    {
        var console = new TestConsole().Width(80).Height(24);
        // Just press Enter with no text
        console.Input.PushKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = renderer.PromptInPanel(["Builds", "Filter"], "Definition pattern");

        Assert.Null(result);
    }

    // ── PromptKindFilter ─────────────────────────────────────────────

    [Fact]
    public void PromptKindFilter_Escape_ReturnsNull()
    {
        var console = new TestConsole().Width(80).Height(24);
        console.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = BrowserUI.PromptKindFilter(renderer);

        Assert.Null(result);
    }

    [Fact]
    public void PromptKindFilter_SelectPr_ReturnsPr()
    {
        var console = new TestConsole().Width(80).Height(24);
        // Items are: all, pr, ci — "pr" is at index 1, so press Down then Enter
        console.Input.PushKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        console.Input.PushKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = BrowserUI.PromptKindFilter(renderer);

        Assert.Equal("pr", result);
    }

    [Fact]
    public void PromptKindFilter_SelectAll_ReturnsNull()
    {
        var console = new TestConsole().Width(80).Height(24);
        // "all" is at index 0 (default selection), just press Enter
        console.Input.PushKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        var renderer = new PanelRenderer(console);
        var result = BrowserUI.PromptKindFilter(renderer);

        Assert.Null(result);
    }

    [Fact]
    public void PromptKindFilter_RendersInPanel_WithBreadcrumbs()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        console.Input.PushKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));

        var renderer = new PanelRenderer(console);
        BrowserUI.PromptKindFilter(renderer);

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Builds > Filter > Kind                                               [dim]║[/]
            [dim]║[/] [dim]Select build kind to filter on[/]                                               [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]>[/] all                                                                        [dim]║[/]
            [dim]║[/]   pr                                                                         [dim]║[/]
            [dim]║[/]   ci                                                                         [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [dim]Up/Dn Navigate  Enter Select  Esc Back[/]                                       [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailFrame_PreservesBackslashesInContent()
    {
        var console = new TestConsole().EmitAnsiSequences().Width(80).Height(24);
        var renderer = new PanelRenderer(console);
        var errorMsg = """Could not find path 'C:\h\w\AF600976\w\B51709B0\e\bincore'""";

        renderer.RenderDetailPanel(["Tests", "Detail"], null, [$"[red]{Markup.Escape(errorMsg)}[/]"], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════════════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Tests > Detail                                                       [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [red]Could not find path 'C:\h\w\AF600976\w\B51709B0\e\bincore'[/]                   [dim]║[/]
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
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]║[/]                                                                              [dim]║[/]
            [dim]╠══════════════════════════════════════════════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                                                                     [dim]║[/]
            [dim]╚══════════════════════════════════════════════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }

    [Fact]
    public void RenderDetailFrame_BackslashesPreservedWithTruncation()
    {
        // Use a narrow width to force truncation
        var console = new TestConsole().EmitAnsiSequences().Width(40).Height(24);
        var renderer = new PanelRenderer(console);
        renderer.TruncationEnabled = true;
        var errorMsg = """Could not find path 'C:\h\w\AF600976\w\B51709B0\e\bincore'""";

        renderer.RenderDetailPanel(["Tests", "Detail"], null, [$"[red]{Markup.Escape(errorMsg)}[/]"], "[blue]Esc[/] Back");

        var actual = StripChrome(console.Output).ReplaceLineEndings("\n").Trim();
        var expected = """
            [dim]╔══════════════════════════════════════╗[/]
            [dim]║[/] [bold orange1]TIGER[/] [dim]>[/] Tests > Detail               [dim]║[/]
            [dim]╠══════════════════════════════════════╣[/]
            [dim]║[/] Could not find path 'C:\h\w\AF600... [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]║[/]                                      [dim]║[/]
            [dim]╠══════════════════════════════════════╣[/]
            [dim]║[/] [blue]Esc[/] Back                             [dim]║[/]
            [dim]╚══════════════════════════════════════╝[/]
            """;
        Assert.Equal(MarkupToAnsi(expected.ReplaceLineEndings("\n").Trim()), actual);
    }
}

/// <summary>
/// A wrapper around TestConsole that records Cursor.SetPosition calls
/// for verifying cursor positioning in tests.
/// </summary>
file class SpyConsole : IAnsiConsole
{
    public TestConsole Inner { get; }
    public List<(int Column, int Line)> SetPositionCalls { get; } = new();

    private readonly SpyCursor _cursor;

    public SpyConsole(int width = 80, int height = 24)
    {
        Inner = new TestConsole().Width(width).Height(height);
        _cursor = new SpyCursor(Inner.Cursor, this);
    }

    public string Output => Inner.Output;
    public IAnsiConsoleCursor Cursor => _cursor;
    public IAnsiConsoleInput Input => Inner.Input;
    public IExclusivityMode ExclusivityMode => Inner.ExclusivityMode;
    public RenderPipeline Pipeline => Inner.Pipeline;
    public Profile Profile => Inner.Profile;

    public void Clear(bool home) => Inner.Clear(home);
    public void Write(IRenderable renderable) => Inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => Inner.WriteAnsi(action);

    private class SpyCursor : IAnsiConsoleCursor
    {
        private readonly IAnsiConsoleCursor _inner;
        private readonly SpyConsole _spy;

        public SpyCursor(IAnsiConsoleCursor inner, SpyConsole spy)
        {
            _inner = inner;
            _spy = spy;
        }

        public void SetPosition(int column, int line)
        {
            _spy.SetPositionCalls.Add((column, line));
            _inner.SetPosition(column, line);
        }

        public void Move(CursorDirection direction, int steps) => _inner.Move(direction, steps);
        public void Show(bool show) => _inner.Show(show);
    }
}
