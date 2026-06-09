using Spectre.Console;

namespace Tiger.Commands;

/// <summary>
/// Represents a single command in the command bar.
/// </summary>
public record CommandBarItem(string Label, ConsoleKey Hotkey, int ReturnValue);

/// <summary>
/// Renders a consistent "command and control" panel layout for all screens.
///
/// Structure:
/// ╔═══════════════════════════════════════════════════╗
/// ║ TIGER ▸ Section ▸ Subsection                     ║
/// ║ Context line (filter, counts, etc.)              ║
/// ╠═══════════════════════════════════════════════════╣
/// ║ Content area (list or detail)                    ║
/// ╠═══════════════════════════════════════════════════╣
/// ║ Command bar (focusable via Tab)                  ║
/// ╚═══════════════════════════════════════════════════╝
///
/// Border color is dim gray; hotkeys are blue for contrast.
/// Tab moves focus between content and command bar.
/// In the command bar, left/right navigates, Enter executes.
/// </summary>
public static class PanelLayout
{
    private const char TopLeft = '╔';
    private const char TopRight = '╗';
    private const char BottomLeft = '╚';
    private const char BottomRight = '╝';
    private const char Horizontal = '═';
    private const char Vertical = '║';
    private const char MiddleLeft = '╠';
    private const char MiddleRight = '╣';
    private const char Separator = '▸';

    private const string BorderStyle = "dim";

    /// <summary>
    /// Gets the usable content width inside the panel borders (total width - 4 for borders and padding).
    /// </summary>
    public static int ContentWidth => Math.Max(40, Console.WindowWidth - 4);

    /// <summary>
    /// Renders a single line inside the panel with vertical borders.
    /// When called during RenderDetailPanel's content capture phase, lines are buffered instead.
    /// </summary>
    public static void RenderPanelLine(string markupContent)
    {
        if (_captureTarget is not null)
        {
            _captureTarget.Add(markupContent);
            return;
        }

        AnsiConsole.Markup($"[{BorderStyle}]{Vertical}[/] ");
        AnsiConsole.Markup(markupContent);

        var plainLen = Markup.Remove(markupContent).Length;
        var padding = Math.Max(0, Console.WindowWidth - 4 - plainLen);
        Console.Write(new string(' ', padding));
        AnsiConsole.MarkupLine($" [{BorderStyle}]{Vertical}[/]");
    }

    /// <summary>
    /// Renders an empty line inside the panel borders.
    /// When called during RenderDetailPanel's content capture phase, an empty line is buffered.
    /// </summary>
    public static void RenderEmptyLine()
    {
        if (_captureTarget is not null)
        {
            _captureTarget.Add("");
            return;
        }

        var width = Console.WindowWidth - 2;
        AnsiConsole.Markup($"[{BorderStyle}]{Vertical}[/]");
        Console.Write(new string(' ', width));
        AnsiConsole.MarkupLine($"[{BorderStyle}]{Vertical}[/]");
    }

    /// <summary>
    /// Renders a section title inside the panel content area.
    /// </summary>
    public static void RenderSectionTitle(string title)
    {
        RenderPanelLine($"[bold underline]{title}[/]");
    }

    /// <summary>
    /// Renders a labeled value pair inside the panel.
    /// </summary>
    public static void RenderField(string label, string value)
    {
        RenderPanelLine($"[bold]{label}:[/] {value}");
    }

