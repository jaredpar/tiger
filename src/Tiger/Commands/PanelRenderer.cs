using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Represents a single command in the command bar.
/// </summary>
public record CommandBarItem(string Label, ConsoleKey Hotkey, int ReturnValue);

/// <summary>
/// Renders and manages the "command and control" panel UI. Uses <see cref="IAnsiConsole"/>
/// for all output, input, and cursor operations — fully testable with Spectre.Console.Testing.
///
/// Structure:
/// ╔═══════════════════════════════════════════════════╗
/// ║ TIGER > Section > Subsection                     ║
/// ║ Context line (filter, counts, etc.)              ║
/// ╠═══════════════════════════════════════════════════╣
/// ║ Content area (list or detail)                    ║
/// ╠═══════════════════════════════════════════════════╣
/// ║ Command bar (focusable via Tab)                  ║
/// ╚═══════════════════════════════════════════════════╝
/// </summary>
public class PanelRenderer
{
    private const char TopLeft = '╔';
    private const char TopRight = '╗';
    private const char BottomLeft = '╚';
    private const char BottomRight = '╝';
    private const char Horizontal = '═';
    private const char Vertical = '║';
    private const char MiddleLeft = '╠';
    private const char MiddleRight = '╣';
    private const char Separator = '>';
    private const string BorderStyle = "dim";

    private readonly IAnsiConsole _console;

    // Scroll state for detail panels
    private string[]? _lastDetailBreadcrumbs;
    private string? _lastDetailContext;
    private string? _lastDetailHotkeys;
    private List<string>? _lastDetailLines;
    private int _lastDetailScrollOffset;

    public PanelRenderer(IAnsiConsole console)
    {
        _console = console;
    }

    /// <summary>
    /// Creates a renderer backed by the real terminal (AnsiConsole.Console).
    /// </summary>
    public static PanelRenderer Create() => new(AnsiConsole.Console);

    /// <summary>
    /// The underlying console.
    /// </summary>
    public IAnsiConsole Console => _console;

    /// <summary>
    /// Terminal width from the console profile.
    /// </summary>
    public int Width => _console.Profile.Width;

    /// <summary>
    /// Terminal height from the console profile.
    /// </summary>
    public int Height => _console.Profile.Height;

    /// <summary>
    /// Gets the usable content width (total width - 4 for borders and padding).
    /// </summary>
    public int ContentWidth => Math.Max(40, Width - 4);

    private int RenderContentWidth => Math.Max(0, Width - 4);

    /// <summary>
    /// When true (default), lines are truncated to fit panel width.
    /// When false, lines are allowed to wrap.
    /// </summary>
    public bool TruncationEnabled { get; set; } = true;

    // ── Content building ────────────────────────────────────────────

    /// <summary>
    /// Renders a single line inside the panel with vertical borders.
    /// </summary>
    public void RenderPanelLine(string markupContent)
    {
        RenderPanelLineDirect(markupContent);
    }

    /// <summary>
    /// Renders an empty line inside the panel borders.
    /// </summary>
    public void RenderEmptyLine()
    {
        RenderEmptyLineDirect();
    }

    /// <summary>
    /// Renders a section title.
    /// </summary>
    public void RenderSectionTitle(string title)
    {
        RenderPanelLine($"[bold underline]{title}[/]");
    }

    /// <summary>
    /// Renders a labeled value pair.
    /// </summary>
    public void RenderField(string label, string value)
    {
        RenderPanelLine($"[bold]{label}:[/] {value}");
    }

    /// <summary>
    /// Returns the markup string for a labeled value pair without rendering.
    /// Use when building content line lists for <see cref="RenderDetailPanel"/>.
    /// </summary>
    public static string FormatField(string label, string value) =>
        $"[bold]{label}:[/] {value}";

    /// <summary>
    /// Returns the markup string for a section title without rendering.
    /// Use when building content line lists for <see cref="RenderDetailPanel"/>.
    /// </summary>
    public static string FormatSectionTitle(string title) =>
        $"[bold underline]{title}[/]";

