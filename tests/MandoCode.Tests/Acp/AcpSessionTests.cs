using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Transport;
using MandoCode.Acp.Protocol;
using MandoCode.Models;
using MandoCode.Services.Acp;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>Session features of <see cref="AcpAgentRuntime"/> against the mock agent over a real process: config options, commands, new conversations, resume, load, list.</summary>
[Collection("AcpProcesses")]
public class AcpSessionTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
    private static readonly string MockDll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly InMemoryTrustStore _trust = new();
    private readonly string _store;

    public AcpSessionTests()
    {
        Directory.CreateDirectory(_work);
        _store = Path.Combine(_work, "mock-sessions.json");
    }

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private AcpAgentRuntime Runtime(string scenario, AcpResumeRequest? resume = null, Action<SessionUpdate>? history = null, string? folder = null)
    {
        var config = new AcpAgentConfig { Command = _host, Args = [MockDll, scenario], StartupTimeoutSeconds = 20 };
        config.Env["MOCK_SESSION_STORE"] = _store;
        var runtime = new AcpAgentRuntime("mock", config, folder ?? _work, new AcpAgentLauncher(_trust),
            _ => Task.FromResult(true), Path.Combine(_work, "logs"), resume: resume, history: history);
        _cleanup.Add(runtime);
        return runtime;
    }

    private static async Task<string> ReplyAsync(IAcpAgentRuntime runtime, string text)
    {
        var turn = runtime.Prompt([new TextContent(text)]);
        var reply = new System.Text.StringBuilder();
        await foreach (var update in turn.Updates)
            if (update is AgentMessageChunk { Content: TextContent t })
                reply.Append(t.Text);
        await turn.Completion;
        return reply.ToString();
    }

    private static async Task<AcpAgentRuntime> ConnectedAsync(AcpAgentRuntime runtime)
    {
        await runtime.ConnectAsync(CancellationToken.None).Within(40);
        return runtime;
    }

    // ─────────── config options and commands ───────────

    [Fact]
    public async Task TheAgentsConfigOptions_AreExposed_GroupedChoicesFlattened()
    {
        var runtime = await ConnectedAsync(Runtime("config-options"));

        var model = Assert.Single(runtime.ConfigOptions, o => o.Category == ConfigOptionCategories.Model);
        Assert.Equal("model-a", model.CurrentSelection);
        Assert.Equal(["model-a", "model-b"], model.Choices().Select(c => c.Value));
        Assert.Equal("The first one", model.Choices()[0].Description);

        var effort = Assert.Single(runtime.ConfigOptions, o => o.Category == ConfigOptionCategories.ThoughtLevel);
        Assert.Equal(["low", "medium"], effort.Choices().Select(c => c.Value));
        Assert.All(effort.Choices(), c => Assert.Equal("Levels", c.Group));
    }

    [Fact]
    public async Task ChoosingAModel_SendsSetConfigOption_AndTheAgentApplies_AndTheOptionsRefresh()
    {
        var runtime = await ConnectedAsync(Runtime("config-options"));

        await runtime.SetConfigOptionAsync("model", "model-b", CancellationToken.None).Within(20);

        Assert.Equal("model-b", runtime.ConfigOptions.Single(o => o.Id == "model").CurrentSelection);
        Assert.Contains("model=model-b", await ReplyAsync(runtime, "__state").Within(20));
    }

    [Fact]
    public async Task ARefusedConfigValue_ThrowsTheAgentsError()
    {
        var runtime = await ConnectedAsync(Runtime("config-options"));

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => runtime.SetConfigOptionAsync("model", "nope", CancellationToken.None));

        Assert.Equal(AcpErrorCodes.InvalidParams, ex.Code);
        Assert.Equal("model-a", runtime.ConfigOptions.Single(o => o.Id == "model").CurrentSelection);
    }

    [Fact]
    public async Task AnAgentWithoutConfigOptions_ExposesNone()
    {
        var runtime = await ConnectedAsync(Runtime("echo"));

        Assert.Empty(runtime.ConfigOptions);
        Assert.Empty(runtime.AvailableCommands);
    }

    [Fact]
    public async Task TheAnnouncedCommands_AreKnown_IncludingCompact()
    {
        var runtime = await ConnectedAsync(Runtime("config-options"));

        await WaitFor(() => runtime.AvailableCommands.Count > 0);

        Assert.Contains(runtime.AvailableCommands, c => c.Name == "compact");
        Assert.Equal("compacted", await ReplyAsync(runtime, "/compact").Within(20));
    }

    // ─────────── new conversation (/clear) ───────────

    [Fact]
    public async Task ANewConversation_ClosesTheOldSession_AndStartsAnotherInTheSameProcessAndFolder()
    {
        var runtime = await ConnectedAsync(Runtime("sessions"));
        var first = runtime.SessionId;
        var started = new List<string>();
        runtime.SessionStarted += id => started.Add(id);
        Assert.Equal("echo: hi", await ReplyAsync(runtime, "hi").Within(20));
        await runtime.SetConfigOptionAsync("model", "model-b", CancellationToken.None).Within(20);

        var closed = await runtime.NewConversationAsync(CancellationToken.None).Within(30);
        Assert.True(closed);

        Assert.NotEqual(first, runtime.SessionId);
        Assert.Equal([runtime.SessionId!], started);
        var state = await ReplyAsync(runtime, "__state").Within(20);
        Assert.Contains($"closed=[{first}]", state);
        Assert.Contains($"session={runtime.SessionId}", state);
        Assert.Contains("model=model-a", state);   // the agent's initial settings, not the ones chosen before
    }

    [Fact]
    public async Task ANewConversation_WithAnAgentThatCannotCloseSessions_StillStartsANewSession()
    {
        var runtime = await ConnectedAsync(Runtime("config-options"));
        Assert.False(runtime.Support.CanClose);

        var closed = await runtime.NewConversationAsync(CancellationToken.None).Within(30);

        Assert.False(closed);
        Assert.Equal(AcpRuntimeStatus.Ready, runtime.Status);
        Assert.Equal("echo: again", await ReplyAsync(runtime, "again").Within(20));
    }

    // ─────────── resume / load / list ───────────

    [Fact]
    public async Task Resume_ContinuesTheSameSession_WithoutReplayingMessages()
    {
        string id;
        var first = await ConnectedAsync(Runtime("sessions"));
        id = first.SessionId!;
        await ReplyAsync(first, "hello").Within(20);
        await first.DisposeAsync();

        var replayed = new List<SessionUpdate>();
        var second = await ConnectedAsync(Runtime("sessions", new AcpResumeRequest(id), replayed.Add));

        Assert.Equal(AcpSessionOrigin.Resumed, second.SessionOrigin);
        Assert.Equal(id, second.SessionId);
        Assert.Empty(replayed);
        Assert.Empty(second.StartupWarnings);
    }

    [Fact]
    public async Task Load_ReplaysTheHistoryInOrder_WhenTheAgentCannotResume()
    {
        var first = await ConnectedAsync(Runtime("load-only"));
        var id = first.SessionId!;
        await ReplyAsync(first, "hello").Within(20);
        await first.DisposeAsync();

        var replayed = new List<SessionUpdate>();
        var second = await ConnectedAsync(Runtime("load-only", new AcpResumeRequest(id), replayed.Add));

        Assert.False(second.Support.CanResume);
        Assert.Equal(AcpSessionOrigin.Loaded, second.SessionOrigin);
        Assert.Equal(id, second.SessionId);
        Assert.Collection(replayed,
            u => Assert.Equal("hello", ((TextContent)Assert.IsType<UserMessageChunk>(u).Content).Text),
            u => Assert.Equal("echo: hello", ((TextContent)Assert.IsType<AgentMessageChunk>(u).Content).Text));
        Assert.Equal("echo: more", await ReplyAsync(second, "more").Within(20));
    }

    [Fact]
    public async Task ASessionTheAgentDoesNotKnow_FallsBackToANewOne_WithAWarning()
    {
        var runtime = await ConnectedAsync(Runtime("sessions", new AcpResumeRequest("gone")));

        Assert.Equal(AcpSessionOrigin.New, runtime.SessionOrigin);
        Assert.NotEqual("gone", runtime.SessionId);
        Assert.Contains(runtime.StartupWarnings, w => w.Contains("previous session"));
    }

    [Fact]
    public async Task ASessionOfAnotherFolder_IsNotResumed()
    {
        var other = Path.Combine(_work, "other");
        Directory.CreateDirectory(other);
        var first = await ConnectedAsync(Runtime("sessions", folder: other));
        var id = first.SessionId!;
        await first.DisposeAsync();

        var runtime = await ConnectedAsync(Runtime("sessions", new AcpResumeRequest(id)));   // pane folder is _work, not "other"

        Assert.Equal(AcpSessionOrigin.New, runtime.SessionOrigin);
        Assert.NotEqual(id, runtime.SessionId);
    }

    [Fact]
    public async Task ListSessions_ReturnsOnlyThisFoldersSessions()
    {
        var other = Path.Combine(_work, "elsewhere");
        Directory.CreateDirectory(other);
        var elsewhere = await ConnectedAsync(Runtime("sessions", folder: other));
        var foreign = elsewhere.SessionId!;
        await elsewhere.DisposeAsync();

        var runtime = await ConnectedAsync(Runtime("sessions"));
        var mine = runtime.SessionId!;

        var sessions = await runtime.ListSessionsAsync(CancellationToken.None).Within(20);

        Assert.Contains(sessions, s => s.SessionId == mine);
        Assert.DoesNotContain(sessions, s => s.SessionId == foreign);
        Assert.All(sessions, s => Assert.True(AcpPaths.SameFolder(s.Cwd, _work)));
    }

    [Fact]
    public async Task TheSupportFlags_FollowWhatTheAgentAdvertises()
    {
        var full = await ConnectedAsync(Runtime("sessions"));
        Assert.Equal(new AcpSessionSupport(true, true, true, true), full.Support);

        var loadOnly = await ConnectedAsync(Runtime("load-only"));
        Assert.Equal(new AcpSessionSupport(false, false, true, false), loadOnly.Support);

        var plain = await ConnectedAsync(Runtime("echo"));
        Assert.Equal(AcpSessionSupport.None, plain.Support);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition(), "The condition was not met in time.");
    }
}

