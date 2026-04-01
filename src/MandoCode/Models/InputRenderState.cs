namespace MandoCode.Models;

/// <summary>
/// Immutable snapshot of the input state machine's visual state.
/// Contains everything the rendering layer needs to draw a frame.
/// </summary>
public class InputRenderState
{
    /// <summary>Current input text.</summary>
    public string InputText { get; set; } = "";

    /// <summary>Logical cursor position within InputText.</summary>
    public int CursorPos { get; set; }

    /// <summary>Current autocomplete mode.</summary>
    public AutocompleteMode Mode { get; set; } = AutocompleteMode.None;

    /// <summary>Items shown in the dropdown (commands or file paths).</summary>
    public IReadOnlyList<string> DropdownItems { get; set; } = Array.Empty<string>();

    /// <summary>Currently highlighted item in the dropdown.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>Command descriptions for the command dropdown display.</summary>
    public IReadOnlyDictionary<string, string> CommandDescriptions { get; set; }
        = new Dictionary<string, string>();

    /// <summary>Directory prefix to strip from file dropdown display paths.</summary>
    public string BrowsePrefix { get; set; } = "";

    /// <summary>The last appended character (set when action is AppendChar).</summary>
    public char LastAppendedChar { get; set; }

    /// <summary>The submitted text (set when action is Submit).</summary>
    public string? SubmittedText { get; set; }
}

/// <summary>
/// Autocomplete mode for the input state machine.
/// </summary>
public enum AutocompleteMode
{
    None,
    Command,
    File,
}
