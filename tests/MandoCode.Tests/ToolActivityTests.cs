using MandoCode.Services;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public class ToolActivityTests
{
    [Fact]
    public void CollapsePreservesOutputAndKeepsRepliesOutsideActivity()
    {
        var session = new TuiSession();
        session.BeginToolTurn();
        using (session.CaptureToolOutput(true)) session.Append(new Text("Reading file"));
        session.Append(new Text("Assistant explanation"));
        using (session.CaptureToolOutput(false)) session.Append(new Text("Done"));
        using (session.CaptureToolOutput(true)) session.Append(new Text("Running command"));
        using (session.CaptureToolOutput(false, false)) session.Append(new Text("Error details"));
        session.CompleteToolTurn();
        var entries = session.Snapshot().Entries;
        var activity = entries[0].ToolActivity!;
        Assert.Equal("2 tool calls · 1 completed · 1 failed", activity.Summary);
        Assert.True(activity.Finished);
        Assert.False(activity.Expanded);
        Assert.Null(entries[2].ToolOutput);
        Assert.Same(activity, entries[1].ToolOutput);
        activity.Expanded = true;
        Assert.Equal(6, entries.Count);
        session.BeginToolTurn();
        using (session.CaptureToolOutput(true)) session.Append(new Text("New turn"));
        session.CompleteToolTurn();
        Assert.NotSame(activity, session.Snapshot().Entries[6].ToolActivity);
        Assert.Contains("1 unfinished", session.Snapshot().Entries[6].ToolActivity!.Summary);
    }
}