public class AcpSessionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "mandocode-acp-sessions-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public void TheLastSession_IsRememberedPerAgentAndFolder()
    {
        var store = new AcpSessionStore(_path);
        var a = Path.Combine(Path.GetTempPath(), "proj-a");
        var b = Path.Combine(Path.GetTempPath(), "proj-b");

        store.Set("claude", a, "s1");
        store.Set("claude", b, "s2");
        store.Set("other", a, "s3");

        Assert.Equal("s1", store.Get("claude", a)!.SessionId);
        Assert.Equal("s2", store.Get("claude", b)!.SessionId);
        Assert.Equal("s3", store.Get("other", a)!.SessionId);
        Assert.Null(store.Get("claude", Path.Combine(Path.GetTempPath(), "proj-c")));
    }

    [Fact]
    public void ANewSession_ReplacesTheStoredId()
    {
        var store = new AcpSessionStore(_path);
        var folder = Path.GetTempPath();
        store.Set("claude", folder, "old");

        store.Set("claude", folder, "new");

        Assert.Equal("new", store.Get("claude", folder)!.SessionId);
    }

    [Fact]
    public void FolderNames_AreComparedWithoutATrailingSeparator()
    {
        var store = new AcpSessionStore(_path);
        var folder = Path.Combine(Path.GetTempPath(), "proj");
        store.Set("claude", folder, "s1");

        Assert.Equal("s1", store.Get("CLAUDE", folder + Path.DirectorySeparatorChar)!.SessionId);
    }

    [Fact]
    public void TheFile_HoldsIdsAndFoldersOnly()
    {
        var store = new AcpSessionStore(_path);
        store.Set("claude", Path.GetTempPath(), "session-123");

        var text = File.ReadAllText(_path);
        using var document = JsonDocument.Parse(text);
        var entry = document.RootElement.EnumerateObject().Single().Value;

        Assert.Equal(["SessionId", "Cwd", "UpdatedAt"], entry.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AnUnreadableFile_MeansNothingIsRemembered()
    {
        File.WriteAllText(_path, "{ not json");
        var store = new AcpSessionStore(_path);

        Assert.Null(store.Get("claude", Path.GetTempPath()));
        store.Set("claude", Path.GetTempPath(), "s1");   // and it recovers on the next write
        Assert.Equal("s1", store.Get("claude", Path.GetTempPath())!.SessionId);
    }

    [Fact]
    public void Remove_ForgetsTheSession()
    {
        var store = new AcpSessionStore(_path);
        store.Set("claude", Path.GetTempPath(), "s1");

        store.Remove("claude", Path.GetTempPath());

        Assert.Null(store.Get("claude", Path.GetTempPath()));
    }
}
