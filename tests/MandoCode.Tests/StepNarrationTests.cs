using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// The one-line "what it's doing right now" shown beside a running step.
///
/// A plan step's text is only rendered once the step finishes — streaming exists for the stall
/// watchdog's heartbeat, not for display — so without this a long step is a spinner and nothing
/// else. Observed live: a step sat at "Working…" for four minutes while the model narrated
/// throughout, which read as a hang.
/// </summary>
public class StepNarrationTests
{
    [Fact]
    public void ShowsTheLineCurrentlyBeingWritten()
    {
        var n = new StepNarration();
        n.Append("⚙️ Creating the maze");

        Assert.Equal("⚙️ Creating the maze", n.Latest);
    }

    [Fact]
    public void ReassemblesChunksSplitMidWord()
    {
        // Streamed chunks have no relationship to word or line boundaries.
        var n = new StepNarration();
        n.Append("⚙️ Crea");
        n.Append("ting the ma");
        n.Append("ze");

        Assert.Equal("⚙️ Creating the maze", n.Latest);
    }

    [Fact]
    public void AdvancesToTheNewestLine()
    {
        var n = new StepNarration();
        n.Append("first thing\nsecond thing\nthird thing");

        Assert.Equal("third thing", n.Latest);
    }

    [Fact]
    public void KeepsTheLastLine_WhileBetweenLines()
    {
        // A trailing newline must not blank the display until the next line starts arriving.
        var n = new StepNarration();
        n.Append("doing the thing\n");

        Assert.Equal("doing the thing", n.Latest);
    }

    [Fact]
    public void IgnoresBlankLines()
    {
        var n = new StepNarration();
        n.Append("real content\n\n\n");

        Assert.Equal("real content", n.Latest);
    }

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        var n = new StepNarration();
        n.Append("one\r\ntwo");

        Assert.Equal("two", n.Latest);
    }

    [Fact]
    public void NullUntilSomethingArrives()
    {
        var n = new StepNarration();
        Assert.Null(n.Latest);

        n.Append("");
        n.Append(null);
        n.Append("   \n  ");
        Assert.Null(n.Latest);
    }

    [Fact]
    public void ShortensWithAnEllipsis()
    {
        // A spinner label that wraps corrupts the line the spinner keeps redrawing, so the width
        // budget is a hard limit.
        var n = new StepNarration();
        n.Append(new string('x', 200));

        var shortened = n.Shortened(20);
        Assert.Equal(20, shortened!.Length);
        Assert.EndsWith("…", shortened);
    }

    [Fact]
    public void LeavesShortLinesAlone()
    {
        var n = new StepNarration();
        n.Append("short");

        Assert.Equal("short", n.Shortened(60));
    }

    [Fact]
    public void ShortenedIsNull_WhenThereIsNothingToShow()
    {
        Assert.Null(new StepNarration().Shortened(60));
    }
}
