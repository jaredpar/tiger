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
        var markupLines = ToMarkupLines(markdown);
        foreach (var ml in markupLines)
        {
            AnsiConsole.MarkupLine(ml);
        }
    }

    /// <summary>
    /// Converts multi-line markdown to markup strings, properly tracking code
    /// block state across lines. Used for testing the Render pipeline without
    /// needing an IAnsiConsole.
    /// </summary>
    internal static List<string> RenderToLines(string markdown)
    {
        var result = new List<string>();
        var lines = markdown.Split('\n');
        var i = 0;
        var inCodeBlock = false;

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');

            if (trimmed.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                result.Add(inCodeBlock
                    ? "[dim]┌────────────────────────────────────────[/]"
                    : "[dim]└────────────────────────────────────────[/]");
                i++;
                continue;
            }

            if (inCodeBlock)
            {
                result.Add($"[dim]│[/] [grey]{Markup.Escape(trimmed)}[/]");
                i++;
                continue;
            }

            if (IsTableRow(trimmed) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].TrimEnd('\r')))
            {
                // Skip entire table — tables are rendered by Render() directly via Spectre.Console Table
                i += 2; // skip header + separator
                while (i < lines.Length && IsTableRow(lines[i].TrimEnd('\r')))
                {
                    i++;
                }
                continue;
            }

            result.AddRange(ToMarkupLines(trimmed));
            i++;
        }

        return result;
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
    public static List<string> ToMarkupLines(string markdown) => ToMarkupLines(markdown, null);

    /// <summary>
    /// Converts multi-line markdown to Spectre.Console markup lines.
    /// When <paramref name="maxWidth"/> is specified, tables are constrained to fit
    /// within that width by shrinking columns proportionally.
    /// </summary>
    public static List<string> ToMarkupLines(string markdown, int? maxWidth)
    {
        var result = new List<string>();
        var lines = markdown.Split('\n');
        var i = 0;
        var inCodeBlock = false;

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimEnd('\r');

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
                i++;
                continue;
            }

            if (inCodeBlock)
            {
                result.Add($"[dim]│[/] [grey]{Markup.Escape(trimmed)}[/]");
                i++;
                continue;
            }

            // Tables: detect header + separator pattern and render inline
            if (IsTableRow(trimmed) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].TrimEnd('\r')))
            {
                var (headers, rows, nextIndex) = ParseTable(lines, i);
                result.AddRange(RenderTableAsText(headers, rows, maxWidth));
                i = nextIndex;
                continue;
            }

            // Horizontal rule
            if (trimmed is "---" or "***" or "___")
            {
                result.Add("[dim]────────────────────────────────────────[/]");
                i++;
                continue;
            }

            // Headers
            if (trimmed.StartsWith("### "))
            {
                result.Add($"[bold]{FormatInlineMarkup(trimmed[4..])}[/]");
                i++;
                continue;
            }
            if (trimmed.StartsWith("## "))
            {
                result.Add($"[bold underline]{FormatInlineMarkup(trimmed[3..])}[/]");
                i++;
                continue;
            }
            if (trimmed.StartsWith("# "))
            {
                result.Add($"[bold blue underline]{FormatInlineMarkup(trimmed[2..])}[/]");
                i++;
                continue;
            }

            // Bullet points (nested)
            if (trimmed.StartsWith("    - ") || trimmed.StartsWith("    * "))
            {
                var content = FormatInlineMarkup(trimmed[6..]);
                result.Add($"      [dim]•[/] {content}");
                i++;
                continue;
            }

            // Bullet points (second level)
            if (trimmed.StartsWith("  - ") || trimmed.StartsWith("  * "))
            {
                var content = FormatInlineMarkup(trimmed[4..]);
                result.Add($"    [dim]•[/] {content}");
                i++;
                continue;
            }

            // Bullet points (top level)
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var content = FormatInlineMarkup(trimmed[2..]);
                result.Add($"  [blue]•[/] {content}");
                i++;
                continue;
            }

            // Numbered lists
            if (trimmed.Length > 2 && char.IsDigit(trimmed[0]))
            {
                var dotIndex = trimmed.IndexOf(". ");
                if (dotIndex > 0 && dotIndex <= 3 && trimmed[..dotIndex].All(char.IsDigit))
                {
                    var number = trimmed[..dotIndex];
                    var content = FormatInlineMarkup(trimmed[(dotIndex + 2)..]);
                    result.Add($"  [blue]{number}.[/] {content}");
                    i++;
                    continue;
                }
            }

            // Blockquotes
            if (trimmed.StartsWith("> "))
            {
                var content = FormatInlineMarkup(trimmed[2..]);
                result.Add($"  [dim]│[/] [italic]{content}[/]");
                i++;
                continue;
            }

            // Empty lines
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                result.Add("");
                i++;
                continue;
            }

            // Regular text with inline formatting
            result.Add($"  {FormatInlineMarkup(trimmed)}");
            i++;
        }

        return result;
    }

    /// <summary>
    /// Renders a parsed table as formatted text lines suitable for panel display.
    /// Uses column-aligned layout with dim separators. When <paramref name="maxWidth"/>
    /// is specified, columns are shrunk proportionally so the table fits, and cells
    /// that exceed their column width are truncated with "...".
    /// </summary>
    internal static List<string> RenderTableAsText(string[] headers, List<string[]> rows, int? maxWidth = null)
    {
        var result = new List<string>();
        var colCount = headers.Length;

        // Calculate natural column widths based on raw text
        var colWidths = new int[colCount];
        for (var c = 0; c < colCount; c++)
        {
            colWidths[c] = headers[c].Length;
        }
        foreach (var row in rows)
        {
            for (var c = 0; c < colCount; c++)
            {
                if (c < row.Length && row[c].Length > colWidths[c])
                {
                    colWidths[c] = row[c].Length;
                }
            }
        }

        // Constrain columns to fit within maxWidth if specified.
        // Total visible width = 2 (indent) + sum(colWidths) + 3*(colCount-1) (separators " │ ")
        if (maxWidth is not null)
        {
            var separatorWidth = 3 * (colCount - 1);
            var indent = 2;
            var availableForColumns = maxWidth.Value - indent - separatorWidth;

            if (colWidths.Sum() > availableForColumns && availableForColumns > 0)
            {
                // Preserve short columns at their natural width; only shrink wide columns.
                // A column is "short" if its natural width <= ShortColumnThreshold.
                const int ShortColumnThreshold = 12;
                var newWidths = new int[colCount];
                var fixedTotal = 0;
                var shrinkableTotal = 0;

                for (var c = 0; c < colCount; c++)
                {
                    if (colWidths[c] <= ShortColumnThreshold)
                    {
                        newWidths[c] = colWidths[c]; // keep as-is
                        fixedTotal += colWidths[c];
                    }
                    else
                    {
                        newWidths[c] = -1; // mark for shrinking
                        shrinkableTotal += colWidths[c];
                    }
                }

                var remainingForShrinkable = availableForColumns - fixedTotal;

                if (remainingForShrinkable > 0 && shrinkableTotal > 0)
                {
                    // Distribute remaining space proportionally among wide columns
                    var allocated = 0;
                    var lastShrinkable = -1;
                    for (var c = 0; c < colCount; c++)
                    {
                        if (newWidths[c] == -1)
                        {
                            lastShrinkable = c;
                        }
                    }

                    for (var c = 0; c < colCount; c++)
                    {
                        if (newWidths[c] != -1)
                        {
                            continue;
                        }

                        if (c == lastShrinkable)
                        {
                            newWidths[c] = remainingForShrinkable - allocated;
                        }
                        else
                        {
                            newWidths[c] = (int)((double)colWidths[c] / shrinkableTotal * remainingForShrinkable);
                        }

                        newWidths[c] = Math.Max(4, newWidths[c]);
                        allocated += newWidths[c];
                    }
                }
                else
                {
                    // Even fixed columns exceed budget — fall back to proportional for all
                    var totalNatural = colWidths.Sum();
                    var allocated = 0;
                    for (var c = 0; c < colCount; c++)
                    {
                        if (c == colCount - 1)
                        {
                            newWidths[c] = availableForColumns - allocated;
                        }
                        else
                        {
                            newWidths[c] = (int)((double)colWidths[c] / totalNatural * availableForColumns);
                        }
                        newWidths[c] = Math.Max(4, newWidths[c]);
                        allocated += newWidths[c];
                    }
                }

                colWidths = newWidths;
            }
        }

        // Header row — truncate headers that exceed column width
        var headerParts = new string[colCount];
        for (var c = 0; c < colCount; c++)
        {
            var h = headers[c];
            if (h.Length > colWidths[c])
            {
                h = colWidths[c] > 3 ? h[..(colWidths[c] - 3)] + "..." : h[..colWidths[c]];
            }
            headerParts[c] = $"[bold]{FormatInlineMarkup(h.PadRight(colWidths[c]))}[/]";
        }
        result.Add($"  {string.Join(" [dim]│[/] ", headerParts)}");

        // Separator
        var sepParts = new string[colCount];
        for (var c = 0; c < colCount; c++)
        {
            sepParts[c] = new string('─', colWidths[c]);
        }
        result.Add($"  [dim]{string.Join("─┼─", sepParts)}[/]");

        // Data rows — word-wrap cells that exceed their column width
        foreach (var row in rows)
        {
            // Word-wrap each cell into multiple lines
            var wrappedCells = new List<string[]>();
            for (var c = 0; c < colCount; c++)
            {
                var cell = c < row.Length ? row[c] : "";
                wrappedCells.Add(WordWrapCell(cell, colWidths[c]));
            }

            // Determine max number of visual lines across all cells in this row
            var lineCount = wrappedCells.Max(w => w.Length);

            for (var line = 0; line < lineCount; line++)
            {
                var cellParts = new string[colCount];
                for (var c = 0; c < colCount; c++)
                {
                    var cellLine = line < wrappedCells[c].Length
                        ? wrappedCells[c][line]
                        : "";
                    cellParts[c] = FormatInlineMarkup(cellLine.PadRight(colWidths[c]));
                }
                result.Add($"  {string.Join(" [dim]│[/] ", cellParts)}");
            }
        }

        return result;
    }

    /// <summary>
    /// Word-wraps a cell value into lines that fit within the given width.
    /// Breaks at word boundaries when possible, hard-breaks mid-word if a
    /// single word exceeds the width.
    /// </summary>
    internal static string[] WordWrapCell(string text, int width)
    {
        if (width <= 0)
        {
            return [text];
        }

        if (text.Length <= width)
        {
            return [text];
        }

        var lines = new List<string>();
        var remaining = text.AsSpan();

        while (remaining.Length > 0)
        {
            if (remaining.Length <= width)
            {
                lines.Add(remaining.ToString());
                break;
            }

            // Find the last space within the width limit for a word break
            var breakAt = remaining[..width].LastIndexOf(' ');
            if (breakAt <= 0)
            {
                // No space found — hard break at width
                breakAt = width;
                lines.Add(remaining[..breakAt].ToString());
                remaining = remaining[breakAt..];
            }
            else
            {
                lines.Add(remaining[..breakAt].ToString());
                remaining = remaining[(breakAt + 1)..]; // skip the space
            }
        }

        return lines.ToArray();
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

        // Strikethrough: ~~text~~ → [strikethrough]text[/]
        escaped = Regex.Replace(escaped, @"~~(.+?)~~", "[strikethrough]$1[/]");

        return escaped;
    }
}
