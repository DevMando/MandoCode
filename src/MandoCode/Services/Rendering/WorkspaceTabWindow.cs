using System.Text;
using RazorConsole.Core.Input;

namespace MandoCode.Services;

/// <summary>Bounds tab presentation without changing mounted workspace identities.</summary>
public sealed class WorkspaceTabWindow
{
    public int Start { get; private set; }
    public int Capacity { get; private set; }
    public int TabWidth { get; private set; }
    public int AvailableWidth { get; private set; }
    private int _activeId = -1;
    private int _lastWidth = -1;
    public void Update(IReadOnlyList<AgentWorkspace> workspaces, AgentWorkspace active, int width)
    {
        AvailableWidth = Math.Max(0, width);
        Capacity = AvailableWidth == 0 ? 0 : Math.Min(workspaces.Count, Math.Max(1, AvailableWidth / 22));
        TabWidth = Capacity == 0 ? 0 : Math.Min(22, AvailableWidth / Capacity);
        Start = Math.Clamp(Start, 0, Math.Max(0, workspaces.Count - Capacity));
        if (_activeId != active.Id || _lastWidth != width)
        {
            var index = workspaces.ToList().IndexOf(active);
            if (index < Start) Start = Math.Max(0, index);
            else if (index >= Start + Capacity) Start = Math.Max(0, index - Capacity + 1);
            _activeId = active.Id;
            _lastWidth = width;
        }
    }
    public void Scroll(int direction, int count)
        => Start = Math.Clamp(Start + direction * Math.Max(1, Capacity), 0, Math.Max(0, count - Capacity));
    public bool Includes(int index) => index >= Start && index < Start + Capacity;
    public void RevealSelection() => _activeId = -1;
    public static string Fit(string text, int cells)
    {
        if (cells <= 0) return "";
        if (TextSelectionState.CellWidth(text) <= cells) return text;
        var result = new StringBuilder();
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = TextSelectionState.CellWidth(rune.ToString());
            if (width + runeWidth > cells - 1) break;
            result.Append(rune);
            width += runeWidth;
        }
        return result + "…";
    }
}
