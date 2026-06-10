using Spectre.Console;
using Spectre.Console.Rendering;
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

    // ── Helix Work Item Display Format ──────────────────────────────

    [Fact]
    public void HelixWorkItem_SectionTitle_IncludesFailed()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            // Mirrors BuildBrowser.RenderBuildDetail helix section
            var count = 2;
            renderer.RenderSectionTitle($"Failed Helix Work Items ({count})");
        });

        Assert.Single(lines);
        Assert.Contains("Failed Helix Work Items", lines[0]);
    }

    [Fact]
    public void HelixWorkItem_Format_IncludesExitCode()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            // Mirrors the per-item rendering in BuildBrowser
            var wi = "workitem1";
            var job = "job-abc123";
            int? exitCode = 1;
            var isDeadletter = false;
            var exitInfo = exitCode is not null ? $" exit {exitCode}" : "";
            var extra = isDeadletter ? " [red]deadletter[/]" : "";
            var color = (exitCode ?? 1) == 0 ? "green" : "red";
            renderer.RenderPanelLine($"  [{color}]X[/] {Markup.Escape(wi)}  [dim]{Markup.Escape(job)}[/]{exitInfo}{extra}");
        });

        Assert.Single(lines);
        var plain = Markup.Remove(lines[0]);
        Assert.Contains("workitem1", plain);
        Assert.Contains("job-abc123", plain);
        Assert.Contains("exit 1", plain);
    }

    [Fact]
    public void HelixWorkItem_Deadletter_Format_IncludesExitCodeAndDeadletter()
    {
        var renderer = CreateRenderer();
        var lines = renderer.CaptureContent(() =>
        {
            var wi = "workitem2";
            var job = "job-def456";
            int? exitCode = -1;
            var isDeadletter = true;
            var exitInfo = exitCode is not null ? $" exit {exitCode}" : "";
            var extra = isDeadletter ? " [red]deadletter[/]" : "";
            var color = (exitCode ?? 1) == 0 ? "green" : "red";
            renderer.RenderPanelLine($"  [{color}]X[/] {Markup.Escape(wi)}  [dim]{Markup.Escape(job)}[/]{exitInfo}{extra}");
        });

        Assert.Single(lines);
        var plain = Markup.Remove(lines[0]);
        Assert.Contains("workitem2", plain);
        Assert.Contains("job-def456", plain);
        Assert.Contains("exit -1", plain);
        Assert.Contains("deadletter", plain);
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
