using Spectre.Console;
using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Handles command autocomplete with forward slash trigger and file autocomplete with @ trigger.
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
        { "/clear", "Clear conversation history" },
        { "/exit", "Exit MandoCode" },
        { "/quit", "Exit MandoCode" }
    };

    private static FileAutocompleteProvider? _fileProvider;

    private enum AutocompleteMode { None, Command, File }

    /// <summary>
    /// Initializes the file autocomplete provider.
    /// </summary>
    public static void Initialize(FileAutocompleteProvider provider)
    {
        _fileProvider = provider;
    }

    /// <summary>
    /// Gets a command input from the user with autocomplete support.
    /// </summary>
    public static string ReadLineWithAutocomplete()
    {
        var input = new StringBuilder();
        var cursorLeft = Console.CursorLeft;
        var cursorTop = Console.CursorTop;
        var autocompleteMode = AutocompleteMode.None;
        var selectedIndex = 0;
        List<string> filteredCommands = new();
        List<string> filteredFiles = new();
        int atAnchorPos = -1;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Handle different keys
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (autocompleteMode == AutocompleteMode.Command && filteredCommands.Any())
                    {
                        // Auto-complete with selected command
                        input.Clear();
                        input.Append(filteredCommands[selectedIndex]);
                        ClearAutocompleteDisplay(ref cursorTop);
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        AnsiConsole.Markup($"[cyan]{input}[/]");
                        autocompleteMode = AutocompleteMode.None;
                        continue;
                    }
                    else if (autocompleteMode == AutocompleteMode.File && filteredFiles.Any())
                    {
                        // Insert selected file path — do NOT submit
                        InsertFileSelection(input, filteredFiles[selectedIndex], atAnchorPos, cursorLeft, ref cursorTop);
                        autocompleteMode = AutocompleteMode.None;
                        atAnchorPos = -1;
                        selectedIndex = 0;
                        continue;
                    }
                    else
                    {
                        // Submit the input
                        if (autocompleteMode != AutocompleteMode.None)
                            ClearAutocompleteDisplay(ref cursorTop);
                        Console.WriteLine();
                        return input.ToString();
                    }

                case ConsoleKey.Tab:
                    if (autocompleteMode == AutocompleteMode.Command && filteredCommands.Any())
                    {
                        // Auto-complete with selected command
                        input.Clear();
                        input.Append(filteredCommands[selectedIndex]);
                        ClearAutocompleteDisplay(ref cursorTop);
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        AnsiConsole.Markup($"[cyan]{input}[/]");
                        autocompleteMode = AutocompleteMode.None;
                    }
                    else if (autocompleteMode == AutocompleteMode.File && filteredFiles.Any())
                    {
                        // Insert selected file path — do NOT submit
                        InsertFileSelection(input, filteredFiles[selectedIndex], atAnchorPos, cursorLeft, ref cursorTop);
                        autocompleteMode = AutocompleteMode.None;
                        atAnchorPos = -1;
                        selectedIndex = 0;
                    }
                    continue;

                case ConsoleKey.UpArrow:
                    if (autocompleteMode == AutocompleteMode.Command && filteredCommands.Any())
                    {
                        selectedIndex = selectedIndex > 0 ? selectedIndex - 1 : filteredCommands.Count - 1;
                        DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                    }
                    else if (autocompleteMode == AutocompleteMode.File && filteredFiles.Any())
                    {
                        selectedIndex = selectedIndex > 0 ? selectedIndex - 1 : filteredFiles.Count - 1;
                        DisplayFileAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredFiles, selectedIndex);
                    }
                    continue;

                case ConsoleKey.DownArrow:
                    if (autocompleteMode == AutocompleteMode.Command && filteredCommands.Any())
                    {
                        selectedIndex = (selectedIndex + 1) % filteredCommands.Count;
                        DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                    }
                    else if (autocompleteMode == AutocompleteMode.File && filteredFiles.Any())
                    {
                        selectedIndex = (selectedIndex + 1) % filteredFiles.Count;
                        DisplayFileAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredFiles, selectedIndex);
                    }
                    continue;

                case ConsoleKey.Escape:
                    if (autocompleteMode != AutocompleteMode.None)
                    {
                        ClearAutocompleteDisplay(ref cursorTop);
                        autocompleteMode = AutocompleteMode.None;
                        atAnchorPos = -1;
                        selectedIndex = 0;
                    }
                    continue;

                case ConsoleKey.Backspace:
                    if (input.Length > 0)
                    {
                        input.Length--;

                        // Update display
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
                        Console.SetCursorPosition(cursorLeft, cursorTop);

                        if (input.Length > 0)
                        {
                            Console.Write(input.ToString());
                        }

                        // Update autocomplete state
                        if (autocompleteMode == AutocompleteMode.File)
                        {
                            if (atAnchorPos >= input.Length)
                            {
                                // Deleted back to or past the @
                                ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.None;
                                atAnchorPos = -1;
                                selectedIndex = 0;
                            }
                            else
                            {
                                // Re-filter with updated fragment
                                var fragment = input.ToString().Substring(atAnchorPos + 1);
                                filteredFiles = _fileProvider?.FilterFiles(fragment) ?? new();
                                if (filteredFiles.Any())
                                {
                                    selectedIndex = Math.Min(selectedIndex, filteredFiles.Count - 1);
                                    DisplayFileAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredFiles, selectedIndex);
                                }
                                else
                                {
                                    ClearAutocompleteDisplay(ref cursorTop);
                                    autocompleteMode = AutocompleteMode.None;
                                    atAnchorPos = -1;
                                    selectedIndex = 0;
                                }
                            }
                        }
                        else if (input.Length > 0 && input[0] == '/')
                        {
                            filteredCommands = FilterCommands(input.ToString());
                            if (filteredCommands.Any())
                            {
                                if (autocompleteMode != AutocompleteMode.None)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.Command;
                                selectedIndex = Math.Min(selectedIndex, filteredCommands.Count - 1);
                                DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                if (autocompleteMode != AutocompleteMode.None)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.None;
                            }
                        }
                        else
                        {
                            if (autocompleteMode != AutocompleteMode.None)
                                ClearAutocompleteDisplay(ref cursorTop);
                            autocompleteMode = AutocompleteMode.None;
                            selectedIndex = 0;
                        }
                    }
                    continue;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        input.Append(key.KeyChar);
                        Console.Write(key.KeyChar);

                        // Check for @ trigger: @ preceded by space or at position 0
                        if (key.KeyChar == '@' && _fileProvider != null
                            && autocompleteMode != AutocompleteMode.File
                            && (input.Length == 1 || input[input.Length - 2] == ' '))
                        {
                            atAnchorPos = input.Length - 1;
                            filteredFiles = _fileProvider.FilterFiles("");
                            if (filteredFiles.Any())
                            {
                                if (autocompleteMode != AutocompleteMode.None)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.File;
                                selectedIndex = 0;
                                DisplayFileAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredFiles, selectedIndex);
                            }
                        }
                        else if (autocompleteMode == AutocompleteMode.File && atAnchorPos >= 0)
                        {
                            // Typing after @: filter files
                            var fragment = input.ToString().Substring(atAnchorPos + 1);
                            filteredFiles = _fileProvider?.FilterFiles(fragment) ?? new();
                            if (filteredFiles.Any())
                            {
                                ClearAutocompleteDisplay(ref cursorTop);
                                selectedIndex = 0;
                                DisplayFileAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredFiles, selectedIndex);
                            }
                            else
                            {
                                ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.None;
                                atAnchorPos = -1;
                                selectedIndex = 0;
                            }
                        }
                        else if (input.Length > 0 && input[0] == '/')
                        {
                            // Command autocomplete
                            filteredCommands = FilterCommands(input.ToString());
                            if (filteredCommands.Any())
                            {
                                if (autocompleteMode != AutocompleteMode.None)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.Command;
                                selectedIndex = 0;
                                DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                if (autocompleteMode != AutocompleteMode.None)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                autocompleteMode = AutocompleteMode.None;
                            }
                        }
                        else if (autocompleteMode == AutocompleteMode.Command)
                        {
                            // Was in command mode but input no longer starts with /
                            ClearAutocompleteDisplay(ref cursorTop);
                            autocompleteMode = AutocompleteMode.None;
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Inserts the selected file path into the input, replacing the @fragment.
    /// </summary>
    private static void InsertFileSelection(StringBuilder input, string selectedPath, int atAnchorPos, int cursorLeft, ref int cursorTop)
    {
        // Replace everything from @ onward with @selectedPath
        var before = input.ToString().Substring(0, atAnchorPos);
        input.Clear();
        input.Append(before);
        input.Append('@');
        input.Append(selectedPath);

        // Redraw
        ClearAutocompleteDisplay(ref cursorTop);
        Console.SetCursorPosition(cursorLeft, cursorTop);
        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
        Console.SetCursorPosition(cursorLeft, cursorTop);
        Console.Write(input.ToString());
    }

    /// <summary>
    /// Filters commands based on the current input.
    /// </summary>
    private static List<string> FilterCommands(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Commands.Keys.ToList();

        var query = input.ToLower();
        return Commands.Keys
            .Where(cmd => cmd.ToLower().StartsWith(query))
            .ToList();
    }

    /// <summary>
    /// Ensures the console buffer has enough rows below cursorTop for the dropdown.
    /// If not, scrolls the buffer and adjusts cursorTop accordingly.
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
    private static void DisplayAutocomplete(int cursorLeft, ref int cursorTop, int inputLength, List<string> commands, int selectedIndex)
    {
        var totalLines = commands.Count + 3; // header + commands + footer + help

        // Ensure enough buffer space so drawing won't cause terminal scrolling
        EnsureBufferSpace(ref cursorTop, totalLines);

        // Clear everything below the input line
        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");

        // Draw the dropdown
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
        // Use Markup (no trailing newline) on the last line to avoid an extra scroll
        AnsiConsole.Markup("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        // Restore cursor to the input line
        Console.SetCursorPosition(cursorLeft + inputLength, cursorTop);
    }

    /// <summary>
    /// Displays the file autocomplete dropdown.
    /// </summary>
    private static void DisplayFileAutocomplete(int cursorLeft, ref int cursorTop, int inputLength, List<string> files, int selectedIndex)
    {
        var totalLines = files.Count + 3; // header + files + footer + help

        EnsureBufferSpace(ref cursorTop, totalLines);

        // Clear everything below the input line
        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");

        // Draw the dropdown
        Console.SetCursorPosition(0, cursorTop + 1);
        AnsiConsole.MarkupLine("[dim]┌─ Files ────────────────────────────────────────[/]");

        for (int i = 0; i < files.Count; i++)
        {
            var filePath = files[i];
            var fileName = Path.GetFileName(filePath);
            var dirPath = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";

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

        AnsiConsole.MarkupLine("[dim]└────────────────────────────────────────────────[/]");
        AnsiConsole.Markup("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        // Restore cursor to the input line
        Console.SetCursorPosition(cursorLeft + inputLength, cursorTop);
    }

    /// <summary>
    /// Clears the autocomplete display below the input line.
    /// </summary>
    private static void ClearAutocompleteDisplay(ref int cursorTop)
    {
        // Clear everything below the input line using ANSI escape
        Console.SetCursorPosition(0, cursorTop + 1);
        Console.Write("\x1b[J");

        // Move cursor back to the input line
        Console.SetCursorPosition(0, cursorTop);
    }

    /// <summary>
    /// Checks if the input is a valid command (starts with /).
    /// </summary>
    public static bool IsCommand(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && input.TrimStart().StartsWith('/');
    }

    /// <summary>
    /// Gets the command without the forward slash.
    /// </summary>
    public static string GetCommandName(string input)
    {
        if (!IsCommand(input)) return input;
        return input.TrimStart().Substring(1).ToLower();
    }

    /// <summary>
    /// Gets all available commands.
    /// </summary>
    public static IEnumerable<string> GetAllCommands()
    {
        return Commands.Keys;
    }
}