    /// <summary>
    /// Renders the Tiger ASCII art logo.
    /// </summary>
    public void RenderLogo()
    {
        var writer = new StringWriter();
        var figletConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            ColorSystem = ColorSystemSupport.NoColors,
        });
        figletConsole.Write(new FigletText("tiger"));
        var figletLines = writer.ToString().Split('\n', StringSplitOptions.None);

        foreach (var line in figletLines)
        {
            var trimmed = line.TrimEnd('\r', '\n');
            if (trimmed.Length > 0)
            {
                RenderPanelLine($"[bold orange1]{Markup.Escape(trimmed)}[/]");
            }
        }

        RenderEmptyLine();
        RenderPanelLine("[bold orange1]TIGER[/] — CI/CD Infrastructure Management");
        RenderEmptyLine();
    }

    // ── Layout calculations ─────────────────────────────────────────

    /// <summary>
    /// Computes how many content lines fit in the detail panel.
    /// </summary>
    public int GetDetailAvailableHeight(bool hasContext)
    {
        var headerRows = 3 + (hasContext ? 1 : 0);
        var footerRows = 3;
        return Math.Max(5, Height - headerRows - footerRows);
    }

    /// <summary>
    /// Computes the max page size for list panels.
    /// </summary>
    public int GetListPageSize(bool hasContext)
    {
        var headerRows = 3 + (hasContext ? 1 : 0);
        var footerRows = 3;
        var reservedIndicatorRows = 1;
        return Math.Max(5, Height - headerRows - footerRows - reservedIndicatorRows);
    }

    // ── Hotkey formatting ───────────────────────────────────────────

    /// <summary>
    /// Formats a command label with its hotkey in [X] bracket style.
    /// </summary>
    public static string FormatHotkeyLabel(CommandBarItem item)
    {
        var label = item.Label;
        var hotkeyChar = item.Hotkey switch
        {
            >= ConsoleKey.A and <= ConsoleKey.Z => (char)('A' + (item.Hotkey - ConsoleKey.A)),
            _ => '\0'
        };

        if (hotkeyChar == '\0')
        {
            return label;
        }

        for (var i = 0; i < label.Length; i++)
        {
            if (char.ToUpperInvariant(label[i]) == hotkeyChar)
            {
                return $"{label[..i]}[blue][[{label[i]}]][/]{label[(i + 1)..]}";
            }
        }

        return label;
    }

    /// <summary>
    /// Builds a static hotkey string for detail panel footers.
    /// </summary>
    public static string BuildCommandBarString(List<CommandBarItem> commands)
    {
        var parts = new List<string>();
        foreach (var cmd in commands)
        {
            parts.Add(FormatHotkeyLabel(cmd));
        }
        return string.Join("  ", parts) + "  [blue]Esc[/] Back";
    }

    /// <summary>
    /// Builds the command bar markup with focus state for interactive panels.
    /// </summary>
    public static string BuildCommandBarMarkup(List<CommandBarItem> commands, int focusedIndex, bool barFocused)
    {
        if (commands.Count == 0)
        {
            return "[dim]Up/Dn Navigate  Enter Select  Esc Back[/]";
        }

        var parts = new List<string>();
        for (var i = 0; i < commands.Count; i++)
        {
            if (barFocused && i == focusedIndex)
            {
                parts.Add($"[bold white on blue] {commands[i].Label} [/]");
            }
            else
            {
                parts.Add(FormatHotkeyLabel(commands[i]));
            }
        }
        var barContent = string.Join("  ", parts);
        var tabHint = barFocused
            ? "  [dim]<-> Move  Enter Execute  Tab Content[/]"
            : "  [dim]Tab Commands[/]";
        return $"{barContent}{tabHint}";
    }

    // ── Line truncation ─────────────────────────────────────────────

    /// <summary>
    /// Truncates markup content so its plain-text length fits within the panel width.
    /// </summary>
    public string TruncateToFit(string markupContent)
    {
        var maxContentWidth = RenderContentWidth;
        var plainText = Markup.Remove(markupContent);

        if (plainText.Length <= maxContentWidth)
        {
            return markupContent;
        }

        var truncated = plainText[..(maxContentWidth - 3)] + "...";
        return Markup.Escape(truncated);
    }

    // ── Line wrapping ───────────────────────────────────────────────

    /// <summary>
    /// Wraps a markup string into multiple lines so each line's visible text
    /// fits within <paramref name="maxWidth"/>. Preserves Spectre markup tags
    /// by tracking open styles and re-opening them on continuation lines.
    /// </summary>
    internal static List<string> WrapMarkupLine(string markupContent, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return [markupContent];
        }

        var plainText = Markup.Remove(markupContent);
        if (plainText.Length <= maxWidth)
        {
            return [markupContent];
        }

        // Parse the markup string into tokens (tags vs text segments)
        var tokens = TokenizeMarkup(markupContent);

        var result = new List<string>();
        var currentLine = new System.Text.StringBuilder();
        var visibleLen = 0;
        var openStyles = new Stack<string>();

        foreach (var token in tokens)
        {
            if (token.IsTag)
            {
                // Track open/close styles for continuation lines
                if (token.Text == "[/]")
                {
                    if (openStyles.Count > 0)
                    {
                        openStyles.Pop();
                    }
                }
                else
                {
                    openStyles.Push(token.Text);
                }
                currentLine.Append(token.Text);
                continue;
            }

            // Visible text — need to wrap at word boundaries
            var text = token.Text;
            var pos = 0;
            while (pos < text.Length)
            {
                var remaining = maxWidth - visibleLen;
                if (remaining <= 0)
                {
                    // Close open styles, emit line, start new one
                    result.Add(CloseAndEmit(currentLine, openStyles));
                    currentLine = ReopenStyles(openStyles);
                    visibleLen = 0;
                    remaining = maxWidth;
                }

                var chunk = text[pos..];
                if (chunk.Length <= remaining)
                {
                    currentLine.Append(EscapeBrackets(chunk));
                    visibleLen += chunk.Length;
                    pos = text.Length;
                }
                else
                {
                    // Find the last word boundary within the remaining space
                    var breakPos = FindWordBreak(chunk, remaining);
                    if (breakPos <= 0)
                    {
                        if (visibleLen == 0)
                        {
                            // Single word longer than max width — force break
                            breakPos = remaining;
                        }
                        else
                        {
                            // Wrap before this word
                            result.Add(CloseAndEmit(currentLine, openStyles));
                            currentLine = ReopenStyles(openStyles);
                            visibleLen = 0;
                            continue;
                        }
                    }

                    currentLine.Append(EscapeBrackets(chunk[..breakPos]));
                    visibleLen += breakPos;
                    pos += breakPos;

                    // Skip trailing space at the break point
                    if (pos < text.Length && text[pos] == ' ')
                    {
                        pos++;
                    }

                    // Emit current line
                    result.Add(CloseAndEmit(currentLine, openStyles));
                    currentLine = ReopenStyles(openStyles);
                    visibleLen = 0;
                }
            }
        }

        // Emit the final line if non-empty
        if (currentLine.Length > 0)
        {
            // Close all open styles
            for (var i = 0; i < openStyles.Count; i++)
            {
                currentLine.Append("[/]");
            }
            result.Add(currentLine.ToString());
        }

        return result.Count > 0 ? result : [""];
    }

    private static string CloseAndEmit(System.Text.StringBuilder sb, Stack<string> openStyles)
    {
        // Close all open styles for this line
        for (var i = 0; i < openStyles.Count; i++)
        {
            sb.Append("[/]");
        }
        var line = sb.ToString();
        return line;
    }

    private static System.Text.StringBuilder ReopenStyles(Stack<string> openStyles)
    {
        var sb = new System.Text.StringBuilder();
        // Re-open styles in the correct order (bottom of stack first)
        foreach (var style in openStyles.Reverse())
        {
            sb.Append(style);
        }
        return sb;
    }

    /// <summary>
    /// Finds the last space position within maxLen characters, returning the
    /// length of text to include. Returns maxLen if the character at that position
    /// is a space (natural word boundary). Returns 0 if no space is found.
    /// </summary>
    private static int FindWordBreak(string text, int maxLen)
    {
        // If we're at a natural word boundary (space at maxLen or end of text), take full chunk
        if (maxLen >= text.Length || text[maxLen] == ' ')
        {
            return maxLen;
        }

        // Find the last space within the first maxLen characters
        var lastSpace = -1;
        for (var i = 0; i < maxLen; i++)
        {
            if (text[i] == ' ')
            {
                lastSpace = i;
            }
        }
        return lastSpace > 0 ? lastSpace : 0;
    }

    /// <summary>
    /// Re-escapes literal bracket characters in visible text so the output is valid
    /// Spectre markup. TokenizeMarkup decodes [[ → [ and ]] → ] for width measurement,
    /// but the output string must use [[ and ]] for Spectre to treat them as literals.
    /// </summary>
    private static string EscapeBrackets(string text)
    {
        if (!text.Contains('[') && !text.Contains(']'))
        {
            return text;
        }
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    /// <summary>
    /// Tokenizes a Spectre markup string into tags (e.g., [bold], [/]) and
    /// visible text segments.
    /// </summary>
    internal static List<MarkupToken> TokenizeMarkup(string markup)
    {
        var tokens = new List<MarkupToken>();
        var i = 0;
        var textStart = 0;

        while (i < markup.Length)
        {
            if (markup[i] == '[')
            {
                // Check for escaped bracket [[
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    // Escaped opening bracket — treat as visible text [
                    if (textStart < i)
                    {
                        tokens.Add(new MarkupToken(markup[textStart..i], false));
                    }
                    tokens.Add(new MarkupToken("[", false));
                    i += 2;
                    textStart = i;
                    continue;
                }

                // Find matching ]
                var closePos = markup.IndexOf(']', i + 1);
                if (closePos < 0)
                {
                    // No closing bracket — treat as text
                    i++;
                    continue;
                }

                // Emit any preceding text
                if (textStart < i)
                {
                    tokens.Add(new MarkupToken(markup[textStart..i], false));
                }

                // Emit the tag
                var tag = markup[i..(closePos + 1)];
                tokens.Add(new MarkupToken(tag, true));
                i = closePos + 1;
                textStart = i;
            }
            else if (markup[i] == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                // Escaped closing bracket ]]
                if (textStart < i)
                {
                    tokens.Add(new MarkupToken(markup[textStart..i], false));
                }
                tokens.Add(new MarkupToken("]", false));
                i += 2;
                textStart = i;
            }
            else
            {
                i++;
            }
        }

        // Emit trailing text
        if (textStart < markup.Length)
        {
            tokens.Add(new MarkupToken(markup[textStart..markup.Length], false));
        }

        return tokens;
    }

    internal record struct MarkupToken(string Text, bool IsTag);

    // ── Interactive: Main Menu ───────────────────────────────────────

    /// <summary>
    /// Renders the main dashboard with logo and command bar.
    /// Returns the selected command's ReturnValue, or -1 on Escape.
    /// </summary>
    public int ShowMainMenu(List<CommandBarItem> commands)
    {
        var barIndex = 0;

        while (true)
        {
            Clear();
            _console.Cursor.Show(false);
            RenderMainMenuFrame(commands, barIndex);

            while (true)
            {
                var key = ReadKey();
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                        barIndex = (barIndex - 1 + commands.Count) % commands.Count;
                        // Full redraw for simplicity (menu is small)
                        break;
                    case ConsoleKey.RightArrow:
                        barIndex = (barIndex + 1) % commands.Count;
                        break;
                    case ConsoleKey.Enter:
                        _console.Cursor.Show(true);
                        return commands[barIndex].ReturnValue;
                    case ConsoleKey.Escape:
                        _console.Cursor.Show(true);
                        return -1;
                    default:
                        for (var i = 0; i < commands.Count; i++)
                        {
                            if (key.Key == commands[i].Hotkey)
                            {
                                _console.Cursor.Show(true);
                                return commands[i].ReturnValue;
                            }
                        }
                        continue; // Unknown key, don't redraw
                }
                break; // Redraw on arrow keys
            }
        }
    }

    // ── Interactive: Select in Panel ────────────────────────────────

    /// <summary>
    /// List selection with a focusable command bar.
    /// Returns list index on Enter, command ReturnValue, or -1 on Escape.
    /// </summary>
    public int SelectInPanel(string[] breadcrumbs, string? context, List<string> items,
        List<CommandBarItem> commands, int pageSize = 0,
        int startIndex = 0, HashSet<int>? skipIndices = null, Action? renderAboveList = null)
    {
        if (items.Count == 0)
        {
            return -1;
        }

        var maxPageSize = GetListPageSize(context is not null);
        if (pageSize <= 0)
        {
            pageSize = maxPageSize;
        }
        else
        {
            pageSize = Math.Min(pageSize, maxPageSize);
        }

        var selected = Math.Clamp(startIndex, 0, items.Count - 1);
        if (skipIndices is not null)
        {
            while (selected < items.Count && skipIndices.Contains(selected))
            {
                selected++;
            }
            if (selected >= items.Count)
            {
                selected = Math.Clamp(startIndex, 0, items.Count - 1);
                while (selected > 0 && skipIndices.Contains(selected))
                {
                    selected--;
                }
            }
        }

        var scrollOffset = Math.Max(0, selected - pageSize + 1);
        var visibleCount = Math.Min(pageSize, items.Count);
        var barFocused = false;
        var barIndex = 0;
        var needsFullRedraw = true;

        while (true)
        {
            if (needsFullRedraw)
            {
                Clear();
                _console.Cursor.Show(false);
                RenderListFrame(breadcrumbs, context, items, selected, scrollOffset,
                    visibleCount, barFocused, commands, barIndex, renderAboveList);
                needsFullRedraw = false;
            }

            var key = ReadKey();
            var prevSelected = selected;
            var prevScrollOffset = scrollOffset;

            if (key.Key == ConsoleKey.Tab)
            {
                if (commands.Count == 0)
                {
                    continue;
                }
                barFocused = !barFocused;
                needsFullRedraw = true;
                continue;
            }

            if (barFocused)
            {
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                        barIndex = (barIndex - 1 + commands.Count) % commands.Count;
                        needsFullRedraw = true;
                        continue;
                    case ConsoleKey.RightArrow:
                        barIndex = (barIndex + 1) % commands.Count;
                        needsFullRedraw = true;
                        continue;
                    case ConsoleKey.Enter:
                        _console.Cursor.Show(true);
                        return commands[barIndex].ReturnValue;
                    case ConsoleKey.Escape:
                        _console.Cursor.Show(true);
                        return -1;
                    default:
                        for (var i = 0; i < commands.Count; i++)
                        {
                            if (key.Key == commands[i].Hotkey)
                            {
                                _console.Cursor.Show(true);
                                return commands[i].ReturnValue;
                            }
                        }
                        continue;
                }
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selected > 0)
                    {
                        selected--;
                        while (selected > 0 && skipIndices is not null && skipIndices.Contains(selected))
                        {
                            selected--;
                        }
                        if (skipIndices is not null && skipIndices.Contains(selected))
                        {
                            selected++;
                        }
                        if (selected < scrollOffset)
                        {
                            scrollOffset = selected;
                        }
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (selected < items.Count - 1)
                    {
                        selected++;
                        while (selected < items.Count - 1 && skipIndices is not null && skipIndices.Contains(selected))
                        {
                            selected++;
                        }
                        if (skipIndices is not null && skipIndices.Contains(selected))
                        {
                            selected--;
                        }
                        if (selected >= scrollOffset + visibleCount)
                        {
                            scrollOffset = selected - visibleCount + 1;
                        }
                    }
                    break;
                case ConsoleKey.Enter:
                    _console.Cursor.Show(true);
                    return selected;
                case ConsoleKey.Escape:
                    _console.Cursor.Show(true);
                    return -1;
                default:
                    for (var i = 0; i < commands.Count; i++)
                    {
                        if (key.Key == commands[i].Hotkey)
                        {
                            _console.Cursor.Show(true);
                            return commands[i].ReturnValue;
                        }
                    }
                    break;
            }

            if (selected != prevSelected || scrollOffset != prevScrollOffset)
            {
                if (scrollOffset != prevScrollOffset)
                {
                    // Scroll changed — need full redraw
                    needsFullRedraw = true;
                }
                else
                {
                    // Only cursor moved within same page — redraw just the two affected lines
                    // Row calculation: 0-based row index of first list item
                    var listStartRow = 1 + 1 + (context is not null ? 1 : 0) + 1; // top border + header + context? + mid border
                    if (renderAboveList is not null)
                    {
                        // renderAboveList adds content before the list; we can't easily count lines
                        // so fall back to full redraw
                        needsFullRedraw = true;
                    }
                    else
                    {
                        // SetPosition uses 1-based coordinates (ANSI CUP), so add 1
                        var prevRow = listStartRow + (prevSelected - scrollOffset) + 1;
                        var newRow = listStartRow + (selected - scrollOffset) + 1;

                        // Redraw old line (remove cursor)
                        _console.Cursor.SetPosition(0, prevRow);
                        RenderPanelLineDirect($"  {items[prevSelected]}");

                        // Redraw new line (add cursor)
                        _console.Cursor.SetPosition(0, newRow);
                        RenderPanelLineDirect($"[blue]>[/] {items[selected]}");

                        // Update counter if visible
                        if (items.Count > visibleCount)
                        {
                            var counterRow = listStartRow + visibleCount + 1;
                            _console.Cursor.SetPosition(0, counterRow);
                            RenderPanelLineDirect($"[dim]({selected + 1}/{items.Count})[/]");
                        }
                    }
                }
            }
        }
    }

    // ── Interactive: Detail Panel ───────────────────────────────────

    /// <summary>
    /// Renders a detail view with scrollable content.
    /// Callers supply raw markup lines; wrapping/truncation is applied here.
    /// Use <see cref="HandleDetailScroll"/> in the caller's key loop.
    /// </summary>
    public void RenderDetailPanel(string[] breadcrumbs, string? context, List<string> rawContentLines, string hotkeys)
    {
        var processedLines = new List<string>();
        foreach (var line in rawContentLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                processedLines.Add("");
            }
            else if (TruncationEnabled)
            {
                processedLines.Add(TruncateToFit(line));
            }
            else
            {
                processedLines.AddRange(WrapMarkupLine(line, RenderContentWidth));
            }
        }

        _lastDetailBreadcrumbs = breadcrumbs;
        _lastDetailContext = context;
        _lastDetailHotkeys = hotkeys;
        _lastDetailLines = processedLines;
        _lastDetailScrollOffset = 0;

        Clear();
        _console.Cursor.Show(false);
        RenderDetailFrame(breadcrumbs, context, processedLines, 0, hotkeys);
        _console.Cursor.Show(true);
    }

    /// <summary>
    /// Handles scroll keys for the last rendered detail panel.
    /// Returns true if handled, false if the caller should process the key.
    /// </summary>
    public bool HandleDetailScroll(ConsoleKeyInfo key)
    {
        if (_lastDetailLines is null || _lastDetailLines.Count == 0)
        {
            return false;
        }

        var availableHeight = GetDetailAvailableHeight(_lastDetailContext is not null);
        if (_lastDetailLines.Count <= availableHeight)
        {
            return false;
        }

        var maxOffset = Math.Max(0, _lastDetailLines.Count - availableHeight);
        var oldOffset = _lastDetailScrollOffset;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _lastDetailScrollOffset = Math.Max(0, _lastDetailScrollOffset - 1);
                break;
            case ConsoleKey.DownArrow:
                _lastDetailScrollOffset = Math.Min(maxOffset, _lastDetailScrollOffset + 1);
                break;
            case ConsoleKey.PageUp:
                _lastDetailScrollOffset = Math.Max(0, _lastDetailScrollOffset - 10);
                break;
            case ConsoleKey.PageDown:
                _lastDetailScrollOffset = Math.Min(maxOffset, _lastDetailScrollOffset + 10);
                break;
            default:
                return false;
        }

        if (_lastDetailScrollOffset != oldOffset)
        {
            Clear();
            _console.Cursor.Show(false);
            RenderDetailFrame(_lastDetailBreadcrumbs!, _lastDetailContext, _lastDetailLines, _lastDetailScrollOffset, _lastDetailHotkeys!);
            _console.Cursor.Show(true);
        }

        return true;
    }

    // ── Interactive: Text Prompt ─────────────────────────────────────

    /// <summary>
    /// Prompts for text input inside the panel frame.
    /// </summary>
    public string? PromptInPanel(string[] breadcrumbs, string prompt, string? currentValue = null)
    {
        Clear();
        _console.Cursor.Show(true);
        var width = Width - 2;

        int row = 1; // 1-based row tracking
        _console.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        row++; // row 2: header
        var crumbText = string.Join($" {Separator} ", breadcrumbs);
        RenderPanelLineDirect($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");
        row++; // row 3: mid separator
        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        row++; // row 4: prompt text
        RenderPanelLineDirect($"[bold]{prompt}[/]");
        if (currentValue is not null)
        {
            row++; // current value line
            RenderPanelLineDirect($"[dim]Current: {Markup.Escape(currentValue)}[/]");
        }
        row++; // empty input line
        var inputRow = row;
        RenderEmptyLineDirect();
        row++; // mid separator
        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        row++; // footer
        RenderPanelLineDirect("[blue]Enter[/] Confirm  [blue]Esc[/] Cancel");
        // Use Markup (no trailing newline) to prevent terminal scroll when frame fills the screen
        _console.Markup($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");

        // Position cursor on the empty input line and render the "> " prompt
        _console.Cursor.SetPosition(2, inputRow);
        _console.Markup("[blue]>[/] ");

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = ReadKey();
            if (key.Key == ConsoleKey.Escape)
            {
                return null;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                var result = buffer.ToString().Trim();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    _console.Markup("\b \b");
                }
                continue;
            }
            if (key.KeyChar >= 32)
            {
                buffer.Append(key.KeyChar);
                _console.Markup(Markup.Escape(key.KeyChar.ToString()));
            }
        }
    }

    // ── Frame rendering ─────────────────────────────────────────────

    internal void RenderDetailFrame(string[] breadcrumbs, string? context, List<string> contentLines, int scrollOffset, string hotkeys)
    {
        var width = Width - 2;

        _console.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        var crumbText = string.Join($" {Separator} ", breadcrumbs);
        RenderPanelLineDirect($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");

        if (context is not null)
        {
            RenderPanelLineDirect(context);
        }

        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");

        var availableHeight = GetDetailAvailableHeight(context is not null);
        var visibleLines = contentLines.Skip(scrollOffset).Take(availableHeight).ToList();
        foreach (var line in visibleLines)
        {
            RenderPanelLineDirect(line);
        }

        for (var i = visibleLines.Count; i < availableHeight; i++)
        {
            RenderEmptyLineDirect();
        }

        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        var scrollHint = contentLines.Count > availableHeight
            ? $"  [dim]({scrollOffset + 1}-{Math.Min(scrollOffset + availableHeight, contentLines.Count)}/{contentLines.Count} Up/Dn)[/]"
            : "";
        RenderPanelLineDirect($"{hotkeys}{scrollHint}");
        // Use Markup (no trailing newline) to prevent terminal scroll when frame fills the screen
        _console.Markup($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");
    }

    private void RenderMainMenuFrame(List<CommandBarItem> commands, int barIndex)
    {
        var width = Width - 2;

        _console.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        RenderPanelLineDirect("[bold orange1]TIGER[/]");
        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");

        RenderLogo();

        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        RenderPanelLineDirect(BuildCommandBarMarkup(commands, barIndex, true));
        // Use Markup (no trailing newline) to prevent terminal scroll when frame fills the screen
        _console.Markup($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");
    }

    private void RenderListFrame(string[] breadcrumbs, string? context, List<string> items,
        int selected, int scrollOffset, int visibleCount, bool barFocused,
        List<CommandBarItem> commands, int barIndex, Action? renderAboveList)
    {
        var width = Width - 2;

        _console.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        var crumbText = string.Join($" {Separator} ", breadcrumbs);
        RenderPanelLineDirect($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");

        if (context is not null)
        {
            RenderPanelLineDirect(context);
        }

        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        renderAboveList?.Invoke();

        for (var i = 0; i < visibleCount; i++)
        {
            var idx = scrollOffset + i;
            if (idx >= items.Count)
            {
                RenderEmptyLineDirect();
                continue;
            }

            if (!barFocused && idx == selected)
            {
                RenderPanelLineDirect($"[blue]>[/] {items[idx]}");
            }
            else
            {
                RenderPanelLineDirect($"  {items[idx]}");
            }
        }

        if (items.Count > visibleCount)
        {
            RenderPanelLineDirect($"[dim]({selected + 1}/{items.Count})[/]");
        }

        _console.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        RenderPanelLineDirect(BuildCommandBarMarkup(commands, barIndex, barFocused));
        // Use Markup (no trailing newline) to prevent terminal scroll when frame fills the screen
        _console.Markup($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");
    }

    // ── Direct rendering helpers ────────────────────────────────────

    private void RenderPanelLineDirect(string markupContent)
    {
        if (!TruncationEnabled)
        {
            var wrapped = WrapMarkupLine(markupContent, RenderContentWidth);
            foreach (var line in wrapped)
            {
                RenderSinglePanelLine(line);
            }
            return;
        }

        RenderSinglePanelLine(TruncateToFit(markupContent));
    }

    private void RenderSinglePanelLine(string markupContent)
    {
        _console.Markup($"[{BorderStyle}]{Vertical}[/] ");
        _console.Markup(markupContent);

        var plainLen = Markup.Remove(markupContent).Length;
        var padding = Math.Max(0, RenderContentWidth - plainLen);
        _console.Markup(new string(' ', padding));
        _console.MarkupLine($" [{BorderStyle}]{Vertical}[/]");
    }

    private void RenderEmptyLineDirect()
    {
        var width = Width - 2;
        _console.Markup($"[{BorderStyle}]{Vertical}[/]");
        _console.Markup(new string(' ', width));
        _console.MarkupLine($"[{BorderStyle}]{Vertical}[/]");
    }

    // ── Console operations ──────────────────────────────────────────

    private void Clear()
    {
        _console.Clear();
    }

    private ConsoleKeyInfo ReadKey()
    {
        var result = _console.Input.ReadKey(true);
        return result ?? new ConsoleKeyInfo('\0', ConsoleKey.None, false, false, false);
    }
}