    /// <summary>
    /// Renders the Tiger ASCII art logo using Spectre.Console FigletText,
    /// plus the TIGER text branding.
    /// </summary>
    public static void RenderLogo()
    {
        // Render figlet to a string buffer and extract lines
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            ColorSystem = ColorSystemSupport.NoColors,
        });
        console.Write(new FigletText("tiger"));
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

    // ── Command Bar ─────────────────────────────────────────────────

    /// <summary>
    /// Formats a label with its hotkey letter highlighted in [X] bracket style.
    /// </summary>
    private static string FormatHotkeyLabel(CommandBarItem item)
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

        // Find the hotkey character in the label (case-insensitive)
        for (var i = 0; i < label.Length; i++)
        {
            if (char.ToUpperInvariant(label[i]) == hotkeyChar)
            {
                return $"{label[..i]}[blue][[{label[i]}]][/]{label[(i + 1)..]}";
            }
        }

        return label;
    }

    private static string BuildCommandBarMarkup(List<CommandBarItem> commands, int focusedIndex, bool barFocused)
    {
        if (commands.Count == 0)
        {
            return "[dim]↑↓ Navigate  Enter Select  Esc Back[/]";
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
            ? "  [dim]←→ Move  Enter Execute  Tab Content[/]"
            : "  [dim]Tab Commands[/]";
        return $"{barContent}{tabHint}";
    }

    /// <summary>
    /// Builds a static hotkey string from a list of commands (for use in RenderDetailPanel footers).
    /// Renders each command with the [X] bracket-style hotkey highlighting.
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

    private static void RenderCommandBarAt(int row, List<CommandBarItem> commands, int focusedIndex, bool barFocused)
    {
        Console.SetCursorPosition(0, row);
        RenderPanelLine(BuildCommandBarMarkup(commands, focusedIndex, barFocused));
    }

    // ── Flicker-free list line ──────────────────────────────────────

    private static void RenderListLineAt(int row, string markupContent, bool isSelected)
    {
        Console.SetCursorPosition(0, row);
        var prefix = isSelected ? "[blue]▸[/] " : "  ";
        var line = $"{prefix}{markupContent}";
        AnsiConsole.Markup($"[{BorderStyle}]{Vertical}[/] ");
        AnsiConsole.Markup(line);
        var plainLen = Markup.Remove(line).Length;
        var padding = Math.Max(0, Console.WindowWidth - 4 - plainLen);
        Console.Write(new string(' ', padding));
        AnsiConsole.Markup($" [{BorderStyle}]{Vertical}[/]");
    }

    // ── Main Menu (logo + focusable command bar, no list) ───────────

    /// <summary>
    /// Renders the main dashboard: logo in the content area, navigation only via the command bar.
    /// Returns the selected command's ReturnValue, or -1 on Escape.
    /// </summary>
    public static int ShowMainMenu(List<CommandBarItem> commands)
    {
        var barIndex = 0;

        while (true)
        {
            AnsiConsole.Clear();
            Console.CursorVisible = false;
            var width = Console.WindowWidth - 2;

            AnsiConsole.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
            RenderPanelLine("[bold orange1]TIGER[/]");
            AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");

            RenderLogo();

            AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
            var barRow = Console.CursorTop;
            RenderPanelLine(BuildCommandBarMarkup(commands, barIndex, true));
            AnsiConsole.MarkupLine($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");

            while (true)
            {
                var key = Console.ReadKey(true);

                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                        barIndex = (barIndex - 1 + commands.Count) % commands.Count;
                        RenderCommandBarAt(barRow, commands, barIndex, true);
                        continue;
                    case ConsoleKey.RightArrow:
                        barIndex = (barIndex + 1) % commands.Count;
                        RenderCommandBarAt(barRow, commands, barIndex, true);
                        continue;
                    case ConsoleKey.Enter:
                        Console.CursorVisible = true;
                        return commands[barIndex].ReturnValue;
                    case ConsoleKey.Escape:
                        Console.CursorVisible = true;
                        return -1;
                    default:
                        // Check hotkeys
                        for (var i = 0; i < commands.Count; i++)
                        {
                            if (key.Key == commands[i].Hotkey)
                            {
                                Console.CursorVisible = true;
                                return commands[i].ReturnValue;
                            }
                        }
                        continue;
                }
            }
        }
    }

    // ── Select in Panel (with focusable command bar) ────────────────

    /// <summary>
    /// List selection with a focusable command bar. Tab switches focus.
    /// Returns list index on Enter (list focused), command ReturnValue on Enter (bar focused),
    /// or -1 on Escape.
    /// </summary>
    public static int SelectInPanel(string[] breadcrumbs, string? context, List<string> items,
        List<CommandBarItem> commands, int pageSize = 0,
        int startIndex = 0, HashSet<int>? skipIndices = null, Action? renderAboveList = null)
    {
        if (items.Count == 0)
        {
            return -1;
        }

        // Calculate available page size dynamically to ensure command bar stays visible.
        // Reserve rows: top border(1) + breadcrumb(1) + context(0-1) + separator(1)
        //   + position indicator(0-1) + separator(1) + command bar(1) + bottom border(1) = 6-8
        var headerRows = 3 + (context is not null ? 1 : 0); // top border + breadcrumb + context + separator
        var footerRows = 3; // separator + command bar + bottom border
        var reservedIndicatorRows = 1; // position indicator when items > visible
        var maxPageSize = Math.Max(5, Console.WindowHeight - headerRows - footerRows - reservedIndicatorRows);

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
        var listStartRow = -1;
        var barRow = -1;
        var barFocused = false;
        var barIndex = 0;
        var needsFullRedraw = true;

        while (true)
        {
            if (needsFullRedraw)
            {
                AnsiConsole.Clear();
                Console.CursorVisible = false;
                var width = Console.WindowWidth - 2;

                AnsiConsole.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
                var crumbText = string.Join($" {Separator} ", breadcrumbs);
                RenderPanelLine($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");

                if (context is not null)
                {
                    RenderPanelLine(context);
                }

                AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
                renderAboveList?.Invoke();

                listStartRow = Console.CursorTop;

                for (var i = 0; i < visibleCount; i++)
                {
                    var idx = scrollOffset + i;
                    if (idx >= items.Count)
                    {
                        RenderEmptyLine();
                        continue;
                    }

                    if (!barFocused && idx == selected)
                    {
                        RenderPanelLine($"[blue]▸[/] {items[idx]}");
                    }
                    else
                    {
                        RenderPanelLine($"  {items[idx]}");
                    }
                }

                if (items.Count > visibleCount)
                {
                    RenderPanelLine($"[dim]({selected + 1}/{items.Count})[/]");
                }

                AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
                barRow = Console.CursorTop;
                RenderPanelLine(BuildCommandBarMarkup(commands, barIndex, barFocused));
                AnsiConsole.MarkupLine($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");

                needsFullRedraw = false;
            }

            var key = Console.ReadKey(true);
            var prevSelected = selected;
            var prevScrollOffset = scrollOffset;

            if (key.Key == ConsoleKey.Tab)
            {
                if (commands.Count == 0)
                {
                    continue;
                }

                barFocused = !barFocused;
                RenderCommandBarAt(barRow, commands, barIndex, barFocused);
                if (listStartRow >= 0 && selected - scrollOffset >= 0 && selected - scrollOffset < visibleCount)
                {
                    RenderListLineAt(listStartRow + (selected - scrollOffset), items[selected], !barFocused);
                }
                continue;
            }

            if (barFocused)
            {
                switch (key.Key)
                {
                    case ConsoleKey.LeftArrow:
                        barIndex = (barIndex - 1 + commands.Count) % commands.Count;
                        RenderCommandBarAt(barRow, commands, barIndex, true);
                        continue;
                    case ConsoleKey.RightArrow:
                        barIndex = (barIndex + 1) % commands.Count;
                        RenderCommandBarAt(barRow, commands, barIndex, true);
                        continue;
                    case ConsoleKey.Enter:
                        Console.CursorVisible = true;
                        return commands[barIndex].ReturnValue;
                    case ConsoleKey.Escape:
                        Console.CursorVisible = true;
                        return -1;
                    default:
                        for (var i = 0; i < commands.Count; i++)
                        {
                            if (key.Key == commands[i].Hotkey)
                            {
                                Console.CursorVisible = true;
                                return commands[i].ReturnValue;
                            }
                        }
                        continue;
                }
            }

            // List navigation
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
                    Console.CursorVisible = true;
                    return selected;
                case ConsoleKey.Escape:
                    Console.CursorVisible = true;
                    return -1;
                default:
                    for (var i = 0; i < commands.Count; i++)
                    {
                        if (key.Key == commands[i].Hotkey)
                        {
                            Console.CursorVisible = true;
                            return commands[i].ReturnValue;
                        }
                    }
                    break;
            }

            if (scrollOffset != prevScrollOffset)
            {
                needsFullRedraw = true;
                continue;
            }

            if (selected != prevSelected && listStartRow >= 0)
            {
                var prevRow = listStartRow + (prevSelected - scrollOffset);
                var newRow = listStartRow + (selected - scrollOffset);

                if (prevSelected - scrollOffset >= 0 && prevSelected - scrollOffset < visibleCount)
                {
                    RenderListLineAt(prevRow, items[prevSelected], false);
                }
                if (selected - scrollOffset >= 0 && selected - scrollOffset < visibleCount)
                {
                    RenderListLineAt(newRow, items[selected], true);
                }

                if (items.Count > visibleCount)
                {
                    var indicatorRow = listStartRow + visibleCount;
                    Console.SetCursorPosition(0, indicatorRow);
                    RenderPanelLine($"[dim]({selected + 1}/{items.Count})[/]");
                }
            }
        }
    }


    // ── Detail Panel ────────────────────────────────────────────────

    /// <summary>
    /// Renders a detail view inside a panel frame with scrollable content.
    /// Content is captured and paginated to fit the terminal. Callers should use
    /// <see cref="HandleDetailScroll"/> in their key loop for scroll support.
    /// </summary>
    public static void RenderDetailPanel(string[] breadcrumbs, string? context, Action renderContent, string hotkeys)
    {
        // Capture content lines
        var contentLines = new List<string>();
        _captureTarget = contentLines;
        try
        {
            renderContent();
        }
        finally
        {
            _captureTarget = null;
        }

        // Store for scroll support
        _lastDetailBreadcrumbs = breadcrumbs;
        _lastDetailContext = context;
        _lastDetailHotkeys = hotkeys;
        _lastDetailLines = contentLines;
        _lastDetailScrollOffset = 0;

        RenderDetailPanelFrame(breadcrumbs, context, contentLines, 0, hotkeys);
    }

    /// <summary>
    /// Handles scroll keys (Up/Down/PageUp/PageDown) for the last rendered detail panel.
    /// Returns true if the key was handled (scrolled), false if the caller should process it.
    /// </summary>
    public static bool HandleDetailScroll(ConsoleKeyInfo key)
    {
        if (_lastDetailLines is null || _lastDetailLines.Count == 0)
        {
            return false;
        }

        var availableHeight = GetDetailAvailableHeight(_lastDetailContext);
        if (_lastDetailLines.Count <= availableHeight)
        {
            return false; // No scrolling needed
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
            RenderDetailPanelFrame(_lastDetailBreadcrumbs!, _lastDetailContext, _lastDetailLines, _lastDetailScrollOffset, _lastDetailHotkeys!);
        }

        return true;
    }

    private static string[]? _lastDetailBreadcrumbs;
    private static string? _lastDetailContext;
    private static string? _lastDetailHotkeys;
    private static List<string>? _lastDetailLines;
    private static int _lastDetailScrollOffset;

    private static int GetDetailAvailableHeight(string? context)
    {
        // Header: top border(1) + breadcrumb(1) + context?(0-1) + separator(1)
        // Footer: separator(1) + hotkeys(1) + bottom border(1)
        var headerRows = 3 + (context is not null ? 1 : 0);
        var footerRows = 3;
        return Math.Max(5, Console.WindowHeight - headerRows - footerRows);
    }

    private static void RenderDetailPanelFrame(string[] breadcrumbs, string? context, List<string> contentLines, int scrollOffset, string hotkeys)
    {
        AnsiConsole.Clear();
        Console.CursorVisible = false;
        var width = Console.WindowWidth - 2;

        // Header
        AnsiConsole.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        var crumbText = string.Join($" {Separator} ", breadcrumbs);
        RenderPanelLineDirect($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");

        if (context is not null)
        {
            RenderPanelLineDirect(context);
        }

        AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");

        // Content
        var availableHeight = GetDetailAvailableHeight(context);
        var visibleLines = contentLines.Skip(scrollOffset).Take(availableHeight).ToList();
        foreach (var line in visibleLines)
        {
            RenderPanelLineDirect(line);
        }

        // Pad remaining space
        for (var i = visibleLines.Count; i < availableHeight; i++)
        {
            RenderEmptyLineDirect();
        }

        // Footer
        AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        var scrollHint = contentLines.Count > availableHeight
            ? $"  [dim]({scrollOffset + 1}-{Math.Min(scrollOffset + availableHeight, contentLines.Count)}/{contentLines.Count} ↑↓)[/]"
            : "";
        RenderPanelLineDirect($"{hotkeys}{scrollHint}");
        AnsiConsole.MarkupLine($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");
        Console.CursorVisible = true;
    }

    // Capture infrastructure for RenderDetailPanel scrolling
    [ThreadStatic]
    private static List<string>? _captureTarget;

    /// <summary>
    /// Renders a single line inside the panel with vertical borders (direct, no capture).
    /// </summary>
    private static void RenderPanelLineDirect(string markupContent)
    {
        AnsiConsole.Markup($"[{BorderStyle}]{Vertical}[/] ");
        AnsiConsole.Markup(markupContent);

        var plainLen = Markup.Remove(markupContent).Length;
        var padding = Math.Max(0, Console.WindowWidth - 4 - plainLen);
        Console.Write(new string(' ', padding));
        AnsiConsole.MarkupLine($" [{BorderStyle}]{Vertical}[/]");
    }

    /// <summary>
    /// Renders an empty line (direct, no capture).
    /// </summary>
    private static void RenderEmptyLineDirect()
    {
        var width = Console.WindowWidth - 2;
        AnsiConsole.Markup($"[{BorderStyle}]{Vertical}[/]");
        Console.Write(new string(' ', width));
        AnsiConsole.MarkupLine($"[{BorderStyle}]{Vertical}[/]");
    }

    // ── Text Prompt ─────────────────────────────────────────────────

    /// <summary>
    /// Prompts for text input inside the panel frame.
    /// </summary>
    public static string? PromptInPanel(string[] breadcrumbs, string prompt, string? currentValue = null)
    {
        AnsiConsole.Clear();
        Console.CursorVisible = true;
        var width = Console.WindowWidth - 2;

        AnsiConsole.MarkupLine($"[{BorderStyle}]{TopLeft}{new string(Horizontal, width)}{TopRight}[/]");
        var crumbText = string.Join($" {Separator} ", breadcrumbs);
        RenderPanelLine($"[bold orange1]TIGER[/] [dim]{Separator}[/] {crumbText}");

        AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");

        RenderPanelLine($"[bold]{prompt}[/]");
        if (currentValue is not null)
        {
            RenderPanelLine($"[dim]Current: {Markup.Escape(currentValue)}[/]");
        }
        RenderEmptyLine();

        AnsiConsole.MarkupLine($"[{BorderStyle}]{MiddleLeft}{new string(Horizontal, width)}{MiddleRight}[/]");
        RenderPanelLine("[blue]Enter[/] Confirm  [blue]Esc[/] Cancel");
        AnsiConsole.MarkupLine($"[{BorderStyle}]{BottomLeft}{new string(Horizontal, width)}{BottomRight}[/]");

        var inputRow = Console.CursorTop - 4;
        Console.SetCursorPosition(4, inputRow);
        AnsiConsole.Markup("[blue]>[/] ");

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
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
                    Console.Write("\b \b");
                }
                continue;
            }
            if (key.KeyChar >= 32)
            {
                buffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
