using Spectre.Console;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Thin orchestrator: wires ConsoleInputReader → InputStateMachine → rendering.
/// All state logic lives in InputStateMachine; this class owns console I/O only.
/// </summary>
public static class CommandAutocomplete
{
    /// <summary>
    /// Available commands with their descriptions.
    /// </summary>
    private static readonly Dictionary<string, string> Commands = new()
    {
        { "/help", "Show this help message" },
        { "/config", "Open configuration menu" },
        { "/copy", "Copy last AI response to clipboard" },
        { "/copy-code", "Copy code blocks from last AI response" },
        { "/command", "Run a shell command (also: !<cmd>)" },
        { "/clear", "Clear conversation history" },
        { "/learn", "Learn about LLMs and local AI models" },
        { "/retry", "Retry Ollama connection" },
        { "/music", "Play music" },
        { "/music-stop", "Stop music playback" },
        { "/music-pause", "Pause/resume music" },
        { "/music-next", "Skip to next track" },
        { "/music-vol", "Set volume (0-100), e.g. /music-vol 70" },
        { "/music-playlist", "Select a genre and start playing" },
        { "/music-list", "Show available tracks" },
        { "/exit", "Exit MandoCode" }
    };

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

        Console.SetCursorPosition(0, cursorTop + 1);
        AnsiConsole.MarkupLine("[dim]┌─ Commands ─────────────────────────────────────[/]");

        for (int i = 0; i < commands.Count; i++)
        {
            var cmd = commands[i];
            var description = Commands.ContainsKey(cmd) ? Commands[cmd] : "";

            if (i == selectedIndex)
            {
                AnsiConsole.MarkupLine($"[black on cyan]│ {cmd,-15} {description,-30}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]│[/] [cyan]{cmd,-15}[/] [dim]{description,-30}[/]");
            }
        }

        AnsiConsole.MarkupLine("[dim]└────────────────────────────────────────────────[/]");
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

        Console.SetCursorPosition(0, cursorTop + 1);
        AnsiConsole.MarkupLine("[dim]┌─ Files ────────────────────────────────────────[/]");

        for (int i = 0; i < files.Count; i++)
        {
            var entryPath = files[i];
            var isDirectory = entryPath.EndsWith('/');

            var displayEntry = browsePrefix.Length > 0 && entryPath.StartsWith(browsePrefix, StringComparison.OrdinalIgnoreCase)
                ? entryPath.Substring(browsePrefix.Length)
                : entryPath;

            if (isDirectory)
            {
                var trimmed = displayEntry.TrimEnd('/');
                var dirName = trimmed.Contains('/')
                    ? trimmed.Substring(trimmed.LastIndexOf('/') + 1)
                    : trimmed;
                var parentPath = trimmed.Contains('/')
                    ? trimmed.Substring(0, trimmed.LastIndexOf('/'))
                    : "";

                if (i == selectedIndex)
                {
                    var display = string.IsNullOrEmpty(parentPath)
                        ? $"{dirName}/"
                        : $"{dirName}/  {parentPath}/";
                    AnsiConsole.MarkupLine($"[black on cyan]│ {display,-46}[/]");
                }
                else
                {
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        AnsiConsole.MarkupLine($"[dim]│[/] [cyan]{Markup.Escape(dirName)}/[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]│[/] [cyan]{Markup.Escape(dirName)}/[/]  [dim]{Markup.Escape(parentPath)}/[/]");
                    }
                }
            }
            else
            {
                var fileName = Path.GetFileName(displayEntry);
                var dirPath = Path.GetDirectoryName(displayEntry)?.Replace('\\', '/') ?? "";

                if (i == selectedIndex)
                {
                    var display = string.IsNullOrEmpty(dirPath)
                        ? fileName
                        : $"{fileName}  {dirPath}/";
                    AnsiConsole.MarkupLine($"[black on yellow]│ {display,-46}[/]");
                }
                else
                {
                    if (string.IsNullOrEmpty(dirPath))
                    {
                        AnsiConsole.MarkupLine($"[dim]│[/] [yellow]{Markup.Escape(fileName),-46}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]│[/] [yellow]{Markup.Escape(fileName)}[/]  [dim]{Markup.Escape(dirPath)}/[/]");
                    }
                }
            }
        }

        AnsiConsole.MarkupLine("[dim]└────────────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        SetCursorToPos(cursorLeft, cursorTop, cursorPos);
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
