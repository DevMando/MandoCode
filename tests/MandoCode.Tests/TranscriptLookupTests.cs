using MandoCode.Services;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public sealed class TranscriptLookupTests
{
    [Fact]
    public void AllEntryKindsResolve_AndClearingDropsHistoryButKeepsRegisteredContent()
    {
        var session = new TuiSession();
        var first = new Text("First");
        session.Append(first);
        session.AppendSpaced(new Text("Spaced"));
        session.AppendUserPrompt("Prompt");
        session.AppendRestored(new Text("Restored"), null, true);
        using (session.CaptureToolOutput(true)) session.Append(new Text("Tool"));
        for (var i = 0; i < 10000; i++) session.Append(new Text($"Line {i}"));
        var entries = session.Snapshot().Entries;
        Assert.All(entries, entry => Assert.Same(entry.Content, session.Find(entry.Id)));
        var registered = new Text("Registered");
        var registeredId = session.Register(registered);
        session.Clear();
        Assert.All(entries, entry => Assert.Null(session.Find(entry.Id)));
        Assert.Same(registered, session.Find(registeredId));
        session.Unregister(registeredId);
        Assert.Null(session.Find(registeredId));
        session.Append(first);
        Assert.Same(first, session.Find(Assert.Single(session.Snapshot().Entries).Id));
    }
}
