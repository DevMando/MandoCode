using MandoCode.Models;
using RazorConsole.Core.Input;

namespace MandoCode.Services;

/// <summary>One keyboard owner for editing and caret-local slash/file completion.</summary>
public sealed class PromptComposerState(InputStateMachine machine)
{
    public TextSelectionState Buffer { get; } = new();
    public InputRenderState Suggestions => machine.State;
    public bool IsOpen => Suggestions.Mode != AutocompleteMode.None && Suggestions.DropdownItems.Count > 0;
    public void SetText(string text)
    {
        Buffer.SetText(text);
        Buffer.Begin(text.Length, 1, false);
        Buffer.End();
        Buffer.ClearSelection();
        Refresh();
    }
    public void Refresh() => machine.UpdateText(Buffer.Text[..Buffer.Cursor]);
    public bool HandleKey(string key)
    {
        if (!IsOpen) return false;
        var consoleKey = key switch
        {
            "ArrowUp" => ConsoleKey.UpArrow,
            "ArrowDown" => ConsoleKey.DownArrow,
            "Tab" => ConsoleKey.Tab,
            "Enter" => ConsoleKey.Enter,
            "Escape" => ConsoleKey.Escape,
            _ => (ConsoleKey?)null,
        };
        if (consoleKey is null) return false;
        var suffix = Buffer.Text[Buffer.Cursor..];
        machine.ProcessKey(new ConsoleKeyInfo('\0', consoleKey.Value, false, false, false));
        if (consoleKey is ConsoleKey.Tab or ConsoleKey.Enter)
        {
            var prefix = Suggestions.InputText;
            Buffer.SetText(prefix + suffix);
            Buffer.Begin(prefix.Length, 1, false);
            Buffer.End();
            Buffer.ClearSelection();
        }
        return true;
    }
    public void Submitted(string text) => machine.SubmitInput(text);
}
