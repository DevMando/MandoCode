using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for DiffService — a static class with pure functions,
/// which makes it the easiest kind of code to test.
///
/// No setup needed: static methods = no constructor, no state.
/// Just call the method and check the output.
/// </summary>
public class DiffServiceTests
{
    // ════════════════════════════════════════════════════════════
    //  1. NEW FILE DIFFS
    //     When oldContent is null, every line is an addition.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void NewFile_AllLinesAreAdded()
    {
        var result = DiffService.ComputeDiff(null, "line1\nline2\nline3");

        Assert.Equal(3, result.Count);
        Assert.All(result, line => Assert.Equal(DiffLineType.Added, line.LineType));
    }

    [Fact]
    public void NewFile_LineNumbersAreSequential()
    {
        var result = DiffService.ComputeDiff(null, "a\nb\nc");

        Assert.Equal(1, result[0].NewLineNumber);
        Assert.Equal(2, result[1].NewLineNumber);
        Assert.Equal(3, result[2].NewLineNumber);
    }

    [Fact]
    public void NewFile_PreservesContent()
    {
        var result = DiffService.ComputeDiff(null, "hello world");

        Assert.Single(result);
        Assert.Equal("hello world", result[0].Content);
    }

    // ════════════════════════════════════════════════════════════
    //  2. IDENTICAL FILES
    //     No changes = all lines unchanged.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void IdenticalContent_AllLinesUnchanged()
    {
        var content = "line1\nline2\nline3";

        var result = DiffService.ComputeDiff(content, content);

        Assert.Equal(3, result.Count);
        Assert.All(result, line => Assert.Equal(DiffLineType.Unchanged, line.LineType));
    }

    [Fact]
    public void IdenticalContent_HasBothLineNumbers()
    {
        var result = DiffService.ComputeDiff("a\nb", "a\nb");

        // Unchanged lines should have BOTH old and new line numbers
        Assert.Equal(1, result[0].OldLineNumber);
        Assert.Equal(1, result[0].NewLineNumber);
        Assert.Equal(2, result[1].OldLineNumber);
        Assert.Equal(2, result[1].NewLineNumber);
    }

    // ════════════════════════════════════════════════════════════
    //  3. LINE MODIFICATIONS
    //     The LCS algorithm shows removes + adds for changed lines.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void SingleLineChange_ShowsRemoveAndAdd()
    {
        var old = "hello\nworld";
        var @new = "hello\nearth";

        var result = DiffService.ComputeDiff(old, @new);

        // "hello" unchanged, "world" removed, "earth" added
        Assert.Equal(DiffLineType.Unchanged, result[0].LineType);
        Assert.Equal("hello", result[0].Content);

        var removed = result.First(l => l.LineType == DiffLineType.Removed);
        Assert.Equal("world", removed.Content);

        var added = result.First(l => l.LineType == DiffLineType.Added);
        Assert.Equal("earth", added.Content);
    }

    [Fact]
    public void AddedLine_AppearsInDiff()
    {
        var old = "a\nc";
        var @new = "a\nb\nc";

        var result = DiffService.ComputeDiff(old, @new);

        // "a" unchanged, "b" added, "c" unchanged
        var added = result.Where(l => l.LineType == DiffLineType.Added).ToList();
        Assert.Single(added);
        Assert.Equal("b", added[0].Content);
    }

    [Fact]
    public void RemovedLine_AppearsInDiff()
    {
        var old = "a\nb\nc";
        var @new = "a\nc";

        var result = DiffService.ComputeDiff(old, @new);

        var removed = result.Where(l => l.LineType == DiffLineType.Removed).ToList();
        Assert.Single(removed);
        Assert.Equal("b", removed[0].Content);
    }

    // ════════════════════════════════════════════════════════════
    //  4. LINE ENDING NORMALIZATION
    //     \r\n and \r should be treated the same as \n.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void WindowsLineEndings_NormalizedToUnix()
    {
        var old = "a\r\nb\r\nc";
        var @new = "a\nb\nc";

        var result = DiffService.ComputeDiff(old, @new);

        // Should be identical after normalization
        Assert.All(result, line => Assert.Equal(DiffLineType.Unchanged, line.LineType));
    }

    [Fact]
    public void OldMacLineEndings_NormalizedToUnix()
    {
        var old = "a\rb\rc";
        var @new = "a\nb\nc";

        var result = DiffService.ComputeDiff(old, @new);

        Assert.All(result, line => Assert.Equal(DiffLineType.Unchanged, line.LineType));
    }

    // ════════════════════════════════════════════════════════════
    //  5. EMPTY CONTENT EDGE CASES
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyOldContent_AllLinesAdded()
    {
        var result = DiffService.ComputeDiff("", "new content");

        // Empty string splits to [""], so old has 1 empty line, new has 1 line
        var added = result.Where(l => l.LineType == DiffLineType.Added).ToList();
        Assert.True(added.Count >= 1);
    }

    [Fact]
    public void EmptyNewContent_AllLinesRemoved()
    {
        var result = DiffService.ComputeDiff("old content", "");

        var removed = result.Where(l => l.LineType == DiffLineType.Removed).ToList();
        Assert.True(removed.Count >= 1);
    }

