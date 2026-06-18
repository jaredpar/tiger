using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Tiger.Tests;

public class MarkupToAnsiTests
{
    /// <summary>
    /// Converts a string containing Spectre markup to the equivalent ANSI-encoded string.
    /// Uses a real Spectre console configured for Standard color to perform the conversion.
    /// </summary>
    private static string MarkupToAnsi(string markupText)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
        });

        var lines = markupText.Split('\n');
        return string.Join("\n", lines.Select(line =>
        {
            if (string.IsNullOrEmpty(line)) return line;
            return console.ToAnsi(new Markup(line));
        }));
    }

    [Fact]
    public void PlainText_PassesThrough()
    {
        var result = MarkupToAnsi("hello world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Red_ProducesAnsiCode()
    {
        var result = MarkupToAnsi("[red]hello[/]");
        Assert.Equal("\x1b[38;5;9mhello\x1b[0m", result);
    }

    [Fact]
    public void Bold_ProducesAnsiCode()
    {
        var result = MarkupToAnsi("[bold]text[/]");
        Assert.Equal("\x1b[1mtext\x1b[0m", result);
    }

    [Fact]
    public void MixedMarkupAndPlain()
    {
        var result = MarkupToAnsi("[red]X[/] workitem1");
        Assert.Equal("\x1b[38;5;9mX\x1b[0m workitem1", result);
    }

    [Fact]
    public void MultipleLines()
    {
        var input = "line one\n[red]line two[/]";
        var result = MarkupToAnsi(input);
        Assert.Equal("line one\n\x1b[38;5;9mline two\x1b[0m", result);
    }

    [Fact]
    public void MatchesTestConsoleEmitAnsi()
    {
        // The key test: what MarkupToAnsi produces should match
        // what TestConsole.EmitAnsiSequences() captures
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        // Render something with markup
        AnsiConsoleExtensions.Markup(tc, "[red]hello[/] world");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[red]hello[/] world");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesTestConsole_Bold()
    {
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        AnsiConsoleExtensions.Markup(tc, "[bold]Status:[/] running");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[bold]Status:[/] running");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesTestConsole_Dim()
    {
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        AnsiConsoleExtensions.Markup(tc, "[dim]faded[/] normal");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[dim]faded[/] normal");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesTestConsole_NestedStyles()
    {
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        AnsiConsoleExtensions.Markup(tc, "[bold red]error[/] in [dim]module[/]");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[bold red]error[/] in [dim]module[/]");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesTestConsole_EscapedBrackets()
    {
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        AnsiConsoleExtensions.Markup(tc, "[[T]]ests");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[[T]]ests");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesTestConsole_Blue()
    {
        var testConsole = new TestConsole();
        testConsole.EmitAnsiSequences();
        var tc = testConsole.Width(80).Height(10);

        AnsiConsoleExtensions.Markup(tc, "[blue][[E]][/]sc Back");

        var actual = tc.Output;
        var expected = MarkupToAnsi("[blue][[E]][/]sc Back");

        Assert.Equal(expected, actual);
    }
}
