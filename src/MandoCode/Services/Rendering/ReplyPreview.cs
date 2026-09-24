using System.Globalization;
using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Lays out the live reply preview the spinner shows while a reply streams: the last few lines of
/// the text, wrapped to the terminal and cut so no line can wrap on its own. A line that wrapped
/// would take two rows while the spinner counts one, and its cursor-up redraws would then land on
/// the wrong row and eat scrollback.
/// </summary>
public static class ReplyPreview
{
    /// <summary>The last <paramref name="maxLines"/> display lines of <paramref name="text"/>, each at
    /// most <paramref name="width"/> terminal cells wide, with control characters removed.</summary>
    public static IReadOnlyList<string> Tail(string? text, int width, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text) || width < 1 || maxLines < 1)
            return Array.Empty<string>();

        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var clean = Clean(raw);
            if (clean.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            Wrap(clean, width, lines);
        }

        // Blank lines at either end carry nothing a preview needs; inner ones keep paragraphs apart.
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);

        return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
    }

    private static string Clean(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c == '\t') sb.Append("    ");
            else if (!char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    // Greedy word wrap by terminal cells; a word wider than the line is split hard.
    private static void Wrap(string line, int width, List<string> into)
    {
        var current = new StringBuilder();
        int currentCells = 0;

        foreach (var word in line.Split(' '))
        {
            int wordCells = Cells(word);
            int needed = currentCells == 0 ? wordCells : currentCells + 1 + wordCells;
            if (needed <= width)
            {
                if (currentCells > 0) { current.Append(' '); currentCells++; }
                current.Append(word);
                currentCells += wordCells;
                continue;
            }

            if (currentCells > 0)
            {
                into.Add(current.ToString());
                current.Clear();
                currentCells = 0;
            }

            // Split a word that can't fit even on its own line.
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                int cells = Cells(element);
                if (currentCells + cells > width && currentCells > 0)
                {
                    into.Add(current.ToString());
                    current.Clear();
                    currentCells = 0;
                }
                current.Append(element);
                currentCells += cells;
            }
        }

        if (currentCells > 0 || current.Length > 0) into.Add(current.ToString());
    }

    /// <summary>Terminal cells a string takes, counting wide characters (CJK, emoji) as two.</summary>
    public static int Cells(string s)
    {
        int cells = 0;
        var elements = StringInfo.GetTextElementEnumerator(s);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            var rune = System.Text.Rune.GetRuneAt(element, 0);
            cells += IsWide(rune.Value) || element.Length > rune.Utf16SequenceLength ? 2 : 1;
        }
        return cells;
    }

    private static bool IsWide(int cp) =>
        cp >= 0x1100 && (
            cp <= 0x115F ||
            (cp >= 0x2E80 && cp <= 0xA4CF) ||
            (cp >= 0xAC00 && cp <= 0xD7A3) ||
            (cp >= 0xF900 && cp <= 0xFAFF) ||
            (cp >= 0xFE30 && cp <= 0xFE4F) ||
            (cp >= 0xFF00 && cp <= 0xFF60) ||
            (cp >= 0xFFE0 && cp <= 0xFFE6) ||
            (cp >= 0x1F300 && cp <= 0x1FAFF) ||
            (cp >= 0x20000 && cp <= 0x3FFFD));
}
