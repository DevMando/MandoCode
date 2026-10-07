using System.Globalization;
using RazorConsole.Core.Input;

namespace MandoCode.Services;

/// <summary>Moves by visual rows, retaining the desired column through shorter lines.</summary>
internal sealed class PromptCursorNavigation
{
    private int? _column;
    private int _lastCursor = -1;
    private string? _lastText;
    internal void Reset() { _column = null; _lastCursor = -1; _lastText = null; }
    internal bool Move(TextSelectionState buffer, int width, int height, int direction, bool extend)
    {
        var rows = buffer.Wrap(width, 0, true);
        var current = rows.Select((row, index) => (row, index)).Last(pair => pair.row.Start <= buffer.Cursor).index;
        var targetRow = current + direction;
        if (targetRow < 0 || targetRow >= rows.Count) return false;
        if (_lastCursor != buffer.Cursor || !ReferenceEquals(_lastText, buffer.Text)) _column = null;
        _column ??= TextSelectionState.CellWidth(buffer.Text[rows[current].Start..Math.Min(buffer.Cursor, rows[current].End)]);
        var target = buffer.PositionAt(rows[targetRow], _column.Value);
        // At a soft wrap, the ending offset belongs to the following visual row.
        // Keep upward movement on the requested row when its width is shorter.
        if (targetRow + 1 < rows.Count && target == rows[targetRow + 1].Start && target > rows[targetRow].Start)
            target = StringInfo.ParseCombiningCharacters(buffer.Text[..target]).Last();
        if (extend) { buffer.Begin(buffer.Cursor, 1, true); buffer.Extend(target); }
        else buffer.Begin(target, 1, false);
        buffer.End();
        _lastCursor = buffer.Cursor; _lastText = buffer.Text;
        buffer.Offset = Math.Clamp(buffer.Offset, Math.Max(0, targetRow - height + 1), targetRow);
        return true;
    }
}