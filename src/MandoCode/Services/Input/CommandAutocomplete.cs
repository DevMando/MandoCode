using Spectre.Console;
using Spectre.Console.Rendering;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Thin orchestrator: wires ConsoleInputReader → InputStateMachine → rendering.
/// All state logic lives in InputStateMachine; this class owns console I/O only.
/// </summary>
public static class CommandAutocomplete
{
    /// <summary>
    /// Available commands with their descriptions. Sourced from
    /// <see cref="SlashCommands.All"/> so the state machine and autocomplete
    /// renderer can't drift.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Commands = SlashCommands.All;

    private static InputStateMachine? _stateMachine;
    private static ConsoleInputReader _keySource = new();

    /// <summary>
    /// Initializes with a DI-provided InputStateMachine instance.
    /// </summary>
    public static void Initialize(InputStateMachine stateMachine)
    {
        _stateMachine = stateMachine;
    }

    /// <summary>
    /// Clears the command history.
    /// </summary>
    public static void ClearHistory()
    {
        _stateMachine?.ClearHistory();
    }

    /// <summary>
    /// Gets a command input from the user with autocomplete support.
    /// Reads keys via ConsoleInputReader, delegates logic to InputStateMachine,
    /// and renders based on the returned InputAction.
    /// </summary>
    public static string ReadLineWithAutocomplete()
    {
        var sm = _stateMachine ?? throw new InvalidOperationException(
            "CommandAutocomplete.Initialize() must be called before ReadLineWithAutocomplete().");

        sm.BeginNewInput();
        var cursorLeft = Console.CursorLeft;
        var cursorTop = Console.CursorTop;

        return RunAutocompleteLoop(sm, cursorLeft, cursorTop) ?? "";
    }

    /// <summary>
    /// Continues autocomplete from the current state machine state (no reset).
    /// Used when VDOM TextInput has already set up the input text and autocomplete mode.
    /// Renders the current state first, then enters the key loop.
    /// Returns null if autocomplete was dismissed (user erased trigger) — caller should
    /// return to VDOM input with the remaining text.
    /// </summary>
    public static string? ContinueAutocomplete()
    {
        var sm = _stateMachine ?? throw new InvalidOperationException(
            "CommandAutocomplete.Initialize() must be called before ContinueAutocomplete().");

        // Wait for RazorConsole to fully release stdin after VDOM teardown
        Thread.Sleep(150);

        // Process any buffered keyboard chars through the state machine.
        // When the user types "/music" fast, TextInput only captures "/"
        // before being removed from VDOM. The remaining "music" chars are
        // in the stdin buffer — feed them to the state machine instead of flushing.
        while (Console.KeyAvailable)
        {
            var bufferedKey = Console.ReadKey(intercept: true);
            sm.ProcessKey(bufferedKey);
        }

        // Snapshot the correct state BEFORE any rendering
        var correctInput = sm.State.InputText;
        var correctMode = sm.State.Mode;
        var correctItems = sm.State.DropdownItems;
        var correctIndex = sm.State.SelectedIndex;
        var correctCursorPos = sm.State.CursorPos;
        var correctPrefix = sm.State.BrowsePrefix;

        // Hard reset terminal state — VDOM teardown leaves foreground invisible
        Console.ResetColor();
        Console.Write("\x1b[0m\x1b[?25h"); // Reset attrs + ensure cursor visible
        var cursorLeft = Console.CursorLeft;
        var cursorTop = Console.CursorTop;

        // Clear any VDOM remnants from the line
        Console.SetCursorPosition(0, cursorTop);
        Console.Write("\x1b[2K"); // Clear entire current line
        Console.SetCursorPosition(cursorLeft, cursorTop);

        // Write input text with explicit white foreground
        Console.Write($"\x1b[37m{correctInput}\x1b[0m");

        // Render dropdown after the input text
        if (correctMode == AutocompleteMode.Command && correctItems.Count > 0)
            DisplayAutocomplete(cursorLeft, ref cursorTop, correctCursorPos,
                correctItems, correctIndex);
        else if (correctMode == AutocompleteMode.File && correctItems.Count > 0)
            DisplayFileAutocomplete(cursorLeft, ref cursorTop, correctCursorPos,
                correctItems, correctIndex, correctPrefix);

        return RunAutocompleteLoop(sm, cursorLeft, cursorTop, returnOnDismiss: true);
    }

