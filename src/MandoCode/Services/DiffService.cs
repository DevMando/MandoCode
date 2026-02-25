using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Computes line-by-line diffs using the LCS (Longest Common Subsequence) algorithm.
/// </summary>
public static class DiffService
{
    /// <summary>
    /// Computes a diff between old and new file content.
    /// </summary>
    /// <param name="oldContent">Existing file content, or null for new files.</param>
    /// <param name="newContent">New file content to write.</param>
    /// <returns>List of diff lines showing additions, removals, and unchanged lines.</returns>
    public static List<DiffLine> ComputeDiff(string? oldContent, string newContent)
    {
        var oldLines = oldContent != null
            ? SplitLines(oldContent)
            : Array.Empty<string>();

        var newLines = SplitLines(newContent);

        if (oldLines.Length == 0)
        {
            // New file — all lines are additions
            return newLines.Select((line, i) => new DiffLine
            {
                LineType = DiffLineType.Added,
                Content = line,
                NewLineNumber = i + 1
            }).ToList();
        }

        // Compute LCS table
        var lcs = ComputeLcsTable(oldLines, newLines);

        // Build diff from LCS
        return BuildDiff(oldLines, newLines, lcs);
    }

    /// <summary>
    /// Splits content into lines, normalizing line endings.
    /// </summary>
    private static string[] SplitLines(string content)
    {
        return content.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
    }

    /// <summary>
    /// Computes the LCS length table using dynamic programming.
    /// </summary>
    private static int[,] ComputeLcsTable(string[] oldLines, string[] newLines)
    {
        var m = oldLines.Length;
        var n = newLines.Length;
        var table = new int[m + 1, n + 1];

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (oldLines[i - 1] == newLines[j - 1])
                {
                    table[i, j] = table[i - 1, j - 1] + 1;
                }
                else
                {
                    table[i, j] = Math.Max(table[i - 1, j], table[i, j - 1]);
                }
            }
        }

        return table;
    }

    /// <summary>
    /// Builds the diff output by backtracking through the LCS table.
    /// </summary>
    private static List<DiffLine> BuildDiff(string[] oldLines, string[] newLines, int[,] lcs)
    {
        var diff = new List<DiffLine>();
        int i = oldLines.Length;
        int j = newLines.Length;

        // Backtrack through LCS table to produce diff
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && oldLines[i - 1] == newLines[j - 1])
            {
                // Lines match — unchanged
                diff.Add(new DiffLine
                {
                    LineType = DiffLineType.Unchanged,
                    Content = oldLines[i - 1],
                    OldLineNumber = i,
                    NewLineNumber = j
                });
                i--;
                j--;
            }
            else if (j > 0 && (i == 0 || lcs[i, j - 1] >= lcs[i - 1, j]))
            {
                // Line added in new version
                diff.Add(new DiffLine
                {
                    LineType = DiffLineType.Added,
                    Content = newLines[j - 1],
                    NewLineNumber = j
                });
                j--;
            }
            else if (i > 0)
            {
                // Line removed from old version
                diff.Add(new DiffLine
                {
                    LineType = DiffLineType.Removed,
                    Content = oldLines[i - 1],
                    OldLineNumber = i
                });
                i--;
            }
        }

        // Reverse since we built it backwards
        diff.Reverse();
        return diff;
    }

    /// <summary>
    /// Filters diff lines to show only changed lines and surrounding context.
    /// Collapses long unchanged sections with a summary marker.
    /// </summary>
    /// <param name="diffLines">Full diff output.</param>
    /// <param name="contextLines">Number of context lines to show around changes.</param>
    /// <returns>Filtered diff lines with collapse markers for long unchanged sections.</returns>
    public static List<DiffLine> CollapseContext(List<DiffLine> diffLines, int contextLines = 3)
    {
        if (diffLines.Count == 0)
            return diffLines;

        // Find indices of all changed lines
        var changedIndices = new HashSet<int>();
        for (int i = 0; i < diffLines.Count; i++)
        {
            if (diffLines[i].LineType != DiffLineType.Unchanged)
            {
                changedIndices.Add(i);
            }
        }

        // If no changes, return empty (shouldn't happen in practice)
        if (changedIndices.Count == 0)
            return diffLines;

        // Build set of indices to show (changed lines + context)
        var showIndices = new HashSet<int>();
        foreach (var idx in changedIndices)
        {
            for (int c = -contextLines; c <= contextLines; c++)
            {
                var target = idx + c;
                if (target >= 0 && target < diffLines.Count)
                {
                    showIndices.Add(target);
                }
            }
        }

        // Build result with collapse markers
        var result = new List<DiffLine>();
        int lastShown = -1;

        for (int i = 0; i < diffLines.Count; i++)
        {
            if (showIndices.Contains(i))
            {
                // If there's a gap, add a collapse marker
                if (lastShown >= 0 && i - lastShown > 1)
                {
                    var skipped = i - lastShown - 1;
                    result.Add(new DiffLine
                    {
                        LineType = DiffLineType.Unchanged,
                        Content = $"... ({skipped} lines unchanged) ..."
                    });
                }

                result.Add(diffLines[i]);
                lastShown = i;
            }
        }

        // If there are trailing unchanged lines not shown
        if (lastShown < diffLines.Count - 1)
        {
            var skipped = diffLines.Count - 1 - lastShown;
            if (skipped > 0)
            {
                result.Add(new DiffLine
                {
                    LineType = DiffLineType.Unchanged,
                    Content = $"... ({skipped} lines unchanged) ..."
                });
            }
        }

        return result;
    }
}
