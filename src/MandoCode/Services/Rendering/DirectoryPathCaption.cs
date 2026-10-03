using System.Text.RegularExpressions;
using RazorConsole.Core.Input;

namespace MandoCode.Services;

/// <summary>Keeps the current directory visible when a pane cannot fit its full path.</summary>
public static class DirectoryPathCaption
{
    public static string Fit(string path, int width, bool showIcon = true)
    {
        var icon = showIcon ? "📁 " : "";
        if (TextSelectionState.CellWidth(icon + path) <= width) return icon + path;
        var available = Math.Max(0, width - TextSelectionState.CellWidth(icon));
        var separator = path.Contains('\\') ? "\\" : "/";
        var root = Regex.Match(path, @"^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+[\\/]?|[\\/])").Value;
        var folders = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var count = folders.Length - 1; count >= 1; count--)
        {
            var candidate = root + "…" + separator + string.Join(separator, folders[^count..]);
            if (TextSelectionState.CellWidth(candidate) <= available) return icon + candidate;
        }
        // If even the drive cannot fit, retain the leaf and nearest parents.
        for (var count = folders.Length - 1; count >= 1; count--)
        {
            var candidate = "…" + separator + string.Join(separator, folders[^count..]);
            if (TextSelectionState.CellWidth(candidate) <= available) return icon + candidate;
        }
        var leaf = folders.LastOrDefault() ?? path;
        if (TextSelectionState.CellWidth(leaf) <= available) return icon + leaf;
        var tail = "";
        foreach (var rune in leaf.EnumerateRunes().Reverse())
        {
            if (TextSelectionState.CellWidth("…" + rune + tail) > available) break;
            tail = rune + tail;
        }
        return available >= 1 ? icon + "…" + tail : "";
    }
}
