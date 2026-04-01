namespace MandoCode.Models;

/// <summary>
/// Actions returned by InputStateMachine.ProcessKey() telling the
/// orchestrator what visual update is needed.
/// </summary>
public enum InputAction
{
    /// <summary>Nothing visually changed.</summary>
    Noop,

    /// <summary>Input text changed — redraw the input line.</summary>
    Redraw,

    /// <summary>Single char appended at end — orchestrator can just Write(char).</summary>
    AppendChar,

    /// <summary>Cursor position changed without text change.</summary>
    CursorMoved,

    /// <summary>Command dropdown needs to be shown/updated.</summary>
    ShowCommandDropdown,

    /// <summary>File dropdown needs to be shown/updated.</summary>
    ShowFileDropdown,

    /// <summary>Dropdown was dismissed.</summary>
    ClearDropdown,

    /// <summary>Command selected from dropdown — redraw input with command text.</summary>
    AcceptCommand,

    /// <summary>File selected from dropdown — redraw input with file path.</summary>
    AcceptFile,

    /// <summary>Directory selected — update input and show directory contents.</summary>
    DrillDirectory,

    /// <summary>User pressed Enter to submit input.</summary>
    Submit,
}
