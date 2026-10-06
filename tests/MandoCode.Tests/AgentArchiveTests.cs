using System.Text.Json;
using System.Reflection;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public sealed class AgentArchiveTests
{
    [Fact]
    public async Task AltHOpensSelectedIdleAgentsHistoryAndClosesExistingHistory()
    {
        using var fixture = new ArchiveFolder();
        await using var provider = Services(fixture.Store);
        var workspace = provider.GetRequiredService<WorkspaceRegistry>().Active;
        var inactive = workspace.Add(); var selected = workspace.Add();
        string? command = null; var closed = false;
        selected.SubmitCommand = value => { command = value; return Task.CompletedTask; };
        selected.CloseHistory = () => closed = true;
        var shortcut = new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "h", AltKey = true };
        workspace.Key(inactive, shortcut); Assert.Null(command);
        selected.IsBusy = () => true;
        workspace.Key(selected, shortcut); Assert.Null(command);
        selected.IsBusy = () => false;
        selected.IsAwaitingInput = () => true;
        workspace.Key(selected, shortcut); Assert.Null(command);
        selected.IsAwaitingInput = () => false;
        workspace.Key(selected, shortcut); Assert.Equal("/history", command);
        command = null;
        selected.IsHistoryOpen = () => true;
        workspace.Key(selected, shortcut); Assert.True(closed); Assert.Null(command);
        Assert.Contains("Alt+H", MandoCode.Models.SlashCommands.All["/history"]);
    }
    internal static ArchivedAgent Sample(string? key = null, string name = "Jetik") => new(
        key ?? Guid.NewGuid().ToString("N"), name, Path.GetTempPath(), "Main", DateTimeOffset.Now,
        new("model:latest", 0.3, 2048, 32768) { ContextLengthSetByUser = true },
        [new() { Role = "user", Text = "Build a calculator" }, new() { Role = "assistant", Text = "The calculator is ready." }],
        [], null, 9000, 1000, new() { PromptTokens = 8000, CompletionTokens = 300 }) { MessageCount = 2 };

    [Fact]
    public void SeparateAgentsInSameFolderAreDurableAndSearchableAcrossLaunches()
    {
        using var fixture = new ArchiveFolder();
        var first = Sample(); var second = Sample(name: "Voxel");
        Assert.True(fixture.Store.Save(first)); Assert.True(fixture.Store.Save(second));
        var reopened = new AgentArchiveStore(fixture.Path);
        Assert.Equal(2, reopened.Closed().Count);
        Assert.Equal(first.Key, reopened.Closed("jetik").Single().Key);
        Assert.Equal(2, reopened.Closed("calculator").Count);
        var saved = reopened.Load(first.Key)!;
        Assert.Equal(9000, saved.PromptTokens); Assert.Equal(32768, saved.Settings.ContextLength);
        Assert.Equal(2, reopened.Closed().First().MessageCount);
        Assert.True(reopened.MarkOpen(saved));
        Assert.DoesNotContain(reopened.Closed(), a => a.Key == first.Key);
        Assert.True(reopened.Save(saved with { ClosedAt = DateTimeOffset.Now }));
        Assert.Equal(2, reopened.Closed().Count);
    }

    [Fact]
    public void FullTextSearchReadsConversationBeyondPreview()
    {
        using var fixture = new ArchiveFolder();
        var archive = Sample();
        archive.Messages.Insert(1, new() { Role = "user", Text = "A hidden middle message about zebras" });
        Assert.True(fixture.Store.Save(archive));
        Assert.Single(fixture.Store.Closed("zebras"));
        Assert.Empty(fixture.Store.Closed("giraffes"));
    }

    [Fact]
    public void RetentionBoundsClosedAgentsAndClearRemovesTheirFiles()
    {
        using var fixture = new ArchiveFolder();
        var live = Sample() with { ClosedAt = null };
        Assert.True(fixture.Store.Save(live));
        for (var i = 0; i < 62; i++) Assert.True(fixture.Store.Save(Sample() with { ClosedAt = DateTimeOffset.Now.AddMinutes(i) }));
        Assert.Equal(60, fixture.Store.Closed().Count);
        Assert.NotNull(fixture.Store.Load(live.Key));
        var deleted = fixture.Store.Closed().First();
        Assert.True(fixture.Store.DeleteClosed(deleted.Key));
        Assert.Null(fixture.Store.Load(deleted.Key));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, deleted.Key + ".json.meta")));
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, Guid.NewGuid().ToString("N") + ".json"), "corrupt");
        Assert.Equal(59, fixture.Store.Closed().Count);
        Assert.False(fixture.Store.Save(Sample("../escape")));
    }

    [Fact]
    public void ClaimedAgentCannotBeRestoredOrDeletedInAnotherWindow()
    {
        using var fixture = new ArchiveFolder();
        var archive = Sample(); fixture.Store.Save(archive);
        var other = new AgentArchiveStore(fixture.Path);
        using (var claim = fixture.Store.Claim(archive.Key))
        {
            Assert.NotNull(claim); Assert.Null(other.Claim(archive.Key));
            Assert.False(other.DeleteClosed(archive.Key)); Assert.NotNull(other.Load(archive.Key));
        }
        using var released = other.Claim(archive.Key);
        Assert.NotNull(released);
    }

    [Fact]
    public void TranscriptRestoresStyledPromptsToolsRepliesAndSpacingWithoutStartupOrSpinner()
    {
        var session = new TuiSession();
        session.Append(new Text("startup")); session.AppendUserPrompt("make changes");
        session.Append(new Markup("[cyan]Read file.cs[/]"));
        session.AppendSpaced(new Markup("[green]Jetik:[/] done [bold]successfully[/]"));
        session.SetRunning(true, "working");
        var archive = Sample() with { Transcript = AgentArchiveStore.Capture(session) };
        Assert.Equal(3, archive.Transcript.Count);
        Assert.Contains(archive.Transcript[1].Spans, s => Style.Parse(s.Style).Foreground == Color.Aqua);
        var restored = new TuiSession(); AgentArchiveStore.Replay(archive, restored);
        Assert.False(restored.Snapshot().Running);
        Assert.Equal("make changes", restored.Snapshot().Entries[0].UserPrompt);
        Assert.True(restored.Snapshot().Entries[^1].SpaceAfter);
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
        console.Profile.Width = 30;
        foreach (var entry in restored.Snapshot().Entries) console.Write(entry.Content);
        Assert.Contains("Read file.cs", writer.ToString()); Assert.Contains("successfully", writer.ToString());
        Assert.DoesNotContain("startup", writer.ToString());
    }

    [Fact]
    public void PeerOnlyTranscriptRestoresCollapsedGroupsAndTheirDetails()
    {
        var session = new TuiSession();
        session.Append(new Text("startup"));
        session.AppendAgentExchange("Fusion · Question\nRequest", Color.Purple, "q1", "Working");
        using (session.CaptureAgentExchange("q1")) session.AppendSpaced(new Text("Reply"));
        var original = session.Snapshot().Entries[1].AgentActivity!;
        original.Expanded = true;
        var archive = Sample() with { Transcript = AgentArchiveStore.Capture(session) };
        archive = JsonSerializer.Deserialize<ArchivedAgent>(JsonSerializer.Serialize(archive))!;
        var restored = new TuiSession();
        AgentArchiveStore.Replay(archive, restored);
        var entries = restored.Snapshot().Entries;
        Assert.Equal(3, entries.Count);
        var group = entries[0].AgentActivity!;
        Assert.False(group.Expanded);
        Assert.All(entries.Skip(1), entry => Assert.Same(group, entry.AgentOutput));
        restored.AppendAgentExchange("New question\nNew request", Color.Purple, "q1", "Working");
        Assert.Equal(2, restored.Snapshot().Entries.Count(e => e.AgentActivity is not null));
    }

    [Fact]
    public async Task ModelHistoryIncludesToolCallsAndResultsAfterArchiveRoundTrip()
    {
        using var fixture = new ArchiveFolder();
        await using var provider = Services(fixture.Store);
        var pane = provider.GetRequiredService<WorkspaceRegistry>().Active.Add();
        var ai = pane.Services.GetRequiredService<AIService>();
        var history = JsonSerializer.Serialize(new ChatMessage[] {
            new(ChatRole.User, "Read file.cs"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file_contents", new Dictionary<string, object?> { ["path"] = "file.cs" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]), new(ChatRole.Assistant, "done") });
        Assert.Equal(4, ai.TryRestoreHistoryJson(history));
        var archive = Sample() with { HistoryJson = ai.ExportHistoryJson() };
        fixture.Store.Save(archive);
        var second = provider.GetRequiredService<WorkspaceRegistry>().Active.Add();
        var resumed = second.Services.GetRequiredService<AIService>();
        Assert.Equal(4, resumed.TryRestoreHistoryJson(fixture.Store.Load(archive.Key)!.HistoryJson!));
        var restored = JsonSerializer.Deserialize<List<ChatMessage>>(resumed.ExportHistoryJson()!)!;
        Assert.Equal("call-1", restored[1].Contents.OfType<FunctionCallContent>().Single().CallId);
        Assert.Equal("contents", restored[2].Contents.OfType<FunctionResultContent>().Single().Result?.ToString());
    }

    [Fact]
    public async Task RestoreUsesFreePaneOrNewWorkspaceAndFocusesExistingIdentity()
    {
        using var fixture = new ArchiveFolder();
        await using var provider = Services(fixture.Store);
        var registry = provider.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        var archive = Sample(); fixture.Store.Save(archive);
        var pane = registry.Restore(archive)!;
        Assert.Equal(archive.Key, pane.PersistKey); Assert.Equal("Jetik", pane.Name);
        Assert.Equal(32768, pane.Services.GetRequiredService<MandoCodeConfig>().ContextLength);
        Assert.True(pane.Services.GetRequiredService<MandoCodeConfig>().ContextLengthSetByUser);
        Assert.Same(pane, registry.Restore(archive)); Assert.Equal(2, registry.Active.Panes.Count);
        registry.Active.Add(); registry.Active.Add();
        var other = Sample(name: "Voxel"); fixture.Store.Save(other);
        var restored = registry.Restore(other)!;
        Assert.NotSame(pane.Workspace, restored.Workspace);
        Assert.Single(registry.Active.Panes); Assert.Equal(2, registry.Workspaces.Count());
    }

    [Fact]
    public async Task FailedSaveKeepsAgentAndWorkspaceOpen()
    {
        using var fixture = new ArchiveFolder();
        await using var provider = Services(fixture.Store);
        var registry = provider.GetRequiredService<WorkspaceRegistry>();
        var one = registry.Active.Add(); var two = registry.Active.Add();
        two.SaveHistory = _ => false;
        registry.Active.Close(two); Assert.Equal(2, registry.Active.Panes.Count);
        registry.Add(); registry.Switch(one.Workspace); registry.Close();
        Assert.False(one.Workspace.IsClosed); Assert.Same(one.Workspace, registry.Active);
    }

    internal static ServiceProvider Services(AgentArchiveStore store)
    {
        var services = new ServiceCollection().AddLogging();
        Program.RegisterAgentServices(services, new MandoCodeConfig { AllowPersistence = false }, Path.GetTempPath());
        services.AddSingleton(store);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AbruptTerminalExitRecoversAllUnlockedCheckpointsButNotLiveAgents()
    {
        using var fixture = new ArchiveFolder();
        var original = Sample() with { ClosedAt = null };
        var spawned = Sample(name: "Voxel") with { ClosedAt = null };
        var live = Sample(name: "Kernel") with { ClosedAt = null };
        using var liveLease = fixture.Store.Claim(live.Key);
        using (var originalLease = fixture.Store.Claim(original.Key))
        using (var spawnedLease = fixture.Store.Claim(spawned.Key))
        {
            fixture.Store.Save(original); fixture.Store.Save(spawned); fixture.Store.Save(live);
            fixture.Store.RecoverAbandoned();
            Assert.Empty(fixture.Store.Closed());
        } // OS also releases these handles when a terminal process is killed.
        var reopened = new AgentArchiveStore(fixture.Path);
        reopened.RecoverAbandoned();
        Assert.Equal(2, reopened.Closed().Count);
        Assert.Contains(reopened.Closed(), a => a.Key == original.Key);
        Assert.Contains(reopened.Closed(), a => a.Key == spawned.Key);
        Assert.Null(reopened.Load(live.Key)!.ClosedAt);
        Assert.Equal(spawned.Messages.Count, reopened.Load(spawned.Key)!.Messages.Count);
        reopened.RecoverAbandoned();
        Assert.Equal(2, reopened.Closed().Count);
        Assert.Null(reopened.Error);
    }

    [Fact]
    public async Task ClosingSpawnedPaneFinalizesItsCheckpointEvenWithoutComponentCallback()
    {
        using var fixture = new ArchiveFolder();
        await using var provider = Services(fixture.Store);
        var workspace = provider.GetRequiredService<WorkspaceRegistry>().Active;
        workspace.Add();
        var spawned = workspace.Add();
        var snapshot = Sample(spawned.PersistKey, spawned.Name) with { ClosedAt = null };
        Assert.True(fixture.Store.Save(snapshot));
        Assert.Null(spawned.SaveHistory);
        workspace.Key(spawned, new() { Key = "w", AltKey = true });
        Assert.Single(workspace.Panes);
        Assert.Contains(fixture.Store.Closed(), a => a.Key == spawned.PersistKey);
        Assert.Equal(snapshot.Messages.Count, fixture.Store.Load(spawned.PersistKey)!.Messages.Count);
    }
    internal sealed class ArchiveFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mandocode-agent-archive-" + Guid.NewGuid().ToString("N"));
        public AgentArchiveStore Store { get; }
        public ArchiveFolder() { Directory.CreateDirectory(Path); Store = new(Path); }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
