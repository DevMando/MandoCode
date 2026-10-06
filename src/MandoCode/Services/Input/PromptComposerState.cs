using MandoCode.Models;
using RazorConsole.Core.Input;

namespace MandoCode.Services;

/// <summary>One keyboard owner for editing and caret-local slash/file completion.</summary>
public sealed class PromptComposerState(InputStateMachine machine)
{
    public TextSelectionState Buffer { get; } = new();
    public InputRenderState Suggestions => machine.State;
    public bool FilesLoading => machine.FilesLoading && Suggestions.Mode == AutocompleteMode.File;
    public bool IsOpen => Suggestions.Mode != AutocompleteMode.None && Suggestions.DropdownItems.Count > 0;
    public CliAgentDirectory? Agents { get; set; }
    public bool ViewingAgents { get; private set; }
    public AgentPane[] AgentSuggestions { get; private set; } = [];
    public int AgentSelected { get; private set; }
    private string? _dismissedAt;
    public (int Start, string Fragment)? AtFragment
    {
        get
        {
            var prefix = Buffer.Text[..Buffer.Cursor];
            var start = prefix.LastIndexOf('@');
            if (start < 0 || (start > 0 && !char.IsWhiteSpace(prefix[start - 1]))) return null;
            var fragment = prefix[(start + 1)..];
            if (fragment.StartsWith('"'))
            {
                if (fragment.EndsWith('"') && fragment.Length > 1) return null;
                fragment = fragment[1..];
            }
            else if (fragment.Any(char.IsWhiteSpace)) return null;
            return (start, fragment);
        }
    }
    public bool AtPickerOpen => Agents is not null && AtFragment is { } at && _dismissedAt != Buffer.Text[..Buffer.Cursor] &&
        (ViewingAgents || FilesLoading || IsOpen || !at.Fragment.EndsWith('/'));
    public bool ToggleAgentView()
    {
        if (!AtPickerOpen) return false;
        ViewingAgents = !ViewingAgents;
        Refresh();
        return true;
    }
    public void SetText(string text)
    {
        Buffer.SetText(text);
        Buffer.Begin(text.Length, 1, false);
        Buffer.End();
        Buffer.ClearSelection();
        Refresh();
    }
    public void Refresh()
    {
        if (AtFragment is { } at && Agents is not null)
        {
            var selected = AgentSuggestions.ElementAtOrDefault(AgentSelected)?.PersistKey;
            AgentSuggestions = Agents.Match(at.Fragment);
            var retained = Array.FindIndex(AgentSuggestions, p => p.PersistKey == selected);
            AgentSelected = retained >= 0 ? retained : 0;
        }
        else { ViewingAgents = false; AgentSuggestions = []; _dismissedAt = null; }
        if (!ViewingAgents) machine.UpdateText(Buffer.Text[..Buffer.Cursor]);
    }
    public void InsertAgent(int index)
    {
        if (AtFragment is not { } at || index < 0 || index >= AgentSuggestions.Length) return;
        var selected = AgentSuggestions[index];
        if (Agents?.All.All(p => p.PersistKey != selected.PersistKey) != false) { Refresh(); return; }
        var inserted = Buffer.Text[..at.Start] + FileReferenceToken.Format(selected.Name) + " ";
        Buffer.SetText(inserted + Buffer.Text[Buffer.Cursor..]);
        Buffer.Begin(inserted.Length, 1, false); Buffer.End(); Buffer.ClearSelection();
        ViewingAgents = false;
        Refresh();
    }
    public void InsertFileReference(string path)
    {
        if (Agents is not null && CliAgentDirectory.IsAgentName(path) && Agents.Resolve(path) is not null) path = "./" + path;
        var prefix = Buffer.Text[..Buffer.Cursor];
        var suffix = Buffer.Text[Buffer.Cursor..];
        var anchor = prefix.LastIndexOf('@');
        if (anchor >= 0 && (anchor == 0 || char.IsWhiteSpace(prefix[anchor - 1])) && !prefix[(anchor + 1)..].Any(char.IsWhiteSpace))
            prefix = prefix[..anchor];
        if (prefix.Length > 0 && !char.IsWhiteSpace(prefix[^1])) prefix += " ";
        var inserted = prefix + FileReferenceToken.Format(path) + " ";
        Buffer.SetText(inserted + suffix);
        Buffer.Begin(inserted.Length, 1, false);
        Buffer.End();
        Buffer.ClearSelection();
        Refresh();
    }
    public bool HandleKey(string key)
    {
        if (key == "Tab" && AtPickerOpen) return ToggleAgentView();
        if (AtPickerOpen && ViewingAgents)
        {
            switch (key)
            {
                case "ArrowUp": AgentSelected = Math.Max(0, AgentSelected - 1); return true;
                case "ArrowDown": AgentSelected = Math.Min(Math.Max(0, AgentSuggestions.Length - 1), AgentSelected + 1); return true;
                case "Enter": InsertAgent(AgentSelected); return true;
                case "Escape": _dismissedAt = Buffer.Text[..Buffer.Cursor]; ViewingAgents = false; machine.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)); return true;
                default: return false;
            }
        }
        if (AtPickerOpen && key == "Escape") { _dismissedAt = Buffer.Text[..Buffer.Cursor]; if (FilesLoading) machine.CancelFileLoading(); machine.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)); return true; }
        if (FilesLoading && key == "Escape") { machine.CancelFileLoading(); machine.ProcessKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)); return true; }
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
            // File selection is explicit: a file sharing an agent's name must remain a path.
            if (consoleKey is ConsoleKey.Tab or ConsoleKey.Enter && Agents is not null && prefix.EndsWith(' '))
            {
                var anchor = prefix.LastIndexOf('@');
                var path = anchor < 0 ? null : FileReferenceToken.Paths(prefix[anchor..]).FirstOrDefault();
                if (path is not null && CliAgentDirectory.IsAgentName(path) && Agents.Resolve(path) is not null)
                    prefix = prefix[..anchor] + FileReferenceToken.Format("./" + path) + " ";
            }
            Buffer.SetText(prefix + suffix);
            Buffer.Begin(prefix.Length, 1, false);
            Buffer.End();
            Buffer.ClearSelection();
        }
        return true;
    }
    public void Submitted(string text) => machine.SubmitInput(text);
}
