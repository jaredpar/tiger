using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Tiger.Tests;

public class TestConsoleClearBehavior
{
    [Fact]
    public void Clear_Emits_Ansi_Escape_Codes()
    {
        var console = new TestConsole().Width(80).Height(24);
        console.Clear();
        var output = console.Output;

        // Clear() emits ANSI escape sequences into TestConsole output buffer
        // \x1b[2J = erase display, \x1b[3J = erase scrollback, \x1b[1;1H = cursor home
        Assert.NotEqual(string.Empty, output);
        Assert.Equal(14, output.Length);
        Assert.Equal("\x1b[2J\x1b[3J\x1b[1;1H", output);
    }

    [Fact]
    public void Clear_Then_Write_Prepends_Ansi_Before_Content()
    {
        var console = new TestConsole().Width(80).Height(24);
        console.Clear();
        console.MarkupLine("hello");
        var output = console.Output;

        // The clear codes appear before the written content
        Assert.Equal("\x1b[2J\x1b[3J\x1b[1;1Hhello\n", output);
    }
}
