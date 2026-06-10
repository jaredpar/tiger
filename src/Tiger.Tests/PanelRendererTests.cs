using Spectre.Console;
using Spectre.Console.Testing;
using Tiger.Commands;
using Xunit;

namespace Tiger.Tests;

public class PanelRendererTests
{
    private static PanelRenderer CreateRenderer(int width = 80, int height = 24)
    {
        var console = new TestConsole().Width(width).Height(height);
        return new PanelRenderer(console);
    }

    // ── Content Capture ─────────────────────────────────────────────

    [Fact]
    public void CaptureContent_CapturesAllLines()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderPanelLine("Line 1");
            renderer.RenderPanelLine("Line 2");
            renderer.RenderEmptyLine();
            renderer.RenderPanelLine("Line 3");
        });

        Assert.Equal(4, lines.Count);
        Assert.Equal("Line 1", lines[0]);
        Assert.Equal("Line 2", lines[1]);
        Assert.Equal("", lines[2]);
        Assert.Equal("Line 3", lines[3]);
    }

    [Fact]
    public void CaptureContent_SectionTitle_IncludesMarkup()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderSectionTitle("My Section");
        });

        Assert.Single(lines);
        Assert.Equal("[bold underline]My Section[/]", lines[0]);
    }

    [Fact]
    public void CaptureContent_Field_IncludesLabelAndValue()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderField("Status", "running");
        });

        Assert.Single(lines);
        Assert.Equal("[bold]Status:[/] running", lines[0]);
    }

    [Fact]
    public void CaptureContent_ReturnsLines()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderPanelLine("stored");
        });

        Assert.Single(lines);
        Assert.Equal("stored", lines[0]);
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
        var console = new TestConsole().Width(80).Height(24);
        var renderer = new PanelRenderer(console);
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderPanelLine("Content here");
        });

        renderer.RenderDetailFrame(["Builds", "#123"], null, lines, 0, "[blue]Esc[/] Back");

        var output = console.Output;
        Assert.Contains("TIGER", output);
        Assert.Contains("Builds", output);
        Assert.Contains("#123", output);
        Assert.Contains("Content here", output);
    }

    [Fact]
    public void RenderDetailFrame_ShowsContext()
    {
        var console = new TestConsole().Width(80).Height(24);
        var renderer = new PanelRenderer(console);
        var lines = renderer.CaptureContent(() =>
        {
            renderer.RenderPanelLine("data");
        });

        renderer.RenderDetailFrame(["Tests"], "3 failures", lines, 0, "[blue]Esc[/] Back");

        var output = console.Output;
        Assert.Contains("3 failures", output);
    }

    [Fact]
    public void RenderDetailFrame_PaginatesContent()
    {
        var console = new TestConsole().Width(80).Height(12);
        // Height 12: available = 12 - 3 header - 3 footer = 6 lines
        var renderer = new PanelRenderer(console);
        var lines = renderer.CaptureContent(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                renderer.RenderPanelLine($"Line {i}");
            }
        });

        renderer.RenderDetailFrame(["Test"], null, lines, 0, "[blue]Esc[/] Back");

        var output = console.Output;
        // First 6 lines should be visible
        Assert.Contains("Line 0", output);
        Assert.Contains("Line 5", output);
        // Line 6+ should NOT be visible (paginated away)
        Assert.DoesNotContain("Line 6", output);
        // Scroll indicator should show
        Assert.Contains("1-6/20", output);
    }

    [Fact]
    public void RenderDetailFrame_ScrollOffset_ShowsLaterContent()
    {
        var console = new TestConsole().Width(80).Height(12);
        var renderer = new PanelRenderer(console);
        var lines = renderer.CaptureContent(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                renderer.RenderPanelLine($"Line {i}");
            }
        });

        renderer.RenderDetailFrame(["Test"], null, lines, 5, "[blue]Esc[/] Back");

        var output = console.Output;
        Assert.DoesNotContain("Line 4", output);
        Assert.Contains("Line 5", output);
        Assert.Contains("Line 10", output);
        Assert.Contains("6-11/20", output);
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
}