    private static string? RunAutocompleteLoop(InputStateMachine sm, int cursorLeft, int cursorTop, bool returnOnDismiss = false)
    {

        while (true)
        {
            var key = _keySource.ReadKey();

            InputAction action;

            // Paste detection: if more keys are immediately available, batch them
            if (!char.IsControl(key.KeyChar) && _keySource.KeyAvailable)
            {
                var buffered = new List<ConsoleKeyInfo>();
                while (_keySource.KeyAvailable)
                    buffered.Add(_keySource.ReadKey());

                action = sm.ProcessPaste(key, buffered);
            }
            else
            {
                action = sm.ProcessKey(key);
            }

            var state = sm.State;

            switch (action)
            {
                case InputAction.Noop:
                    break;

                case InputAction.Redraw:
                    RedrawInput(state.InputText, cursorLeft, ref cursorTop, state.CursorPos);
                    if (state.Mode == AutocompleteMode.Command && state.DropdownItems.Count > 0)
                        DisplayAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                            state.DropdownItems, state.SelectedIndex);
                    else if (state.Mode == AutocompleteMode.File && state.DropdownItems.Count > 0)
                        DisplayFileAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                            state.DropdownItems, state.SelectedIndex, state.BrowsePrefix);
                    else if (returnOnDismiss && state.Mode == AutocompleteMode.None
                             && !HasTriggerChar(state.InputText))
                    {
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write("\x1b[J");
                        return null;
                    }
                    break;

                case InputAction.AppendChar:
                    // Optimized path: just write the char, no full redraw
                    Console.Write(state.LastAppendedChar);
                    // But still need to show dropdown if autocomplete triggered
                    if (state.Mode == AutocompleteMode.Command && state.DropdownItems.Count > 0)
                        DisplayAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                            state.DropdownItems, state.SelectedIndex);
                    else if (state.Mode == AutocompleteMode.File && state.DropdownItems.Count > 0)
                        DisplayFileAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                            state.DropdownItems, state.SelectedIndex, state.BrowsePrefix);
                    else if (state.Mode == AutocompleteMode.None)
                    {
                        ClearAutocompleteDisplay(ref cursorTop);
                        if (returnOnDismiss && !HasTriggerChar(state.InputText))
                        {
                            Console.SetCursorPosition(cursorLeft, cursorTop);
                            Console.Write("\x1b[J");
                            return null;
                        }
                        SetCursorToPos(cursorLeft, cursorTop, state.CursorPos);
                    }
                    break;

                case InputAction.CursorMoved:
                    SetCursorToPos(cursorLeft, cursorTop, state.CursorPos);
                    break;

                case InputAction.ShowCommandDropdown:
                    DisplayAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                        state.DropdownItems, state.SelectedIndex);
                    break;

                case InputAction.ShowFileDropdown:
                    DisplayFileAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                        state.DropdownItems, state.SelectedIndex, state.BrowsePrefix);
                    break;

                case InputAction.ClearDropdown:
                    ClearAutocompleteDisplay(ref cursorTop);
                    if (returnOnDismiss && !HasTriggerChar(state.InputText))
                    {
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write("\x1b[J");
                        return null;
                    }
                    SetCursorToPos(cursorLeft, cursorTop, state.CursorPos);
                    break;

                case InputAction.AcceptCommand:
                    ClearAutocompleteDisplay(ref cursorTop);
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - cursorLeft - 1)));
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    if (returnOnDismiss)
                    {
                        Console.Write("\x1b[J");
                        return state.InputText;
                    }
                    AnsiConsole.Markup($"[cyan]{Markup.Escape(state.InputText)}[/]");
                    break;

                case InputAction.AcceptFile:
                    ClearAutocompleteDisplay(ref cursorTop);
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - cursorLeft - 1)));
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    if (returnOnDismiss)
                    {
                        Console.Write("\x1b[J");
                        return state.InputText;
                    }
                    Console.Write(state.InputText);
                    break;

                case InputAction.DrillDirectory:
                    ClearAutocompleteDisplay(ref cursorTop);
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - cursorLeft - 1)));
                    Console.SetCursorPosition(cursorLeft, cursorTop);
                    Console.Write(state.InputText);
                    // Show directory contents dropdown
                    if (state.DropdownItems.Count > 0)
                        DisplayFileAutocomplete(cursorLeft, ref cursorTop, state.CursorPos,
                            state.DropdownItems, state.SelectedIndex, state.BrowsePrefix);
                    break;

                case InputAction.Submit:
                    ClearAutocompleteDisplay(ref cursorTop);
                    if (returnOnDismiss)
                    {
                        // Clear imperative text — VDOM gold echo will render it
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write("\x1b[J");
                    }
                    else
                    {
                        Console.WriteLine();
                    }
                    return state.SubmittedText!;
            }
        }
    }

    // ─── Rendering Methods (console I/O only) ─────────────────

    /// <summary>
    /// Redraws the entire input line and positions the console cursor.
    /// </summary>
    private static void RedrawInput(string inputText, int cursorLeft, ref int cursorTop, int cursorPos)
    {
        var width = Console.WindowWidth;
        var totalChars = cursorLeft + inputText.Length;
        var linesNeeded = (totalChars + width - 1) / width;

        EnsureBufferSpace(ref cursorTop, linesNeeded);

        Console.SetCursorPosition(cursorLeft, cursorTop);
        Console.Write("\x1b[J");
        Console.SetCursorPosition(cursorLeft, cursorTop);
        Console.Write(inputText);

        SetCursorToPos(cursorLeft, cursorTop, cursorPos);
    }

    /// <summary>
    /// Positions the console cursor accounting for line wrapping.
    /// </summary>
    private static void SetCursorToPos(int cursorLeft, int cursorTop, int cursorPos)
    {
        var width = Console.WindowWidth;
        var absolutePos = cursorLeft + cursorPos;
        Console.SetCursorPosition(absolutePos % width, cursorTop + absolutePos / width);
    }

    /// <summary>
    /// Ensures the console buffer has enough rows below cursorTop.
    /// </summary>
    private static void EnsureBufferSpace(ref int cursorTop, int linesNeeded)
    {
        var bufferHeight = Console.BufferHeight;
        var available = bufferHeight - cursorTop - 1;
        if (available < linesNeeded)
        {
            var scrollAmount = linesNeeded - available;
            Console.SetCursorPosition(0, bufferHeight - 1);
            for (int i = 0; i < scrollAmount; i++)
                Console.Write('\n');
            cursorTop -= scrollAmount;
        }
    }

    // Panel geometry shared by both autocomplete dropdowns. Sized to the terminal:
    // wide enough that command descriptions don't truncate on a normal window, but
    // never wider than the console itself, and capped — a 250-col monitor doesn't
    // need a 250-col dropdown. 100 cols fits the longest current description with
    // room to spare. Falls back to the legacy 48-col look when the console size is
    // unavailable (redirected output, some terminal hosts).
    private const int MinAutocompletePanelWidth = 48;
    private const int MaxAutocompletePanelWidth = 100;

    private static int AutocompletePanelWidth
    {
        get
        {
            try { return Math.Clamp(Console.WindowWidth - 2, MinAutocompletePanelWidth, MaxAutocompletePanelWidth); }
            catch { return MinAutocompletePanelWidth; }
        }
    }

    // Inner content area: panel width minus the 2-col border and 2-col padding.
    private static int AutocompleteContentWidth => AutocompletePanelWidth - 4;

    /// <summary>
    /// Displays the command autocomplete dropdown.
    /// </summary>
    private static void DisplayAutocomplete(int cursorLeft, ref int cursorTop, int cursorPos,
        IReadOnlyList<string> commands, int selectedIndex)
    {
        var totalLines = commands.Count + 3;
        EnsureBufferSpace(ref cursorTop, totalLines);

        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");

        // Command column sizes to the longest VISIBLE command (14-col floor keeps the
        // legacy alignment) so long names like /music-playlist never truncate; the
        // description gets everything that's left.
        var cmdCol = Math.Max(14, commands.Count > 0 ? commands.Max(c => c.Length) : 14);
        var descCol = AutocompleteContentWidth - cmdCol - 1;

        var rows = new List<IRenderable>();
        for (int i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            var rawDescription = Commands.ContainsKey(cmd) ? Commands[cmd] : "";

            var cmdField = FitVisible(cmd, cmdCol);
            var descField = FitVisible(rawDescription, descCol);

            if (i == selectedIndex)
            {
                // Single black-on-cyan span covers the full inner width so the
                // highlight reaches both panel padding edges.
                var combined = $"{cmdField} {descField}";
                rows.Add(new Markup($"[black on cyan]{Markup.Escape(combined)}[/]"));
            }
            else
            {
                rows.Add(new Markup($"[cyan]{Markup.Escape(cmdField)}[/] [dim]{Markup.Escape(descField)}[/]"));
            }
        }

        WriteAutocompletePanel(cursorTop, "[cyan] Commands [/]", rows);

        Console.SetCursorPosition(0, cursorTop + commands.Count + 3);
        AnsiConsole.Markup("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        SetCursorToPos(cursorLeft, cursorTop, cursorPos);
    }

    /// <summary>
    /// Displays the file autocomplete dropdown.
    /// </summary>
    private static void DisplayFileAutocomplete(int cursorLeft, ref int cursorTop, int cursorPos,
        IReadOnlyList<string> files, int selectedIndex, string browsePrefix = "")
    {
        var totalLines = files.Count + 3;
        EnsureBufferSpace(ref cursorTop, totalLines);

        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");

        var rows = new List<IRenderable>();
        for (int i = 0; i < files.Count; i++)
        {
            var entryPath = files[i];
            var isDirectory = entryPath.EndsWith('/');

            var displayEntry = browsePrefix.Length > 0 && entryPath.StartsWith(browsePrefix, StringComparison.OrdinalIgnoreCase)
                ? entryPath.Substring(browsePrefix.Length)
                : entryPath;

            string primary;
            string secondary;
            string accent;

            if (isDirectory)
            {
                var trimmed = displayEntry.TrimEnd('/');
                var slashIdx = trimmed.LastIndexOf('/');
                var dirName = slashIdx >= 0 ? trimmed.Substring(slashIdx + 1) : trimmed;
                var parentPath = slashIdx >= 0 ? trimmed.Substring(0, slashIdx) : "";

                primary = $"{dirName}/";
                secondary = string.IsNullOrEmpty(parentPath) ? "" : $"{parentPath}/";
                accent = "cyan";
            }
            else
            {
                var fileName = Path.GetFileName(displayEntry);
                var dirPath = Path.GetDirectoryName(displayEntry)?.Replace('\\', '/') ?? "";

                primary = fileName;
                secondary = string.IsNullOrEmpty(dirPath) ? "" : $"{dirPath}/";
                accent = "yellow";
            }

            rows.Add(BuildFileRow(primary, secondary, accent, i == selectedIndex));
        }

        WriteAutocompletePanel(cursorTop, "[cyan] Files [/]", rows);

        Console.SetCursorPosition(0, cursorTop + files.Count + 3);
        AnsiConsole.Markup("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        SetCursorToPos(cursorLeft, cursorTop, cursorPos);
    }

    /// <summary>
    /// Builds a two-part file/dir row that truncates the secondary path with
    /// an ellipsis when the combined visible length would exceed the panel.
    /// </summary>
    private static Markup BuildFileRow(string primary, string secondary, string accent, bool selected)
    {
        var width = AutocompleteContentWidth;

        if (string.IsNullOrEmpty(secondary))
        {
            var fitted = FitVisible(primary, width);
            return selected
                ? new Markup($"[black on {accent}]{Markup.Escape(fitted)}[/]")
                : new Markup($"[{accent}]{Markup.Escape(fitted)}[/]" + new string(' ', width - fitted.Length));
        }

        // Reserve space for "  " separator between primary and secondary.
        const string sep = "  ";
        var primaryFit = primary.Length > width - sep.Length - 1
            ? primary.Substring(0, Math.Max(0, width - sep.Length - 2)) + "…"
            : primary;
        var available = width - primaryFit.Length - sep.Length;
        var secondaryFit = secondary.Length > available
            ? (available > 0 ? secondary.Substring(0, available - 1) + "…" : "")
            : secondary;

        if (selected)
        {
            var combined = $"{primaryFit}{sep}{secondaryFit}";
            var padded = combined + new string(' ', Math.Max(0, width - combined.Length));
            return new Markup($"[black on {accent}]{Markup.Escape(padded)}[/]");
        }

        var primaryMarkup = $"[{accent}]{Markup.Escape(primaryFit)}[/]";
        var secondaryMarkup = secondaryFit.Length > 0
            ? $"[dim]{Markup.Escape(secondaryFit)}[/]"
            : "";
        return new Markup($"{primaryMarkup}{sep}{secondaryMarkup}");
    }

    /// <summary>
    /// Truncates with an ellipsis or right-pads to exactly <paramref name="width"/> visible columns.
    /// </summary>
    private static string FitVisible(string text, int width)
    {
        if (width <= 0) return string.Empty;
        if (text.Length > width) return text.Substring(0, width - 1) + "…";
        return text + new string(' ', width - text.Length);
    }

    /// <summary>
    /// Renders a Spectre Panel with the same rounded/dim styling as fenced code
    /// blocks, then writes each line at the cursor row below the input. Using
    /// SetCursorPosition per line preserves the in-place redraw behavior the
    /// autocomplete loop relies on.
    /// </summary>
    private static void WriteAutocompletePanel(int cursorTop, string headerMarkup, IReadOnlyList<IRenderable> rows)
    {
        // Capture the width once so the panel and the capture profile agree even if
        // the terminal is resized mid-render.
        var panelWidth = AutocompletePanelWidth;

        var content = rows.Count == 1 ? rows[0] : new Rows(rows);
        var panel = new Panel(content)
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .Padding(1, 0);
        panel.Header = new PanelHeader(headerMarkup, Justify.Left);
        panel.Width = panelWidth;

        var sw = new StringWriter();
        var captureConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(sw),
        });
        captureConsole.Profile.Width = panelWidth + 4;
        captureConsole.Write(panel);

        var lines = sw.ToString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Console.SetCursorPosition(0, cursorTop + 1 + i);
            Console.Write(lines[i]);
        }
    }

    /// <summary>
    /// Clears the autocomplete display below the input line.
    /// </summary>
    private static void ClearAutocompleteDisplay(ref int cursorTop)
    {
        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");
        Console.SetCursorPosition(0, cursorTop);
    }

    /// <summary>
    /// Checks if the input text still contains an autocomplete trigger (@ or /).
    /// Used to decide whether to exit back to VDOM or stay in the imperative loop.
    /// </summary>
    private static bool HasTriggerChar(string input)
    {
        return input.Contains('@') || input.TrimStart().StartsWith('/');
    }

    // ─── Public API (delegated to state machine) ──────────────

    /// <summary>
    /// Checks if the input is a valid command (starts with /).
    /// </summary>
    public static bool IsCommand(string input) => InputStateMachine.IsCommand(input);

    /// <summary>
    /// Gets the command without the forward slash.
    /// </summary>
    public static string GetCommandName(string input) => InputStateMachine.GetCommandName(input);

    /// <summary>
    /// Gets all available commands.
    /// </summary>
    public static IEnumerable<string> GetAllCommands() => Commands.Keys;
}
