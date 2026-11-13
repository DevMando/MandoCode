using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public sealed class TranscriptSnapshotPerformanceTests
{
    [Fact]
    public void StatusUpdatesReuseHistoryWhileNewOutputAndClearInvalidateIt()
    {
        var session = new TuiSession();
        for (var i = 0; i < 1000; i++) session.Append(new Text($"History {i}"));
        var before = session.Snapshot();
        session.SetRunning(true, "Working");
        session.SetPreview("New streaming preview");
        var during = session.Snapshot();
        Assert.Same(before.Entries, during.Entries);
        Assert.True(during.Running);
        Assert.Equal("New streaming preview", during.Preview);
        session.AppendUserPrompt("Next question");
        var appended = session.Snapshot();
        Assert.NotSame(before.Entries, appended.Entries);
        Assert.Equal(1000, before.Entries.Count);
        Assert.Equal("Next question", appended.Entries[^1].UserPrompt);
        session.Clear();
        Assert.Empty(session.Snapshot().Entries);
        Assert.Equal(1001, appended.Entries.Count);
        session.Append(new Text("Fresh conversation"));
        Assert.Single(session.Snapshot().Entries);
    }

    [Fact]
    public void UnchangedHistoryKeepsGroupsWhileLateAgentDetailsRemainVisible()
    {
        var session = new TuiSession();
        session.AppendAgentDetail("peer", "Other agent", new Text("First detail"), "Working");
        var view = new ConversationView();
        void Apply()
        {
            typeof(ConversationView).GetProperty(nameof(ConversationView.Entries))!.SetValue(view, session.Snapshot().Entries);
            typeof(ConversationView).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        }
        var detailsField = typeof(ConversationView).GetField("_details", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Apply();
        var before = (IReadOnlyDictionary<AgentExchange, TuiEntry[]>)detailsField.GetValue(view)!;
        var exchange = Assert.Single(before.Keys);
        exchange.Expanded = true;
        session.SetActivity("Waiting");
        Apply();
        Assert.Same(before, detailsField.GetValue(view));
        Assert.True(exchange.Expanded);
        session.AppendAgentDetail("peer", "Other agent", new Text("Late reply"), "Completed");
        Apply();
        var after = (IReadOnlyDictionary<AgentExchange, TuiEntry[]>)detailsField.GetValue(view)!;
        Assert.NotSame(before, after);
        Assert.Equal(2, after[exchange].Length);
        Assert.Equal("Completed", exchange.Status);
    }
}
