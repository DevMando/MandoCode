using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for InputStateMachine — the pure state machine that handles
/// all keyboard input, autocomplete, history, and cursor tracking.
///
/// KEY CONCEPTS:
///   [Fact]   = a single test case (always runs the same way)
///   [Theory] = a parameterized test — runs once per [InlineData] row
///
/// Each test follows Arrange → Act → Assert:
///   Arrange: create the state machine, set up any preconditions
///   Act:     call the method under test
///   Assert:  verify the result and state are what we expect
/// </summary>
public class InputStateMachineTests
{
    // ── Helper: builds a state machine with some test commands ──

    private static InputStateMachine CreateMachine(
        Dictionary<string, string>? commands = null,
        FileAutocompleteProvider? fileProvider = null)
    {
        commands ??= new Dictionary<string, string>
        {
            { "/help", "Show help" },
            { "/clear", "Clear chat" },
            { "/config", "Open config" },
            { "/exit", "Exit app" },
            { "/learn", "Learn about a topic" },
        };

        var machine = new InputStateMachine(commands, fileProvider);
        machine.BeginNewInput();
        return machine;
    }

    /// <summary>
    /// Helper: simulates typing a string character by character.
    /// Returns the last InputAction from the final keystroke.
    /// </summary>
    private static InputAction TypeString(InputStateMachine machine, string text)
    {
        InputAction last = InputAction.Noop;
        foreach (char c in text)
        {
            last = machine.ProcessKey(new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false));
        }
        return last;
    }

    /// <summary>
    /// Helper: presses a special key (Enter, Tab, arrows, etc.)
    /// </summary>
    private static InputAction PressKey(InputStateMachine machine, ConsoleKey key)
    {
        return machine.ProcessKey(new ConsoleKeyInfo('\0', key, false, false, false));
    }

    // ════════════════════════════════════════════════════════════
    //  1. BASIC TEXT INPUT
    //     The simplest tests: type characters, verify the state.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void BeginNewInput_StartsEmpty()
    {
        // Arrange
        var machine = CreateMachine();

        // Assert — no Act needed, we're testing initial state
        Assert.Equal("", machine.State.InputText);
        Assert.Equal(0, machine.State.CursorPos);
        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    [Fact]
    public void TypeCharacters_BuildsInputText()
    {
        // Arrange
        var machine = CreateMachine();

        // Act — type "hello"
        TypeString(machine, "hello");

        // Assert
        Assert.Equal("hello", machine.State.InputText);
        Assert.Equal(5, machine.State.CursorPos); // cursor at end
    }

    [Fact]
    public void AppendChar_ReturnedWhenTypingAtEnd()
    {
        // Arrange
        var machine = CreateMachine();

        // Act — type a single character at the end (empty input)
        var action = TypeString(machine, "a");

        // Assert — AppendChar is an optimization hint: "just print this char"
        Assert.Equal(InputAction.AppendChar, action);
    }

    [Fact]
    public void TypeAtMiddle_ReturnsRedraw()
    {
        // Arrange
        var machine = CreateMachine();
        TypeString(machine, "hllo");

        // Move cursor left 3 positions to after 'h'
        PressKey(machine, ConsoleKey.LeftArrow);
        PressKey(machine, ConsoleKey.LeftArrow);
        PressKey(machine, ConsoleKey.LeftArrow);

        // Act — insert 'e' in the middle → "hello"
        var action = machine.ProcessKey(new ConsoleKeyInfo('e', ConsoleKey.None, false, false, false));

        // Assert — inserting in the middle requires full redraw
        Assert.Equal(InputAction.Redraw, action);
        Assert.Equal("hello", machine.State.InputText);
        Assert.Equal(2, machine.State.CursorPos); // cursor after 'e'
    }

    // ════════════════════════════════════════════════════════════
    //  2. CURSOR MOVEMENT
    //     Arrow keys, Home, End.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void LeftArrow_MovesCursorBack()
    {
        var machine = CreateMachine();
        TypeString(machine, "abc");

        var action = PressKey(machine, ConsoleKey.LeftArrow);

        Assert.Equal(InputAction.CursorMoved, action);
        Assert.Equal(2, machine.State.CursorPos);
    }

    [Fact]
    public void LeftArrow_AtStart_ReturnsNoop()
    {
        var machine = CreateMachine();
        // Don't type anything — cursor is at 0

        var action = PressKey(machine, ConsoleKey.LeftArrow);

        Assert.Equal(InputAction.Noop, action);
    }

    [Fact]
    public void RightArrow_MovesCursorForward()
    {
        var machine = CreateMachine();
        TypeString(machine, "abc");
        PressKey(machine, ConsoleKey.LeftArrow); // cursor at 2

        var action = PressKey(machine, ConsoleKey.RightArrow);

        Assert.Equal(InputAction.CursorMoved, action);
        Assert.Equal(3, machine.State.CursorPos);
    }

    [Fact]
    public void RightArrow_AtEnd_ReturnsNoop()
    {
        var machine = CreateMachine();
        TypeString(machine, "abc");

        var action = PressKey(machine, ConsoleKey.RightArrow);

        Assert.Equal(InputAction.Noop, action);
    }

    [Fact]
    public void Home_MovesCursorToStart()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");

        PressKey(machine, ConsoleKey.Home);

        Assert.Equal(0, machine.State.CursorPos);
    }

    [Fact]
    public void End_MovesCursorToEnd()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");
        PressKey(machine, ConsoleKey.Home); // go to start

        PressKey(machine, ConsoleKey.End);

        Assert.Equal(5, machine.State.CursorPos);
    }

    // ════════════════════════════════════════════════════════════
    //  3. BACKSPACE & DELETE
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Backspace_RemovesCharBeforeCursor()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");

        var action = PressKey(machine, ConsoleKey.Backspace);

        Assert.Equal(InputAction.Redraw, action);
        Assert.Equal("hell", machine.State.InputText);
        Assert.Equal(4, machine.State.CursorPos);
    }

    [Fact]
    public void Backspace_AtStart_ReturnsNoop()
    {
        var machine = CreateMachine();

        var action = PressKey(machine, ConsoleKey.Backspace);

        Assert.Equal(InputAction.Noop, action);
    }

    [Fact]
    public void Delete_RemovesCharAtCursor()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");
        PressKey(machine, ConsoleKey.Home); // cursor at 0

        var action = PressKey(machine, ConsoleKey.Delete);

        Assert.Equal(InputAction.Redraw, action);
        Assert.Equal("ello", machine.State.InputText);
        Assert.Equal(0, machine.State.CursorPos); // cursor stays
    }

    [Fact]
    public void Delete_AtEnd_ReturnsNoop()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");

        var action = PressKey(machine, ConsoleKey.Delete);

        Assert.Equal(InputAction.Noop, action);
    }

    // ════════════════════════════════════════════════════════════
    //  4. SUBMIT (Enter key)
    //     Pressing Enter outside of autocomplete submits the input.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Enter_SubmitsInput()
    {
        var machine = CreateMachine();
        TypeString(machine, "build the app");

        var action = PressKey(machine, ConsoleKey.Enter);

        Assert.Equal(InputAction.Submit, action);
        Assert.Equal("build the app", machine.State.SubmittedText);
    }

    // ════════════════════════════════════════════════════════════
    //  5. COMMAND AUTOCOMPLETE
    //     Typing "/" triggers command filtering.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void SlashTriggersCommandAutocomplete()
    {
        var machine = CreateMachine();

        // Act — type "/"
        var action = TypeString(machine, "/");

        // Assert — all commands match "/", so dropdown opens
        Assert.Equal(AutocompleteMode.Command, machine.State.Mode);
        Assert.Equal(5, machine.State.DropdownItems.Count); // all 5 commands
    }

    [Fact]
    public void SlashC_FiltersToMatchingCommands()
    {
        var machine = CreateMachine();

        TypeString(machine, "/c");

        // "/clear" and "/config" start with "/c"
        Assert.Equal(AutocompleteMode.Command, machine.State.Mode);
        Assert.Equal(2, machine.State.DropdownItems.Count);
        Assert.Contains("/clear", machine.State.DropdownItems);
        Assert.Contains("/config", machine.State.DropdownItems);
    }

    [Fact]
    public void Tab_AcceptsSelectedCommand()
    {
        var machine = CreateMachine();
        TypeString(machine, "/he"); // matches "/help"

        var action = PressKey(machine, ConsoleKey.Tab);

        Assert.Equal(InputAction.AcceptCommand, action);
        Assert.Equal("/help", machine.State.InputText);
        Assert.Equal(AutocompleteMode.None, machine.State.Mode); // dropdown closed
    }

    [Fact]
    public void DownArrow_CyclesCommandDropdown()
    {
        var machine = CreateMachine();
        TypeString(machine, "/c"); // matches: /clear, /config

        Assert.Equal(0, machine.State.SelectedIndex); // starts at first item

        PressKey(machine, ConsoleKey.DownArrow);
        Assert.Equal(1, machine.State.SelectedIndex); // second item

        PressKey(machine, ConsoleKey.DownArrow);
        Assert.Equal(0, machine.State.SelectedIndex); // wraps around!
    }

    [Fact]
    public void UpArrow_CyclesCommandDropdownBackward()
    {
        var machine = CreateMachine();
        TypeString(machine, "/c"); // matches: /clear, /config

        PressKey(machine, ConsoleKey.UpArrow);
        Assert.Equal(1, machine.State.SelectedIndex); // wraps to last
    }

    [Fact]
    public void Escape_DismissesDropdown()
    {
        var machine = CreateMachine();
        TypeString(machine, "/c"); // opens dropdown

        var action = PressKey(machine, ConsoleKey.Escape);

        Assert.Equal(InputAction.ClearDropdown, action);
        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    [Fact]
    public void NoMatchingCommands_DropdownCloses()
    {
        var machine = CreateMachine();
        TypeString(machine, "/xyz");

        // No commands start with "/xyz"
        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    // ════════════════════════════════════════════════════════════
    //  6. COMMAND HISTORY
    //     Up/Down arrows navigate history when no dropdown is open.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void History_UpArrow_NavigatesToPreviousInput()
    {
        var machine = CreateMachine();

        // Submit two inputs to build history
        TypeString(machine, "first command");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        TypeString(machine, "second command");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        // Act — press Up to go back in history
        PressKey(machine, ConsoleKey.UpArrow);

        Assert.Equal("second command", machine.State.InputText);
    }

    [Fact]
    public void History_UpUp_GoesBackTwice()
    {
        var machine = CreateMachine();

        TypeString(machine, "first");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        TypeString(machine, "second");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        PressKey(machine, ConsoleKey.UpArrow); // → "second"
        PressKey(machine, ConsoleKey.UpArrow); // → "first"

        Assert.Equal("first", machine.State.InputText);
    }

    [Fact]
    public void History_DownArrow_RestoresSavedInput()
    {
        var machine = CreateMachine();

        TypeString(machine, "old input");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        // Start typing something new, then navigate back
        TypeString(machine, "new typ");
        PressKey(machine, ConsoleKey.UpArrow); // → "old input", saves "new typ"
        PressKey(machine, ConsoleKey.DownArrow); // → back to "new typ"

        Assert.Equal("new typ", machine.State.InputText);
    }

    [Fact]
    public void History_SkipsDuplicateConsecutiveEntries()
    {
        var machine = CreateMachine();

        TypeString(machine, "same");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        TypeString(machine, "same");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        // Only one "same" in history
        PressKey(machine, ConsoleKey.UpArrow);
        Assert.Equal("same", machine.State.InputText);

        // Can't go further back
        PressKey(machine, ConsoleKey.UpArrow);
        Assert.Equal("same", machine.State.InputText);
    }

    [Fact]
    public void History_EmptyInput_NotAdded()
    {
        var machine = CreateMachine();

        // Submit empty input
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        // History should be empty
        var action = PressKey(machine, ConsoleKey.UpArrow);
        Assert.Equal(InputAction.Noop, action);
    }

    // ════════════════════════════════════════════════════════════
    //  7. PASTE HANDLING
    //     Simulates pasting multiple characters at once.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ProcessPaste_InsertsAllCharacters()
    {
        var machine = CreateMachine();

        var firstKey = new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false);
        var buffered = new List<ConsoleKeyInfo>
        {
            new('e', ConsoleKey.E, false, false, false),
            new('l', ConsoleKey.L, false, false, false),
            new('l', ConsoleKey.L, false, false, false),
            new('o', ConsoleKey.O, false, false, false),
        };

        var action = machine.ProcessPaste(firstKey, buffered);

        Assert.Equal(InputAction.Redraw, action);
        Assert.Equal("hello", machine.State.InputText);
    }

    [Fact]
    public void ProcessPaste_NewlinesBecomesSpaces()
    {
        var machine = CreateMachine();

        var firstKey = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);
        var buffered = new List<ConsoleKeyInfo>
        {
            new('\n', ConsoleKey.Enter, false, false, false),
            new('b', ConsoleKey.B, false, false, false),
        };

        machine.ProcessPaste(firstKey, buffered);

        Assert.Equal("a b", machine.State.InputText);
    }

    [Fact]
    public void ProcessPaste_ClosesDropdown()
    {
        var machine = CreateMachine();
        TypeString(machine, "/"); // opens command dropdown

        Assert.Equal(AutocompleteMode.Command, machine.State.Mode);

        // Paste while dropdown is open
        var firstKey = new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false);
        machine.ProcessPaste(firstKey, Array.Empty<ConsoleKeyInfo>());

        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    // ════════════════════════════════════════════════════════════
    //  8. STATIC HELPERS
    //     IsCommand() and GetCommandName() — pure functions.
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("/help", true)]
    [InlineData("/clear", true)]
    [InlineData("  /config", true)]    // leading spaces
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsCommand_DetectsSlashPrefix(string input, bool expected)
    {
        // [Theory] runs this test once for each [InlineData] row.
        // So this single method actually produces 6 test cases!
        Assert.Equal(expected, InputStateMachine.IsCommand(input));
    }

    [Theory]
    [InlineData("/help", "help")]
    [InlineData("/CLEAR", "clear")]      // lowercased
    [InlineData("  /Config", "config")]  // trimmed + lowered
    [InlineData("hello", "hello")]       // not a command, returns as-is
    public void GetCommandName_ExtractsName(string input, string expected)
    {
        Assert.Equal(expected, InputStateMachine.GetCommandName(input));
    }

    // ════════════════════════════════════════════════════════════
    //  9. EDGE CASES
    //     Boundary conditions that might break things.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void BeginNewInput_ResetsEverything()
    {
        var machine = CreateMachine();
        TypeString(machine, "some text");
        PressKey(machine, ConsoleKey.LeftArrow);

        machine.BeginNewInput();

        Assert.Equal("", machine.State.InputText);
        Assert.Equal(0, machine.State.CursorPos);
        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    [Fact]
    public void Escape_WithNoDropdown_ReturnsNoop()
    {
        var machine = CreateMachine();
        TypeString(machine, "hello");

        var action = PressKey(machine, ConsoleKey.Escape);

        Assert.Equal(InputAction.Noop, action);
    }

    [Fact]
    public void ClearHistory_RemovesAllHistory()
    {
        var machine = CreateMachine();

        TypeString(machine, "something");
        PressKey(machine, ConsoleKey.Enter);
        machine.BeginNewInput();

        machine.ClearHistory();

        var action = PressKey(machine, ConsoleKey.UpArrow);
        Assert.Equal(InputAction.Noop, action);
    }

    [Fact]
    public void LeftArrow_DismissesDropdown()
    {
        var machine = CreateMachine();
        TypeString(machine, "/c"); // opens command dropdown

        Assert.Equal(AutocompleteMode.Command, machine.State.Mode);

        var action = PressKey(machine, ConsoleKey.LeftArrow);

        Assert.Equal(InputAction.ClearDropdown, action);
        Assert.Equal(AutocompleteMode.None, machine.State.Mode);
    }

    [Fact]
    public void Enter_WithCommandDropdown_AcceptsCommand()
    {
        var machine = CreateMachine();
        TypeString(machine, "/he"); // matches "/help"

        var action = PressKey(machine, ConsoleKey.Enter);

        // Enter while dropdown is open accepts the selection
        Assert.Equal(InputAction.AcceptCommand, action);
        Assert.Equal("/help", machine.State.InputText);
    }
}
