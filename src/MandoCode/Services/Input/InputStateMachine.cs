using System.Text;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Pure input state machine — zero Console calls.
/// Processes key events, mutates state, returns actions.
/// All autocomplete logic, history navigation, cursor tracking,
/// and text editing live here.
/// </summary>
public class InputStateMachine
{
    private readonly Dictionary<string, string> _commands;
    private readonly FileAutocompleteProvider? _fileProvider;

    // Input state
    private readonly StringBuilder _input = new();
    private int _cursorPos;

    // Autocomplete state
    private AutocompleteMode _mode = AutocompleteMode.None;
    private int _selectedIndex;
    private List<string> _filteredCommands = new();
    private List<string> _filteredFiles = new();
    private int _atAnchorPos = -1;

    // Command history (persists across inputs)
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private string? _savedInput;

    // Render state snapshot
    public InputRenderState State { get; } = new();

    public InputStateMachine(
        Dictionary<string, string> commands,
        FileAutocompleteProvider? fileProvider)
    {
        _commands = commands;
        _fileProvider = fileProvider;
        State.CommandDescriptions = commands;
    }

    /// <summary>
    /// Resets input state for a new prompt. History is preserved.
    /// </summary>
    public void BeginNewInput()
    {
        _input.Clear();
        _cursorPos = 0;
        _mode = AutocompleteMode.None;
        _selectedIndex = 0;
        _filteredCommands.Clear();
        _filteredFiles.Clear();
        _atAnchorPos = -1;
        _historyIndex = -1;
        _savedInput = null;
        SyncState();
    }

    /// <summary>
    /// Processes a single key event and returns the action the renderer should take.
    /// </summary>
    public InputAction ProcessKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                return HandleEnter();

            case ConsoleKey.Tab:
                return HandleTab();

            case ConsoleKey.LeftArrow:
                return HandleLeftArrow();

            case ConsoleKey.RightArrow:
                return HandleRightArrow();

            case ConsoleKey.Home:
                return HandleHome();

            case ConsoleKey.End:
                return HandleEnd();

            case ConsoleKey.UpArrow:
                return HandleUpArrow();

            case ConsoleKey.DownArrow:
                return HandleDownArrow();

            case ConsoleKey.Escape:
                return HandleEscape();

            case ConsoleKey.Delete:
                return HandleDelete();

            case ConsoleKey.Backspace:
                return HandleBackspace();

