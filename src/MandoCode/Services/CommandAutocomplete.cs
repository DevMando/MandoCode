using Spectre.Console;
using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Handles command autocomplete with forward slash trigger.
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

    /// <summary>
    /// Gets a command input from the user with autocomplete support.
    /// </summary>
    public static string ReadLineWithAutocomplete()
    {
        var input = new StringBuilder();
        var cursorLeft = Console.CursorLeft;
        var cursorTop = Console.CursorTop;
        var showAutocomplete = false;
        var selectedIndex = 0;
        List<string> filteredCommands = new();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Handle different keys
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        // Auto-complete with selected command
                        input.Clear();
                        input.Append(filteredCommands[selectedIndex]);
                        ClearAutocompleteDisplay(ref cursorTop);
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        AnsiConsole.Markup($"[cyan]{input}[/]");
                        showAutocomplete = false;
                        continue;
                    }
                    else
                    {
                        // Submit the input
                        if (showAutocomplete)
                            ClearAutocompleteDisplay(ref cursorTop);
                        Console.WriteLine();
                        return input.ToString();
                    }

                case ConsoleKey.Tab:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        // Auto-complete with selected command
                        input.Clear();
                        input.Append(filteredCommands[selectedIndex]);
                        ClearAutocompleteDisplay(ref cursorTop);
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        Console.Write(new string(' ', Console.WindowWidth - cursorLeft - 1));
                        Console.SetCursorPosition(cursorLeft, cursorTop);
                        AnsiConsole.Markup($"[cyan]{input}[/]");
                        showAutocomplete = false;
                    }
                    continue;

                case ConsoleKey.UpArrow:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        selectedIndex = selectedIndex > 0 ? selectedIndex - 1 : filteredCommands.Count - 1;
                        DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                    }
                    continue;

                case ConsoleKey.DownArrow:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        selectedIndex = (selectedIndex + 1) % filteredCommands.Count;
                        DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                    }
                    continue;

                case ConsoleKey.Escape:
                    if (showAutocomplete)
                    {
                        ClearAutocompleteDisplay(ref cursorTop);
                        showAutocomplete = false;
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

                        // Update autocomplete
                        if (input.Length > 0 && input[0] == '/')
                        {
                            filteredCommands = FilterCommands(input.ToString());
                            if (filteredCommands.Any())
                            {
                                if (showAutocomplete)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                showAutocomplete = true;
                                selectedIndex = Math.Min(selectedIndex, filteredCommands.Count - 1);
                                DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                if (showAutocomplete)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                showAutocomplete = false;
                            }
                        }
                        else
                        {
                            if (showAutocomplete)
                                ClearAutocompleteDisplay(ref cursorTop);
                            showAutocomplete = false;
                            selectedIndex = 0;
                        }
                    }
                    continue;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        input.Append(key.KeyChar);
                        Console.Write(key.KeyChar);

                        // Check if we should show autocomplete
                        if (input.Length > 0 && input[0] == '/')
                        {
                            filteredCommands = FilterCommands(input.ToString());
                            if (filteredCommands.Any())
                            {
                                if (showAutocomplete)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                showAutocomplete = true;
                                selectedIndex = 0;
                                DisplayAutocomplete(cursorLeft, ref cursorTop, input.Length, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                if (showAutocomplete)
                                    ClearAutocompleteDisplay(ref cursorTop);
                                showAutocomplete = false;
                            }
                        }
                        else
                        {
                            if (showAutocomplete)
                                ClearAutocompleteDisplay(ref cursorTop);
                            showAutocomplete = false;
                        }
                    }
                    break;
            }
        }
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
    /// Displays the autocomplete dropdown.
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
