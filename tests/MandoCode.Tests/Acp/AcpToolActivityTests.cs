using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Protocol;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpToolActivityTests
{
    [Fact]
    public void SparseUpdatesReplaceStatusWithoutDuplicatingTheRowOrLosingInput()
    {
        var session = new TuiSession();
        var store = new AcpToolActivityStore(session.UpdateAcpTool);
        store.BeginTurn();
        store.Apply(new ToolCall { ToolCallId = "1", Title = "Inspect library", Kind = "execute",
            RawInput = JsonSerializer.SerializeToElement(new { command = "Get-Item library.dll" }) });
        var first = Assert.Single(session.Snapshot().Entries);
        var revision = session.Revision;
        store.Apply(new ToolCallUpdate { ToolCallId = "1", Status = "in_progress" });
        Assert.Equal("Running", first.AcpTool!.Snapshot.Status);
        store.Apply(new ToolCallUpdate { ToolCallId = "1", Status = "completed" });
        var last = Assert.Single(session.Snapshot().Entries);
        Assert.Equal(first.Id, last.Id);
        Assert.Same(first.AcpTool, last.AcpTool);
        Assert.Equal("Inspect library", last.AcpTool!.Snapshot.Title);
        Assert.Contains("Get-Item library.dll", last.AcpTool.Snapshot.Input);
        Assert.Equal("Completed", last.AcpTool.Snapshot.Status);
        Assert.True(session.Revision > revision);
    }

    [Fact]
    public void InterleavedCallsAndEarlyUpdatesKeepTheirOwnIdentities()
    {
        var session = new TuiSession();
        var store = new AcpToolActivityStore(session.UpdateAcpTool);
        store.Apply(new ToolCallUpdate { ToolCallId = "a", Status = "in_progress" });
        store.Apply(new ToolCall { ToolCallId = "b", Title = "Second call" });
        store.Apply(new ToolCall { ToolCallId = "a", Title = "First call", Kind = "read" });
        store.Apply(new ToolCallUpdate { ToolCallId = "b", Status = "failed" });
        var entries = session.Snapshot().Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("First call", entries[0].AcpTool!.Snapshot.Title);
        Assert.Equal("Running", entries[0].AcpTool!.Snapshot.Status);
        Assert.Equal("Failed", entries[1].AcpTool!.Snapshot.Status);
    }

    [Fact]
    public void PermissionDenialCannotBeOverwrittenByALateCompletion()
    {
        var session = new TuiSession();
        var store = new AcpToolActivityStore(session.UpdateAcpTool);
        store.Apply(new ToolCall { ToolCallId = "x", Title = "Write a file" });
        Assert.Equal("Pending", Assert.Single(session.Snapshot().Entries).AcpTool!.Snapshot.Status);
        store.Permission("x", null);
        store.Apply(new ToolCallUpdate { ToolCallId = "x", Status = "in_progress" });
        Assert.Equal("Awaiting approval", Assert.Single(session.Snapshot().Entries).AcpTool!.Snapshot.Status);
        store.Permission("x", false);
        store.Apply(new ToolCallUpdate { ToolCallId = "x", Status = "completed" });
        Assert.Equal("Permission denied", Assert.Single(session.Snapshot().Entries).AcpTool!.Snapshot.Status);
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Interrupted")]
    [InlineData("Unfinished")]
    public void EndTurnSettlesOnlyUnfinishedCallsAndIgnoresLateUpdates(string status)
    {
        var session = new TuiSession();
        var store = new AcpToolActivityStore(session.UpdateAcpTool);
        store.Apply(new ToolCall { ToolCallId = "done", Status = "completed" });
        store.Apply(new ToolCall { ToolCallId = "pending" });
        store.EndTurn(status);
        store.Apply(new ToolCallUpdate { ToolCallId = "pending", Status = "completed" });
        store.Apply(new ToolCall { ToolCallId = "late" });
        var entries = session.Snapshot().Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("Completed", entries[0].AcpTool!.Snapshot.Status);
        Assert.Equal(status, entries[1].AcpTool!.Snapshot.Status);
    }

    [Fact]
    public void ReusedIdsInAnotherTurnOrPaneDoNotChangeEarlierRows()
    {
        var first = new TuiSession();
        var store = new AcpToolActivityStore(first.UpdateAcpTool);
        store.Apply(new ToolCall { ToolCallId = "1", Title = "Earlier", Status = "completed" });
        store.EndTurn("Unfinished");
        store.BeginTurn();
        store.Apply(new ToolCall { ToolCallId = "1", Title = "Later" });
        var second = new TuiSession();
        new AcpToolActivityStore(second.UpdateAcpTool).Apply(new ToolCall { ToolCallId = "1", Title = "Other pane" });
        Assert.Equal(2, first.Snapshot().Entries.Count);
        Assert.Equal("Earlier", first.Snapshot().Entries[0].AcpTool!.Snapshot.Title);
        Assert.Equal("Completed", first.Snapshot().Entries[0].AcpTool!.Snapshot.Status);
        Assert.Equal("Other pane", Assert.Single(second.Snapshot().Entries).AcpTool!.Snapshot.Title);
    }

    [Fact]
    public void OutputIsBoundedDeduplicatedAndTerminalAvailabilityIsOnlyADetail()
    {
        var session = new TuiSession();
        var store = new AcpToolActivityStore(session.UpdateAcpTool);
        store.Apply(new ToolCall { ToolCallId = "1", Content =
            [new ContentToolCallContent { Content = new TextContent("hello") }, new TerminalToolCallContent { TerminalId = "terminal" }] });
        store.Apply(new ToolCallUpdate { ToolCallId = "1", Content = [new ContentToolCallContent { Content = new TextContent("hello") }] });
        Assert.Equal("hello", Assert.Single(Assert.Single(session.Snapshot().Entries).AcpTool!.Snapshot.Output));
        store.Apply(new ToolCallUpdate { ToolCallId = "1", RawOutput = JsonSerializer.SerializeToElement(new string('x', 100_000)) });
        var activity = Assert.Single(session.Snapshot().Entries).AcpTool!;
        Assert.True(activity.Snapshot.Output.Sum(text => text.Length) <= 32_000);
        Assert.True(activity.Snapshot.OutputUnavailable);
        Assert.Contains("Terminal output unavailable", Plain.Of(activity));
    }

    private sealed class Sink : IAcpTurnSink
    {
        public TuiSession Session { get; } = new();
        private AcpToolActivityStore? _store;
        public List<IRenderable> Blocks { get; } = [];
        public void BeginToolActivities() { _store = new(Session.UpdateAcpTool); _store.BeginTurn(); }
        public bool ToolCall(ToolCallBase call) { _store!.Apply(call); return true; }
        public void ToolDetail(string id, IRenderable detail) => _store!.Detail(id, detail);
        public void EndToolActivities(string status) => _store!.EndTurn(status);
        public void Preview(string? tail) { }
        public void Activity(string label) { }
        public void Reply(string markdown) => Session.Append(new Text(markdown));
        public void Block(IRenderable block) => Blocks.Add(block);
        public void Notice(string text) { }
        public void Error(string text) { }
    }

    [Theory]
    [InlineData("end_turn", "Unfinished")]
    [InlineData("cancelled", "Cancelled")]
    public async Task DriverSettlesIncompleteActivitiesWithoutAppendingLegacyLines(string stop, string expected)
    {
        var sink = new Sink();
        var runtime = new FakeRuntime();
        var driver = new AcpTurnDriver(sink, transcript: new AcpTranscriptAdapter(_ => new Text("diff"), Path.GetTempPath()));
        var run = driver.RunAsync(runtime, [new TextContent("test")], default);
        runtime.Send(FakeRuntime.Text("Before"));
        runtime.Send(new ToolCall { ToolCallId = "1", Title = "Run command", Kind = "execute", Status = "in_progress" });
        runtime.Send(FakeRuntime.Text("After"));
        runtime.Succeed(stop);
        await run.Within();
        var entries = sink.Session.Snapshot().Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal("Before", Plain.Of(entries[0].Content));
        Assert.Equal(expected, entries[1].AcpTool!.Snapshot.Status);
        Assert.Equal("After", Plain.Of(entries[2].Content));
        Assert.Empty(sink.Blocks);
    }

    [Fact]
    public async Task CompletedDiffRemainsAttachedToItsActivity()
    {
        var sink = new Sink();
        var runtime = new FakeRuntime();
        var driver = new AcpTurnDriver(sink, transcript: new AcpTranscriptAdapter(_ => new Text("file diff"), Path.GetTempPath()));
        var run = driver.RunAsync(runtime, [new TextContent("test")], default);
        runtime.Send(new ToolCall { ToolCallId = "edit", Title = "Edit file", Kind = "edit", Content =
            [new DiffToolCallContent { Path = Path.Combine(Path.GetTempPath(), "activity.txt"), OldText = "old", NewText = "new" }] });
        runtime.Send(new ToolCallUpdate { ToolCallId = "edit", Status = "completed" });
        runtime.Succeed(StopReasons.EndTurn);
        await run.Within();
        var activity = Assert.Single(sink.Session.Snapshot().Entries).AcpTool!;
        Assert.Equal("Completed", activity.Snapshot.Status);
        Assert.Equal("file diff", Plain.Of(Assert.Single(activity.Snapshot.Details)));
        Assert.Empty(sink.Blocks);
    }

    [Fact]
    public async Task DriverMarksInterruptedCallsWhenTheRuntimeFails()
    {
        var sink = new Sink();
        var runtime = new FakeRuntime();
        var run = new AcpTurnDriver(sink).RunAsync(runtime, [new TextContent("test")], default);
        runtime.Send(new ToolCall { ToolCallId = "1", Status = "in_progress" });
        runtime.Fail(new IOException("connection failed"));
        Assert.NotNull((await run.Within()).Error);
        Assert.Equal("Interrupted", Assert.Single(sink.Session.Snapshot().Entries).AcpTool!.Snapshot.Status);
    }
}