            default:
                if (!char.IsControl(key.KeyChar))
                    return HandleCharInput(key.KeyChar);
                return InputAction.Noop;
        }
    }

    /// <summary>
    /// Handles paste: processes the first key plus all buffered keys at once.
    /// Called by the orchestrator when it detects KeyAvailable after the first ReadKey.
    /// </summary>
    public InputAction ProcessPaste(ConsoleKeyInfo firstKey, IReadOnlyList<ConsoleKeyInfo> bufferedKeys)
    {
        var chars = new List<char>();

        if (!char.IsControl(firstKey.KeyChar))
            chars.Add(firstKey.KeyChar);

        foreach (var pk in bufferedKeys)
        {
            if (pk.Key == ConsoleKey.Enter || pk.KeyChar == '\n' || pk.KeyChar == '\r')
                chars.Add(' '); // newlines → spaces
            else if (!char.IsControl(pk.KeyChar))
                chars.Add(pk.KeyChar);
        }

        if (chars.Count == 0)
            return InputAction.Noop;

        var text = new string(chars.ToArray());
        _input.Insert(_cursorPos, text);
        _cursorPos += chars.Count;

        // Close any autocomplete if open
        if (_mode != AutocompleteMode.None)
        {
            _mode = AutocompleteMode.None;
            _atAnchorPos = -1;
            _selectedIndex = 0;
        }

        SyncState();
        return InputAction.Redraw;
    }

    /// <summary>Clears command history.</summary>
    public void ClearHistory()
    {
        _history.Clear();
        _historyIndex = -1;
        _savedInput = null;
    }

    public static bool IsCommand(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && input.TrimStart().StartsWith('/');
    }

    public static string GetCommandName(string input)
    {
        if (!IsCommand(input)) return input;
        return input.TrimStart().Substring(1).ToLower();
    }

    public IEnumerable<string> GetAllCommands() => _commands.Keys;

    // ─── VDOM Text-Level API ──────────────────────────────────

    /// <summary>
    /// Updates state based on full text content (VDOM path).
    /// Called by Blazor components when TextInput reports a change.
    /// Determines autocomplete mode and filters items.
    /// </summary>
    public InputAction UpdateText(string text)
    {
        _input.Clear();
        _input.Append(text);
        _cursorPos = text.Length;

        // Check for @ trigger — find the rightmost valid @ anchor
        var atPos = FindAtTrigger(text);
        if (atPos >= 0 && _fileProvider != null)
        {
            _atAnchorPos = atPos;
            var fragment = text.Substring(atPos + 1);
            _filteredFiles = _fileProvider.FilterFiles(fragment);
            if (_filteredFiles.Any())
            {
                _mode = AutocompleteMode.File;
                _selectedIndex = 0;
                SyncState();
                return InputAction.ShowFileDropdown;
            }
        }

        if (text.StartsWith("/"))
        {
            _filteredCommands = FilterCommands(text);
            if (_filteredCommands.Any())
            {
                _mode = AutocompleteMode.Command;
                _selectedIndex = 0;
                SyncState();
                return InputAction.ShowCommandDropdown;
            }
        }

        // No autocomplete triggered
        if (_mode != AutocompleteMode.None)
        {
            _mode = AutocompleteMode.None;
            _atAnchorPos = -1;
            _selectedIndex = 0;
            SyncState();
            return InputAction.ClearDropdown;
        }

        SyncState();
        return InputAction.Redraw;
    }

    /// <summary>
    /// Submits the current text (VDOM path). Adds to history and returns the submitted text.
    /// </summary>
    public string SubmitInput(string text)
    {
        _input.Clear();
        _input.Append(text);
        AddToHistory(text);
        _mode = AutocompleteMode.None;
        _atAnchorPos = -1;
        _selectedIndex = 0;
        State.SubmittedText = text;
        SyncState();
        return text;
    }

    /// <summary>
    /// Accepts the currently selected dropdown item (VDOM path).
    /// Returns the updated input text, or null if no selection was active.
    /// </summary>
    public string? AcceptSelection(string selectedRawPath)
    {
        if (_mode == AutocompleteMode.Command)
        {
            // For commands, selectedRawPath is the command key like "/help"
            _input.Clear();
            _input.Append(selectedRawPath);
            _cursorPos = _input.Length;
            _mode = AutocompleteMode.None;
            SyncState();
            return _input.ToString();
        }

        if (_mode == AutocompleteMode.File)
        {
            if (selectedRawPath.EndsWith('/'))
            {
                // Directory — drill into it
                DrillIntoDirectory(selectedRawPath);
                SyncState();
                return _input.ToString();
            }
            else
            {
                // File — insert selection
                InsertFileSelection(selectedRawPath);
                SyncState();
                return _input.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the last AcceptSelection resulted in a directory drill-down
    /// (dropdown should remain open with new items).
    /// </summary>
    public bool IsInFileDropdown => _mode == AutocompleteMode.File && _filteredFiles.Any();

    // ─── Key Handlers ──────────────────────────────────────────

    private InputAction HandleEnter()
    {
        if (_mode == AutocompleteMode.Command && _filteredCommands.Any())
        {
            AcceptCommandSelection();
            SyncState();
            return InputAction.AcceptCommand;
        }

        if (_mode == AutocompleteMode.File && _filteredFiles.Any())
        {
            var selected = _filteredFiles[_selectedIndex];
            if (selected.EndsWith('/'))
            {
                DrillIntoDirectory(selected);
                SyncState();
                return InputAction.DrillDirectory;
            }
            else
            {
                InsertFileSelection(selected);
                SyncState();
                return InputAction.AcceptFile;
            }
        }

        // Submit
        var wasInDropdown = _mode != AutocompleteMode.None;
        _mode = AutocompleteMode.None;
        var submitted = _input.ToString();
        AddToHistory(submitted);
        State.SubmittedText = submitted;
        SyncState();

        // If dropdown was open, orchestrator needs to clear it before submitting
        return InputAction.Submit;
    }

    private InputAction HandleTab()
    {
        if (_mode == AutocompleteMode.Command && _filteredCommands.Any())
        {
            AcceptCommandSelection();
            SyncState();
            return InputAction.AcceptCommand;
        }

        if (_mode == AutocompleteMode.File && _filteredFiles.Any())
        {
            var selected = _filteredFiles[_selectedIndex];
            if (selected.EndsWith('/'))
            {
                DrillIntoDirectory(selected);
                SyncState();
                return InputAction.DrillDirectory;
            }
            else
            {
                InsertFileSelection(selected);
                SyncState();
                return InputAction.AcceptFile;
            }
        }

        return InputAction.Noop;
    }

    private InputAction HandleLeftArrow()
    {
        if (_cursorPos <= 0)
            return InputAction.Noop;

        var hadDropdown = DismissDropdownIfOpen();
        _cursorPos--;
        SyncState();
        return hadDropdown ? InputAction.ClearDropdown : InputAction.CursorMoved;
    }

    private InputAction HandleRightArrow()
    {
        if (_cursorPos >= _input.Length)
            return InputAction.Noop;

        var hadDropdown = DismissDropdownIfOpen();
        _cursorPos++;
        SyncState();
        return hadDropdown ? InputAction.ClearDropdown : InputAction.CursorMoved;
    }

    private InputAction HandleHome()
    {
        var hadDropdown = DismissDropdownIfOpen();
        _cursorPos = 0;
        SyncState();
        return hadDropdown ? InputAction.ClearDropdown : InputAction.CursorMoved;
    }

    private InputAction HandleEnd()
    {
        var hadDropdown = DismissDropdownIfOpen();
        _cursorPos = _input.Length;
        SyncState();
        return hadDropdown ? InputAction.ClearDropdown : InputAction.CursorMoved;
    }

    private InputAction HandleUpArrow()
    {
        if (_mode == AutocompleteMode.Command && _filteredCommands.Any())
        {
            _selectedIndex = _selectedIndex > 0 ? _selectedIndex - 1 : _filteredCommands.Count - 1;
            SyncState();
            return InputAction.ShowCommandDropdown;
        }

        if (_mode == AutocompleteMode.File && _filteredFiles.Any())
        {
            _selectedIndex = _selectedIndex > 0 ? _selectedIndex - 1 : _filteredFiles.Count - 1;
            SyncState();
            return InputAction.ShowFileDropdown;
        }

        if (_mode == AutocompleteMode.None && _history.Count > 0)
        {
            if (_historyIndex == -1)
            {
                _savedInput = _input.ToString();
                _historyIndex = _history.Count - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }

            _input.Clear();
            _input.Append(_history[_historyIndex]);
            _cursorPos = _input.Length;
            SyncState();
            return InputAction.Redraw;
        }

        return InputAction.Noop;
    }

    private InputAction HandleDownArrow()
    {
        if (_mode == AutocompleteMode.Command && _filteredCommands.Any())
        {
            _selectedIndex = (_selectedIndex + 1) % _filteredCommands.Count;
            SyncState();
            return InputAction.ShowCommandDropdown;
        }

        if (_mode == AutocompleteMode.File && _filteredFiles.Any())
        {
            _selectedIndex = (_selectedIndex + 1) % _filteredFiles.Count;
            SyncState();
            return InputAction.ShowFileDropdown;
        }

        if (_mode == AutocompleteMode.None && _historyIndex >= 0)
        {
            _historyIndex++;
            if (_historyIndex >= _history.Count)
            {
                _historyIndex = -1;
                _input.Clear();
                _input.Append(_savedInput ?? "");
                _savedInput = null;
            }
            else
            {
                _input.Clear();
                _input.Append(_history[_historyIndex]);
            }
            _cursorPos = _input.Length;
            SyncState();
            return InputAction.Redraw;
        }

        return InputAction.Noop;
    }

    private InputAction HandleEscape()
    {
        if (_mode != AutocompleteMode.None)
        {
            DismissDropdownIfOpen();
            SyncState();
            return InputAction.ClearDropdown;
        }
        return InputAction.Noop;
    }

    private InputAction HandleDelete()
    {
        if (_cursorPos >= _input.Length)
            return InputAction.Noop;

        _input.Remove(_cursorPos, 1);
        UpdateAutocompleteAfterEdit();
        SyncState();

        // Orchestrator checks State.Mode to decide if dropdown needs rendering
        return InputAction.Redraw;
    }

    private InputAction HandleBackspace()
    {
        if (_cursorPos <= 0)
            return InputAction.Noop;

        _cursorPos--;
        _input.Remove(_cursorPos, 1);
        UpdateAutocompleteAfterEdit();
        SyncState();

        return InputAction.Redraw;
    }

    private InputAction HandleCharInput(char c)
    {
        _input.Insert(_cursorPos, c);
        _cursorPos++;

        var isAppend = _cursorPos == _input.Length;
        State.LastAppendedChar = c;

        // Check for @ trigger
        if (c == '@' && _fileProvider != null
            && _mode != AutocompleteMode.File
            && (_cursorPos == 1 || (_cursorPos >= 2 && _input[_cursorPos - 2] == ' ')))
        {
            _atAnchorPos = _cursorPos - 1;
            _filteredFiles = _fileProvider.FilterFiles("");
            if (_filteredFiles.Any())
            {
                _mode = AutocompleteMode.File;
                _selectedIndex = 0;
                SyncState();
                return isAppend ? InputAction.AppendChar : InputAction.Redraw;
            }
        }
        else if (_mode == AutocompleteMode.File && _atAnchorPos >= 0)
        {
            // Typing after @: filter files
            var fragment = _input.ToString().Substring(_atAnchorPos + 1);
            _filteredFiles = _fileProvider?.FilterFiles(fragment) ?? new();
            if (_filteredFiles.Any())
            {
                _selectedIndex = 0;
                SyncState();
                return isAppend ? InputAction.AppendChar : InputAction.Redraw;
            }
            else
            {
                _mode = AutocompleteMode.None;
                _atAnchorPos = -1;
                _selectedIndex = 0;
                SyncState();
                return isAppend ? InputAction.AppendChar : InputAction.Redraw;
            }
        }
        else if (_input.Length > 0 && _input[0] == '/')
        {
            // Command autocomplete
            _filteredCommands = FilterCommands(_input.ToString());
            if (_filteredCommands.Any())
            {
                _mode = AutocompleteMode.Command;
                _selectedIndex = 0;
                SyncState();
                return isAppend ? InputAction.AppendChar : InputAction.Redraw;
            }
            else
            {
                if (_mode != AutocompleteMode.None)
                {
                    _mode = AutocompleteMode.None;
                    SyncState();
                    return isAppend ? InputAction.AppendChar : InputAction.Redraw;
                }
            }
        }
        else if (_mode == AutocompleteMode.Command)
        {
            // Was in command mode but input no longer starts with /
            _mode = AutocompleteMode.None;
            SyncState();
            return isAppend ? InputAction.AppendChar : InputAction.Redraw;
        }

        SyncState();
        return isAppend ? InputAction.AppendChar : InputAction.Redraw;
    }

    // ─── Internal Helpers ──────────────────────────────────────

    private void AcceptCommandSelection()
    {
        _input.Clear();
        _input.Append(_filteredCommands[_selectedIndex]);
        _cursorPos = _input.Length;
        _mode = AutocompleteMode.None;
    }

    private void InsertFileSelection(string selectedPath)
    {
        var before = _input.ToString().Substring(0, _atAnchorPos);
        _input.Clear();
        _input.Append(before);
        _input.Append('@');
        _input.Append(selectedPath);
        _cursorPos = _input.Length;
        _mode = AutocompleteMode.None;
        _atAnchorPos = -1;
        _selectedIndex = 0;
    }

    private void DrillIntoDirectory(string dirPath)
    {
        var before = _input.ToString().Substring(0, _atAnchorPos);
        _input.Clear();
        _input.Append(before);
        _input.Append('@');
        _input.Append(dirPath);
        _cursorPos = _input.Length;

        // Re-filter to show directory contents
        _filteredFiles = _fileProvider?.FilterFiles(dirPath) ?? new();
        if (_filteredFiles.Any())
        {
            _selectedIndex = 0;
        }
        else
        {
            _mode = AutocompleteMode.None;
            _atAnchorPos = -1;
            _selectedIndex = 0;
        }
    }

    private void UpdateAutocompleteAfterEdit()
    {
        if (_mode == AutocompleteMode.File)
        {
            if (_atAnchorPos >= _input.Length)
            {
                // Deleted back to or past the @
                _mode = AutocompleteMode.None;
                _atAnchorPos = -1;
                _selectedIndex = 0;
            }
            else
            {
                var fragment = _input.ToString().Substring(_atAnchorPos + 1);
                _filteredFiles = _fileProvider?.FilterFiles(fragment) ?? new();
                if (_filteredFiles.Any())
                {
                    _selectedIndex = Math.Min(_selectedIndex, _filteredFiles.Count - 1);
                }
                else
                {
                    _mode = AutocompleteMode.None;
                    _atAnchorPos = -1;
                    _selectedIndex = 0;
                }
            }
        }
        else if (_input.Length > 0 && _input[0] == '/')
        {
            _filteredCommands = FilterCommands(_input.ToString());
            if (_filteredCommands.Any())
            {
                _mode = AutocompleteMode.Command;
                _selectedIndex = Math.Min(_selectedIndex, _filteredCommands.Count - 1);
            }
            else
            {
                _mode = AutocompleteMode.None;
            }
        }
        else
        {
            _mode = AutocompleteMode.None;
            _selectedIndex = 0;
        }
    }

    private bool DismissDropdownIfOpen()
    {
        if (_mode == AutocompleteMode.None)
            return false;

        _mode = AutocompleteMode.None;
        _atAnchorPos = -1;
        _selectedIndex = 0;
        return true;
    }

    private List<string> FilterCommands(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return _commands.Keys.ToList();

        var query = input.ToLower();
        return _commands.Keys
            .Where(cmd => cmd.ToLower().StartsWith(query))
            .ToList();
    }

    private string GetBrowsePrefix()
    {
        if (_atAnchorPos < 0 || _atAnchorPos >= _input.Length) return "";
        var fragment = _input.ToString().Substring(_atAnchorPos + 1);
        var lastSlash = fragment.LastIndexOf('/');
        return lastSlash >= 0 ? fragment.Substring(0, lastSlash + 1) : "";
    }

    private void AddToHistory(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return;

        if (_history.Count > 0 && _history[^1] == input)
        {
            _historyIndex = -1;
            _savedInput = null;
            return;
        }

        _history.Add(input);
        _historyIndex = -1;
        _savedInput = null;
    }

    /// <summary>
    /// Finds the rightmost valid @ trigger position (preceded by space or at position 0).
    /// </summary>
    private static int FindAtTrigger(string text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == '@' && (i == 0 || text[i - 1] == ' '))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Syncs internal state to the public InputRenderState snapshot.
    /// </summary>
    private void SyncState()
    {
        State.InputText = _input.ToString();
        State.CursorPos = _cursorPos;
        State.Mode = _mode;
        State.SelectedIndex = _selectedIndex;
        State.BrowsePrefix = GetBrowsePrefix();

        State.DropdownItems = _mode switch
        {
            AutocompleteMode.Command => _filteredCommands,
            AutocompleteMode.File => _filteredFiles,
            _ => Array.Empty<string>(),
        };
    }
}
