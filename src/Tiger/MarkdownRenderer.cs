using System.Text.RegularExpressions;
using Spectre.Console;

namespace Tiger;

/// <summary>
/// Renders markdown content using Spectre.Console markup.
/// Handles headers, bullet points, bold/italic/code inline formatting,
/// horizontal rules, fenced code blocks, and tables.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// Renders markdown to the console with Spectre.Console formatting.
    /// </summary>
    public static void Render(string markdown)
    {
        var lines = markdown.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');

            // Detect table: line starts with | and has at least 2 |
            if (IsTableRow(trimmed) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].TrimEnd('\r')))
            {
                i = RenderTable(lines, i);
                continue;
            }

            // Fall through to line-by-line rendering
            var markupLines = ToMarkupLines(trimmed);
            foreach (var ml in markupLines)
            {
                AnsiConsole.MarkupLine(ml);
            }
            i++;
        }
    }

    internal static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith('|') && trimmed.EndsWith('|') && trimmed.Count(c => c == '|') >= 2;
    }

    internal static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
        {
            return false;
        }

        // All cells should be dashes (with optional colons for alignment)
        var cells = SplitTableRow(trimmed);
        return cells.All(c => Regex.IsMatch(c.Trim(), @"^:?-+:?$"));
    }

    internal static string[] SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        // Remove leading and trailing |
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }
        return trimmed.Split('|');
    }

    /// <summary>
    /// Renders a markdown table as a Spectre.Console Table. Returns the index
    /// of the first line after the table.
    /// </summary>
    private static int RenderTable(string[] lines, int startIndex)
    {
        var table = new Table().BorderColor(Color.Grey).Border(TableBorder.Rounded).ShowRowSeparators();

        // Header row
        var headers = SplitTableRow(lines[startIndex].TrimEnd('\r'));
        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn(FormatInlineMarkup(header.Trim())).NoWrap());
        }

        // Skip separator row
        var i = startIndex + 2;

        // Data rows
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (!IsTableRow(trimmed))
            {
                break;
            }

            var cells = SplitTableRow(trimmed);
            var row = new string[headers.Length];
            for (var c = 0; c < headers.Length; c++)
            {
                row[c] = c < cells.Length ? FormatInlineMarkup(cells[c].Trim()) : "";
            }
            table.AddRow(row);
            i++;
        }

        AnsiConsole.Write(table);
        return i;
    }

    /// <summary>
    /// Parses a markdown table into headers and rows. Returns the parsed data
    /// and the index of the first line after the table. Testable without a console.
    /// </summary>
    internal static (string[] Headers, List<string[]> Rows, int NextIndex) ParseTable(string[] lines, int startIndex)
    {
        var headers = SplitTableRow(lines[startIndex].TrimEnd('\r'))
            .Select(h => h.Trim())
            .ToArray();

        var i = startIndex + 2; // skip separator
        var rows = new List<string[]>();

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (!IsTableRow(trimmed))
            {
                break;
            }

            var cells = SplitTableRow(trimmed);
            var row = new string[headers.Length];
            for (var c = 0; c < headers.Length; c++)
            {
                row[c] = c < cells.Length ? cells[c].Trim() : "";
            }
            rows.Add(row);
            i++;
        }

        return (headers, rows, i);
    }

    /// <summary>
    /// Converts a single line of markdown to Spectre.Console markup lines.
    /// For multi-line input, use <see cref="Render"/>.
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
    /// Handles inline markdown formatting: [links](url), **bold**, *italic*, `code`.
    /// </summary>
    public static string FormatInlineMarkup(string text)
    {
        // Extract markdown links before escaping, since Markup.Escape will escape the brackets
        // Replace [text](url) with a placeholder, escape everything else, then restore
        var links = new List<(string Placeholder, string Markup)>();
        var linkPattern = new Regex(@"\[([^\]]+)\]\((https?://[^\s)]+)\)");
        var withPlaceholders = linkPattern.Replace(text, m =>
        {
            var placeholder = $"\x00LINK{links.Count}\x00";
            var displayText = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            links.Add((placeholder, $"[link={url}][blue underline]{Markup.Escape(displayText)}[/][/]"));
            return placeholder;
        });

        var escaped = Markup.Escape(withPlaceholders);

        // Restore link placeholders
        foreach (var (placeholder, markup) in links)
        {
            escaped = escaped.Replace(placeholder, markup);
        }

        // Bold: **text** → [bold]text[/]
        escaped = Regex.Replace(escaped, @"\*\*(.+?)\*\*", "[bold]$1[/]");

        // Italic: *text* → [italic]text[/]
        escaped = Regex.Replace(escaped, @"\*(.+?)\*", "[italic]$1[/]");

        // Inline code: `text` → [grey]text[/]
        escaped = Regex.Replace(escaped, @"`(.+?)`", "[grey]$1[/]");

        return escaped;
    }
}