    // ════════════════════════════════════════════════════════════
    //  6. LARGE FILE GUARD
    //     When old * new lines > 25M, the algorithm falls back
    //     to a sampled diff instead of running the full LCS.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void LargeFile_UseseSampledFallback()
    {
        // Create content large enough to trigger the guard:
        // 6000 * 6000 = 36M > 25M threshold
        var oldLines = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"old line {i}"));
        var newLines = string.Join("\n", Enumerable.Range(1, 6000).Select(i => $"new line {i}"));

        var result = DiffService.ComputeDiff(oldLines, newLines);

        // Sampled diff should contain an "omitted" marker
        Assert.Contains(result, l => l.Content.Contains("omitted"));
    }

    [Fact]
    public void SmallFile_DoesNotUseFallback()
    {
        var old = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var @new = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var result = DiffService.ComputeDiff(old, @new);

        // No omission markers
        Assert.DoesNotContain(result, l => l.Content.Contains("omitted"));
    }

    // ════════════════════════════════════════════════════════════
    //  7. CONTEXT COLLAPSE
    //     CollapseContext() hides long unchanged sections,
    //     keeping only N lines of context around changes.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void CollapseContext_HidesDistantUnchangedLines()
    {
        // Build a diff: 20 unchanged, 1 changed, 20 unchanged
        var lines = new List<DiffLine>();

        for (int i = 0; i < 20; i++)
            lines.Add(new DiffLine { LineType = DiffLineType.Unchanged, Content = $"line {i}" });

        lines.Add(new DiffLine { LineType = DiffLineType.Added, Content = "new line" });

        for (int i = 21; i < 41; i++)
            lines.Add(new DiffLine { LineType = DiffLineType.Unchanged, Content = $"line {i}" });

        // Act — collapse with 3 lines of context (default)
        var result = DiffService.CollapseContext(lines, contextLines: 3);

        // Should be much shorter than 41 lines
        Assert.True(result.Count < lines.Count,
            $"Expected fewer lines after collapse, got {result.Count} vs {lines.Count}");

        // The added line should still be present
        Assert.Contains(result, l => l.Content == "new line" && l.LineType == DiffLineType.Added);

        // Should have collapse markers
        Assert.Contains(result, l => l.Content.Contains("lines unchanged"));
    }

    [Fact]
    public void CollapseContext_PreservesContextAroundChanges()
    {
        var lines = new List<DiffLine>();

        // 10 unchanged lines before the change
        for (int i = 0; i < 10; i++)
            lines.Add(new DiffLine { LineType = DiffLineType.Unchanged, Content = $"before {i}" });

        lines.Add(new DiffLine { LineType = DiffLineType.Added, Content = "THE CHANGE" });

        // 10 unchanged lines after
        for (int i = 0; i < 10; i++)
            lines.Add(new DiffLine { LineType = DiffLineType.Unchanged, Content = $"after {i}" });

        var result = DiffService.CollapseContext(lines, contextLines: 2);

        // Lines immediately around the change should be preserved
        Assert.Contains(result, l => l.Content == "before 8");  // 2 lines before change
        Assert.Contains(result, l => l.Content == "before 9");  // 1 line before change
        Assert.Contains(result, l => l.Content == "THE CHANGE");
        Assert.Contains(result, l => l.Content == "after 0");   // 1 line after change
        Assert.Contains(result, l => l.Content == "after 1");   // 2 lines after change

        // Distant lines should NOT be in the result
        Assert.DoesNotContain(result, l => l.Content == "before 0");
    }

    [Fact]
    public void CollapseContext_EmptyInput_ReturnsEmpty()
    {
        var result = DiffService.CollapseContext(new List<DiffLine>());

        Assert.Empty(result);
    }

    [Fact]
    public void CollapseContext_AllChanged_ReturnsAll()
    {
        var lines = new List<DiffLine>
        {
            new() { LineType = DiffLineType.Added, Content = "a" },
            new() { LineType = DiffLineType.Removed, Content = "b" },
            new() { LineType = DiffLineType.Added, Content = "c" },
        };

        var result = DiffService.CollapseContext(lines);

        // No collapsing needed — all lines are changes
        Assert.Equal(3, result.Count);
    }

    // ════════════════════════════════════════════════════════════
    //  8. REALISTIC SCENARIOS
    //     Tests that mirror real-world editing patterns.
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void RealWorld_AddMethodToClass()
    {
        var old = """
            public class Foo
            {
                public void Bar() { }
            }
            """;

        var @new = """
            public class Foo
            {
                public void Bar() { }

                public void Baz() { }
            }
            """;

        var result = DiffService.ComputeDiff(old, @new);

        var added = result.Where(l => l.LineType == DiffLineType.Added).ToList();
        Assert.True(added.Count >= 1);
        Assert.Contains(added, l => l.Content.Contains("Baz"));
    }

    [Fact]
    public void RealWorld_ChangeReturnType()
    {
        var old = "public int Calculate() => 42;";
        var @new = "public string Calculate() => \"42\";";

        var result = DiffService.ComputeDiff(old, @new);

        Assert.Contains(result, l => l.LineType == DiffLineType.Removed && l.Content.Contains("int"));
        Assert.Contains(result, l => l.LineType == DiffLineType.Added && l.Content.Contains("string"));
    }
}
