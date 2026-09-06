namespace MandoCode.Services;

/// <summary>Supplies the actual competing matches so the next edit can be unambiguous.</summary>
public static class AmbiguousEditGuidance
{
    public static string Describe(string content, string oldText)
    {
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');
        oldText = oldText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (oldText.Length == 0) return "old_text must be non-empty.";
        var lines = content.Split('\n');
        var matches = new List<string>();
        var offset = 0;
        while (matches.Count < 4)
        {
            var index = content.IndexOf(oldText, offset, StringComparison.Ordinal);
            if (index < 0) break;
            var line = content[..index].Count(c => c == '\n');
            var start = Math.Max(0, line - 2);
            var end = Math.Min(lines.Length, line + oldText.Count(c => c == '\n') + 3);
            matches.Add($"Match at line {line + 1}:\n" + string.Join("\n", Enumerable.Range(start, end - start)
                .Take(8).Select(i => $"{i + 1}: {lines[i]}")));
            offset = index + oldText.Length;
        }
        return "Found multiple occurrences of old_text. Do not repeat the same fragment. " +
            "Use the distinct surrounding lines below in your next old_text, or read the indicated range. " +
            "If all matches intentionally need changing, read the full relevant block and replace it with unique context. " +
            "No changes were made.\n" + string.Join("\n\n", matches);
    }
}
