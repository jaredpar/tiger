using System.Text.RegularExpressions;
using Spectre.Console;

namespace Tiger;

/// <summary>
/// Renders markdown content using Spectre.Console markup.
/// Handles headers, bullet points, bold/italic/code inline formatting,
/// horizontal rules, and fenced code blocks.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Renders markdown to the console with Spectre.Console formatting.
    /// </summary>
    public static void Render(string markdown)
    {
        foreach (var line in ToMarkupLines(markdown))
        {
            AnsiConsole.MarkupLine(line);
        }
    }

    /// <summary>
    /// Converts markdown to a list of Spectre.Console markup lines.
    /// Testable without a console.
    /// </summary>
    public static List<string> ToMarkupLines(string markdown)
    {
        var result = new List<string>();
        var inCodeBlock = false;

        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            // Code block fences
            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                if (inCodeBlock)
                {
                    result.Add("[dim]┌────────────────────────────────────────[/]");
                }
                else
                {
                    result.Add("[dim]└────────────────────────────────────────[/]");
                }
                continue;
            }

            if (inCodeBlock)
            {
                result.Add($"[dim]│[/] [grey]{Markup.Escape(trimmed)}[/]");
                continue;
            }

            // Horizontal rule
            if (trimmed is "---" or "***" or "___")
            {
                result.Add("[dim]────────────────────────────────────────[/]");
                continue;
            }

            // Headers
            if (trimmed.StartsWith("### "))
            {
                result.Add($"[bold]{Markup.Escape(trimmed[4..])}[/]");
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                result.Add($"[bold underline]{Markup.Escape(trimmed[3..])}[/]");
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                result.Add($"[bold blue underline]{Markup.Escape(trimmed[2..])}[/]");
                continue;
            }

            // Bullet points (nested)
            if (trimmed.StartsWith("    - ") || trimmed.StartsWith("    * "))
            {
                var content = FormatInlineMarkup(trimmed[6..]);
                result.Add($"      [dim]•[/] {content}");
                continue;
            }

            // Bullet points (second level)
            if (trimmed.StartsWith("  - ") || trimmed.StartsWith("  * "))
            {
                var content = FormatInlineMarkup(trimmed[4..]);
                result.Add($"    [dim]•[/] {content}");
                continue;
            }

            // Bullet points (top level)
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var content = FormatInlineMarkup(trimmed[2..]);
                result.Add($"  [blue]•[/] {content}");
                continue;
            }

            // Empty lines
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add("");
                continue;
            }

            // Regular text with inline formatting
            result.Add($"  {FormatInlineMarkup(trimmed)}");
        }

        return result;
    }

    /// <summary>
    /// Handles inline markdown formatting: **bold**, *italic*, `code`.
    /// </summary>
    public static string FormatInlineMarkup(string text)
    {
        var escaped = Markup.Escape(text);

        // Bold: **text** → [bold]text[/]
        escaped = Regex.Replace(escaped, @"\*\*(.+?)\*\*", "[bold]$1[/]");

        // Italic: *text* → [italic]text[/]
        escaped = Regex.Replace(escaped, @"\*(.+?)\*", "[italic]$1[/]");

        // Inline code: `text` → [grey]text[/]
        escaped = Regex.Replace(escaped, @"`(.+?)`", "[grey]$1[/]");

        return escaped;
    }
}
