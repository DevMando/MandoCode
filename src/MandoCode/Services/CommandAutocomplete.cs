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
                        ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
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
                        ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
                        Console.WriteLine();
                        return input.ToString();
                    }

                case ConsoleKey.Tab:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        // Auto-complete with selected command
                        input.Clear();
                        input.Append(filteredCommands[selectedIndex]);
                        ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
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
                        DisplayAutocomplete(cursorLeft, cursorTop, filteredCommands, selectedIndex);
                    }
                    continue;

                case ConsoleKey.DownArrow:
                    if (showAutocomplete && filteredCommands.Any())
                    {
                        selectedIndex = (selectedIndex + 1) % filteredCommands.Count;
                        DisplayAutocomplete(cursorLeft, cursorTop, filteredCommands, selectedIndex);
                    }
                    continue;

                case ConsoleKey.Escape:
                    if (showAutocomplete)
                    {
                        ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
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
                                showAutocomplete = true;
                                selectedIndex = Math.Min(selectedIndex, filteredCommands.Count - 1);
                                DisplayAutocomplete(cursorLeft, cursorTop, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
                                showAutocomplete = false;
                            }
                        }
                        else
                        {
                            if (showAutocomplete)
                            {
                                ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
                            }
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
                                showAutocomplete = true;
                                selectedIndex = 0;
                                DisplayAutocomplete(cursorLeft, cursorTop, filteredCommands, selectedIndex);
                            }
                            else
                            {
                                if (showAutocomplete)
                                {
                                    ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
                                }
                                showAutocomplete = false;
                            }
                        }
                        else
                        {
                            if (showAutocomplete)
                            {
                                ClearAutocompleteDisplay(cursorLeft, cursorTop, filteredCommands.Count);
                            }
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
    /// Displays the autocomplete dropdown.
    /// </summary>
    private static void DisplayAutocomplete(int cursorLeft, int cursorTop, List<string> commands, int selectedIndex)
    {
        // Save current position
        var currentLeft = Console.CursorLeft;
        var currentTop = Console.CursorTop;

        // Move to position below input
        Console.SetCursorPosition(0, cursorTop + 1);

        // Clear previous autocomplete area
        for (int i = 0; i < commands.Count + 2; i++)
        {
            Console.Write(new string(' ', Console.WindowWidth - 1));
            if (i < commands.Count + 1)
                Console.WriteLine();
        }

        // Move back to start position
        Console.SetCursorPosition(0, cursorTop + 1);

        // Display header
        AnsiConsole.MarkupLine("[dim]┌─ Commands ─────────────────────────────────────[/]");

        // Display commands
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
        AnsiConsole.MarkupLine("[dim]↑↓: Navigate  TAB/Enter: Select  ESC: Cancel[/]");

        // Restore cursor position
        Console.SetCursorPosition(currentLeft, currentTop);
    }

    /// <summary>
    /// Clears the autocomplete display.
    /// </summary>
    private static void ClearAutocompleteDisplay(int cursorLeft, int cursorTop, int commandCount)
    {
        if (commandCount == 0) return;

        var currentLeft = Console.CursorLeft;
        var currentTop = Console.CursorTop;

        // Clear the autocomplete area (commands + header + footer + help)
        Console.SetCursorPosition(0, cursorTop + 1);
        for (int i = 0; i < commandCount + 3; i++)
        {
            Console.Write(new string(' ', Console.WindowWidth - 1));
            if (i < commandCount + 2)
                Console.WriteLine();
        }

        // Restore cursor
        Console.SetCursorPosition(currentLeft, currentTop);
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
