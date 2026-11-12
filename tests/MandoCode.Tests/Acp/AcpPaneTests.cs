using System.Text.Json;
using System.Threading.Channels;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>A scripted stand-in for the agent runtime: what the pane sees, without a process.</summary>
public sealed class FakeRuntime : IAcpAgentRuntime
{
    private readonly Channel<SessionUpdate> _updates = Channel.CreateUnbounded<SessionUpdate>();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string AgentName => "fake";
    public AcpRuntimeStatus Status { get; set; } = AcpRuntimeStatus.Ready;
    public string? AgentTitle => "fake-adapter 1.2.3";
    public string? CurrentMode => "default";
    public IReadOnlyList<string> StartupWarnings { get; } = [];
    public PromptCapabilities PromptCapabilities { get; } = new();
    public event Action? Changed { add { } remove { } }
    public event Action<string>? SessionStarted;

    public string? SessionId { get; set; } = "session-1";
    public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];
    public IReadOnlyList<AvailableCommand> AvailableCommands { get; set; } = [];
    public AcpSessionSupport Support { get; set; } = AcpSessionSupport.None;
    public AcpSessionOrigin SessionOrigin { get; set; } = AcpSessionOrigin.New;

    public int NewConversations;
    public Exception? NewConversationFails { get; set; }
    public List<(string Id, string Value)> ConfigChanges { get; } = [];
    public Exception? ConfigChangeFails { get; set; }
    public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];

    public Task<bool> NewConversationAsync(CancellationToken cancellationToken)
    {
        if (NewConversationFails is { } failure)
            throw failure;
        NewConversations++;
        SessionId = "session-" + (NewConversations + 1);
        SessionStarted?.Invoke(SessionId);
        return Task.FromResult(Support.CanClose);
    }

    public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken)
    {
        if (ConfigChangeFails is { } failure)
            throw failure;
        ConfigChanges.Add((configId, value));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(Sessions);

    public List<IReadOnlyList<ContentBlock>> Prompts { get; } = [];
    public int CancelCalls;
    public bool Disposed;

    /// <summary>What CancelAsync returns; null makes it hang forever (an agent that never answers).</summary>
    public bool? CancelAnswer { get; set; } = true;

    /// <summary>When set, cancelling also ends the turn with this stop reason.</summary>
    public string? StopOnCancel { get; set; } = StopReasons.Cancelled;

    /// <summary>What ConnectAsync does; null connects at once.</summary>
    public Func<CancellationToken, Task>? OnConnect { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => OnConnect?.Invoke(cancellationToken) ?? Task.CompletedTask;

    public IAcpTurn Prompt(IReadOnlyList<ContentBlock> prompt)
    {
        Prompts.Add(prompt);
        return new Turn(this);
    }

    public Task<bool> CancelAsync()
    {
        Interlocked.Increment(ref CancelCalls);
        if (CancelAnswer is not { } answer)
            return new TaskCompletionSource<bool>().Task;
        if (answer && StopOnCancel is { } stop)
            Succeed(stop);
        return Task.FromResult(answer);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Status = AcpRuntimeStatus.Closed;
        _updates.Writer.TryComplete(new AcpConnectionClosedException("closed", null, ""));
        _completion.TrySetException(new AcpConnectionClosedException("closed", null, ""));
        return ValueTask.CompletedTask;
    }

    public void Send(SessionUpdate update) => _updates.Writer.TryWrite(update);
    public void Succeed(string stopReason)
    {
        _updates.Writer.TryComplete();
        _completion.TrySetResult(stopReason);
    }

    public void Fail(Exception exception)
    {
        _updates.Writer.TryComplete();
        _completion.TrySetException(exception);
    }

    private sealed class Turn(FakeRuntime owner) : IAcpTurn
    {
        public IAsyncEnumerable<SessionUpdate> Updates => owner._updates.Reader.ReadAllAsync();
        public Task<string> Completion => owner._completion.Task;
    }

    public static AgentMessageChunk Text(string text) => new() { Content = new TextContent(text) };
}

public sealed class RecordingSink : IAcpTurnSink
{
    private readonly object _gate = new();
    public List<string> Events { get; } = [];
    public List<string> Replies { get; } = [];

    private void Add(string e) { lock (_gate) Events.Add(e); }
    public void Preview(string? tail) => Add("preview:" + tail);
    public void Activity(string label) => Add("activity:" + label);
    public void Reply(string markdown) { lock (_gate) Replies.Add(markdown); Add("reply:" + markdown); }
    public List<Spectre.Console.Rendering.IRenderable> Blocks { get; } = [];
    public void Block(Spectre.Console.Rendering.IRenderable block) { lock (_gate) Blocks.Add(block); Add("block"); }
    public void Notice(string text) => Add("notice:" + text);
    public void Error(string text) => Add("error:" + text);
    public IEnumerable<string> Of(string kind) { lock (_gate) return Events.Where(e => e.StartsWith(kind + ":")).Select(e => e[(kind.Length + 1)..]).ToArray(); }
}

public class AcpTurnDriverTests
{
    private readonly RecordingSink _sink = new();
    private readonly FakeRuntime _runtime = new();

    private AcpTurnDriver Driver(AcpAgentConfig? config = null) => new(_sink, config);

    private static IReadOnlyList<ContentBlock> Prompt(string text = "hi") => [new TextContent(text)];

    [Fact]
    public async Task StreamedText_IsAPreview_AndBecomesOneMarkdownBlockAtTheEnd()
    {
        var run = Driver().RunAsync(_runtime, Prompt("go"), CancellationToken.None);
        _runtime.Send(FakeRuntime.Text("Hello, "));
        _runtime.Send(FakeRuntime.Text("**world**"));
        _runtime.Succeed(StopReasons.EndTurn);

        var outcome = await run.Within();

        Assert.Equal(["Hello, ", "Hello, **world**"], _sink.Of("preview").Where(p => p.Length > 0));
        Assert.Equal(["Hello, **world**"], _sink.Replies);
        Assert.Equal(StopReasons.EndTurn, outcome.StopReason);
        Assert.Equal("Hello, **world**", outcome.ReplyText);
        Assert.Null(outcome.Error);
        Assert.Equal("preview:", _sink.Events.Last(e => e.StartsWith("preview:")));
        Assert.Empty(_sink.Of("notice"));
    }

    [Fact]
    public async Task ThePromptReachesTheRuntime_AsGiven()
    {
        var blocks = new ContentBlock[] { new TextContent("look at @a.cs"), new ResourceLink { Name = "a.cs", Uri = "file:///a.cs" } };
        var run = Driver().RunAsync(_runtime, blocks, CancellationToken.None);
        _runtime.Succeed(StopReasons.EndTurn);
        await run.Within();

        Assert.Same(blocks, _runtime.Prompts.Single());
    }

    [Fact]
    public async Task Thoughts_AreADimActivityLine_AndTheirTextIsNeverShown()
    {
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Send(new AgentThoughtChunk { Content = new TextContent("secret reasoning") });
        _runtime.Send(new AgentThoughtChunk { Content = new TextContent("more reasoning") });
        _runtime.Send(FakeRuntime.Text("answer"));
        _runtime.Succeed(StopReasons.EndTurn);
        await run.Within();

        Assert.Equal(["Thinking…", "Responding…"], _sink.Of("activity"));
        Assert.DoesNotContain(_sink.Events, e => e.Contains("reasoning"));
    }

    [Fact]
    public async Task AToolCall_EndsTheTextBlockBeforeIt_AndNamesTheActivity()
    {
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Send(FakeRuntime.Text("Let me look."));
        _runtime.Send(new ToolCall { ToolCallId = "c1", Title = "Read file a.cs" });
        _runtime.Send(new ToolCallUpdate { ToolCallId = "c1", Status = "completed" });
        _runtime.Send(FakeRuntime.Text("Found it."));
        _runtime.Succeed(StopReasons.EndTurn);
        var outcome = await run.Within();

        Assert.Equal(["Let me look.", "Found it."], _sink.Replies);
        Assert.Contains("Read file a.cs", _sink.Of("activity"));
        Assert.Equal("Let me look.\nFound it.", outcome.ReplyText);
    }

    [Fact]
    public async Task UnknownAndUnshownUpdates_AreIgnored()
    {
        using var doc = JsonDocument.Parse("""{"sessionUpdate":"hologram"}""");
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Send(new UnknownSessionUpdate("hologram", doc.RootElement.Clone()));
        _runtime.Send(new Plan { Entries = [new PlanEntry { Content = "step" }] });
        _runtime.Send(new CurrentModeUpdate { CurrentModeId = "plan" });
        _runtime.Send(new UserMessageChunk { Content = new TextContent("echo") });
        _runtime.Send(new AgentMessageChunk { Content = new ImageContent() });
        _runtime.Send(FakeRuntime.Text("fine"));
        _runtime.Succeed("a_future_stop_reason");
        var outcome = await run.Within();

        Assert.Equal(["fine"], _sink.Replies);
        Assert.Equal("a_future_stop_reason", outcome.StopReason);
        Assert.Contains("unrecognised stop reason", _sink.Of("notice").Single());
    }

    [Theory]
    [InlineData(StopReasons.Refusal, "refused")]
    [InlineData(StopReasons.MaxTokens, "output limit")]
    [InlineData(StopReasons.MaxTurnRequests, "limit of model requests")]
    public async Task OtherStopReasons_GetAHumanSentence(string stopReason, string expected)
    {
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Succeed(stopReason);
        await run.Within();

        Assert.Contains(expected, _sink.Of("notice").Single());
    }

    // ───────────── cancellation ─────────────

    [Fact]
    public async Task Escape_CancelsTheTurn_KeepsTheTextSoFar_AndSaysSo()
    {
        using var cts = new CancellationTokenSource();
        var run = Driver().RunAsync(_runtime, Prompt(), cts.Token);
        _runtime.Send(FakeRuntime.Text("partial answer"));
        await Task.Delay(50);

        cts.Cancel();
        var outcome = await run.Within();

        Assert.Equal(1, _runtime.CancelCalls);
        Assert.True(outcome.Cancelled);
        Assert.Equal(["partial answer"], _sink.Replies);
        Assert.Contains("Request cancelled.", _sink.Of("notice"));
        Assert.Contains("Cancelling…", _sink.Of("activity"));
        Assert.False(_runtime.Disposed);
    }

    [Fact]
    public async Task AnAgentThatIgnoresTheCancel_IsShutDown_AndTheTurnEndsWithAMessage()
    {
        _runtime.CancelAnswer = false;
        _runtime.StopOnCancel = null;
        using var cts = new CancellationTokenSource();
        var run = Driver().RunAsync(_runtime, Prompt(), cts.Token);
        _runtime.Send(FakeRuntime.Text("typing…"));
        await Task.Delay(50);

        cts.Cancel();
        var outcome = await run.Within();

        Assert.True(_runtime.Disposed);
        Assert.True(outcome.RuntimeClosed);
        Assert.Contains("did not stop", outcome.Error);
        Assert.Contains("/agent-acp restart", outcome.Error);
        Assert.Equal(["typing…"], _sink.Replies);
    }

    [Fact]
    public async Task AnAgentThatNeverAnswersTheCancel_DoesNotHangTheTurn_OnceTheRuntimeTimesOut()
    {
        // CancelAsync that finally reports "unresponsive" after a delay (the runtime's grace period).
        _runtime.CancelAnswer = null;
        using var cts = new CancellationTokenSource();
        var run = Driver().RunAsync(_runtime, Prompt(), cts.Token);
        cts.Cancel();

        await Task.Delay(100);
        Assert.False(run.IsCompleted); // still waiting for the grace period, not failed early
        await _runtime.DisposeAsync(); // the pane closing ends it

        var outcome = await run.Within();
        Assert.True(outcome.RuntimeClosed);
    }

    // ───────────── failures ─────────────

    [Fact]
    public async Task AnAgentThatDiesMidTurn_KeepsThePartialText_AndExplainsWithExitCodeAndStderr()
    {
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Send(FakeRuntime.Text("half an ans"));
        _runtime.Fail(new AcpConnectionClosedException("The agent process exited with code 3.", 3, "panic: out of memory"));
        var outcome = await run.Within();

        Assert.True(outcome.RuntimeClosed);
        Assert.Equal(["half an ans"], _sink.Replies);
        var error = _sink.Of("error").Single();
        Assert.Contains("exit code 3", error);
        Assert.Contains("panic: out of memory", error);
        Assert.Contains("/agent-acp restart", error);
    }

    [Fact]
    public async Task NotSignedIn_ShowsTheConfiguredHint_AndNoRestartAdvice()
    {
        var run = Driver(new AcpAgentConfig { AuthHint = "run `claude auth login`" }).RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Fail(new AcpAuthRequiredException("Authentication required", []));
        await run.Within();

        var error = _sink.Of("error").Single();
        Assert.Contains("not signed in", error);
        Assert.Contains("run `claude auth login`", error);
    }

    [Theory]
    [InlineData("Invalid API key", true)]
    [InlineData("Please log in again", true)]
    [InlineData("401 Unauthorized", true)]
    [InlineData("credentials expired", true)]
    [InlineData("File not found", false)]
    [InlineData("Internal error", false)]
    public async Task AnErrorThatReadsLikeAuthentication_GetsTheHint_WorkedAsAPossibility(string message, bool hint)
    {
        var run = Driver(new AcpAgentConfig { AuthHint = "sign in please" }).RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Fail(new JsonRpcException(-32603, message));
        await run.Within();

        var error = _sink.Of("error").Single();
        Assert.Contains(message, error);
        Assert.Equal(hint, error.Contains("looks like a sign-in problem"));
        Assert.Equal(hint, error.Contains("sign in please"));
    }

    [Fact]
    public async Task ARuntimeThatIsNotRunning_RefusesWithoutSendingAnything()
    {
        _runtime.Status = AcpRuntimeStatus.Closed;

        var outcome = await Driver().RunAsync(_runtime, Prompt(), CancellationToken.None).Within();

        Assert.Empty(_runtime.Prompts);
        Assert.True(outcome.RuntimeClosed);
        Assert.Contains("not running", _sink.Of("error").Single());
    }

    [Fact]
    public async Task ProtocolViolation_IsExplained()
    {
        var run = Driver().RunAsync(_runtime, Prompt(), CancellationToken.None);
        _runtime.Fail(new AcpProtocolViolationException("garbage"));
        var outcome = await run.Within();

        Assert.True(outcome.RuntimeClosed);
        Assert.Contains("non-protocol output to stdout", _sink.Of("error").Single());
    }
}

public class AcpPromptBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mandocode-acp-prompt-" + Guid.NewGuid().ToString("N"));

    public AcpPromptBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "b.md"), "y");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Text_IsSentAsWritten_AndExistingFilesBecomeResourceLinks()
    {
        var blocks = AcpPromptBuilder.Build("compare @src/a.cs with @b.md, please", _root);

        Assert.Equal("compare @src/a.cs with @b.md, please", ((TextContent)blocks[0]).Text);
        var links = blocks.OfType<ResourceLink>().ToArray();
        Assert.Equal(["a.cs", "b.md"], links.Select(l => l.Name));
        Assert.All(links, l => Assert.StartsWith("file:///", l.Uri));
    }

    [Fact]
    public void UnknownFilesAndOutsideTheProject_AreLeftAsText()
    {
        var blocks = AcpPromptBuilder.Build("see @nope.txt and @../../etc/passwd and user@example.com", _root);

        Assert.Single(blocks);
    }

    [Fact]
    public void TheSameFileTwice_IsLinkedOnce()
    {
        Assert.Equal(2, AcpPromptBuilder.Build("@b.md @b.md", _root).Count);
    }

    [Fact]
    public void AQuotedPathWithSpaces_IsLinked()
    {
        File.WriteAllText(Path.Combine(_root, "src", "My Component.cs"), "z");

        var blocks = AcpPromptBuilder.Build("open @\"src/My Component.cs\" now", _root);

        var link = Assert.Single(blocks.OfType<ResourceLink>());
        Assert.Equal("My Component.cs", link.Name);
        Assert.Contains("My%20Component.cs", link.Uri);
    }

    [Fact]
    public void ATrailingSentenceDot_DoesNotHideTheFile()
    {
        var blocks = AcpPromptBuilder.Build("look at @b.md.", _root);

        Assert.Equal(["b.md"], blocks.OfType<ResourceLink>().Select(l => l.Name));
    }

    [Fact]
    public void ParentTraversal_AndAbsolutePathsOutsideTheFolder_AreNotLinked()
    {
        var outside = Path.Combine(Path.GetTempPath(), "mandocode-acp-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var sibling = "../" + Path.GetFileName(outside);
            var blocks = AcpPromptBuilder.Build($"@{sibling} @\"{outside.Replace("\\", "\\\\")}\" @src/../../{Path.GetFileName(outside)}", _root);

            Assert.Empty(blocks.OfType<ResourceLink>());
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void AnAbsolutePathInsideTheFolder_IsLinked()
    {
        var inside = Path.Combine(_root, "b.md");

        var blocks = AcpPromptBuilder.Build($"@\"{inside.Replace("\\", "\\\\")}\"", _root);

        Assert.Equal(["b.md"], blocks.OfType<ResourceLink>().Select(l => l.Name));
    }

    [SymlinkFact]
    public void ALinkThatLeadsOutOfTheFolder_IsNotLinked()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "mandocode-acp-outdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "secret");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outsideDir);
            File.CreateSymbolicLink(Path.Combine(_root, "secret-link.txt"), Path.Combine(outsideDir, "secret.txt"));

            var blocks = AcpPromptBuilder.Build("@linked/secret.txt @secret-link.txt", _root);

            Assert.Empty(blocks.OfType<ResourceLink>());
        }
        finally { Directory.Delete(outsideDir, recursive: true); }
    }

    [JunctionFact]
    public void AJunctionThatLeadsOutOfTheFolder_IsNotLinked()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "mandocode-acp-outjunc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "secret");
        var junction = Path.Combine(_root, "junction");
        try
        {
            Assert.True(LinkProbe.CreateJunction(junction, outsideDir));

            var blocks = AcpPromptBuilder.Build("@junction/secret.txt", _root);

            Assert.Empty(blocks.OfType<ResourceLink>());
        }
        finally
        {
            try { Directory.Delete(junction); } catch (Exception) { }   // removes the junction, not its target
            Directory.Delete(outsideDir, recursive: true);
        }
    }
}

[Collection("AcpProcesses")]
public class AcpAgentRuntimeTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
    private static readonly string MockDll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");
    private readonly List<IAsyncDisposable> _cleanup = [];
    private int _asked;
    private bool _approve = true;

    public AcpAgentRuntimeTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private AcpAgentRuntime Runtime(string scenario, Action<AcpAgentConfig>? tweak = null, InMemoryTrustStore? trust = null)
    {
        var config = new AcpAgentConfig { Command = _host, Args = [MockDll, scenario], StartupTimeoutSeconds = 20 };
        tweak?.Invoke(config);
        var runtime = new AcpAgentRuntime("mock", config, _work, new AcpAgentLauncher(trust ?? new InMemoryTrustStore()),
            _ => { _asked++; return Task.FromResult(_approve); }, Path.Combine(_work, "logs"));
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

    [Fact]
    public async Task Connects_AfterTheTrustQuestion_AndReportsTheAgentsVersion()
    {
        var runtime = Runtime("echo");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal(AcpRuntimeStatus.Ready, runtime.Status);
        Assert.Equal("mock-echo 0.0.1", runtime.AgentTitle);
        Assert.Equal(1, _asked);
        Assert.Equal("echo: hi", await ReplyAsync(runtime, "hi").Within(20));
    }

    [Fact]
    public async Task ADeclinedLaunch_StartsNothing()
    {
        _approve = false;
        var runtime = Runtime("echo");

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None));

        Assert.Contains("not approved", ex.Message);
        Assert.Equal(AcpRuntimeStatus.Failed, runtime.Status);
    }

    [Fact]
    public async Task ATrustedLaunch_IsNotAskedAgain()
    {
        var trust = new InMemoryTrustStore();
        await Runtime("echo", trust: trust).ConnectAsync(CancellationToken.None).Within(40);
        await Runtime("echo", trust: trust).ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task AnAgentThatStartsInAnotherMode_IsSwitchedToDefault()
    {
        var runtime = Runtime("auto-mode");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal("default", runtime.CurrentMode);
        Assert.Empty(runtime.StartupWarnings);
        Assert.Equal("mode: default", await ReplyAsync(runtime, "/mode").Within(20)); // the agent really switched
    }

    [Fact]
    public async Task StartMode_IsHonoured_WhenTheAgentIsAlreadyInIt_WithoutASwitch()
    {
        var runtime = Runtime("auto-mode", c => c.StartMode = "auto");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal("auto", runtime.CurrentMode);
        Assert.Equal("mode: auto", await ReplyAsync(runtime, "/mode").Within(20));
    }

    [Fact]
    public async Task AFailedModeSwitch_IsOnlyAWarning()
    {
        var runtime = Runtime("set-mode-fails");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal(AcpRuntimeStatus.Ready, runtime.Status);
        Assert.Equal("auto", runtime.CurrentMode);
        var warning = Assert.Single(runtime.StartupWarnings);
        Assert.Contains("Could not switch the agent to mode 'default'", warning);
        Assert.Contains("Mode change is not allowed", warning);
        Assert.Equal("echo: still works", await ReplyAsync(runtime, "still works").Within(20));
    }

    [Fact]
    public async Task AnAgentWithoutADefaultMode_IsLeftAlone_Silently()
    {
        var runtime = Runtime("only-auto-mode");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal("auto", runtime.CurrentMode);
        Assert.Empty(runtime.StartupWarnings);
    }

    [Fact]
    public async Task AnExplicitStartModeTheAgentDoesNotOffer_IsAWarning()
    {
        var runtime = Runtime("echo", c => c.StartMode = "plan");

        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Contains("no mode 'plan'", Assert.Single(runtime.StartupWarnings));
        Assert.Equal("default", runtime.CurrentMode);
    }

    [Fact]
    public async Task NotSignedIn_FailsStartup_WithTheHint()
    {
        var runtime = Runtime("auth-required", c => c.AuthHint = "run mock-login first");

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None).Within(40));

        Assert.Contains("not signed in", ex.Message);
        Assert.Contains("run mock-login first", ex.Message);
        Assert.Equal(AcpRuntimeStatus.Failed, runtime.Status);
    }

    [Fact]
    public async Task ADeadAgent_FailsStartup_WithExitCodeStderrAndLogPath()
    {
        var runtime = Runtime("exit-immediately");

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None).Within(40));

        Assert.Contains("exited during startup (exit code 7)", ex.Message);
        Assert.Contains("cannot start", ex.Message);
        Assert.Contains("Full agent stderr: ", ex.Message);
        Assert.Contains("/agent-acp restart", ex.Message);
    }

    [Fact]
    public async Task ATimeout_SaysSo_AndKillsTheTree()
    {
        var runtime = Runtime("silent-with-child", c => c.StartupTimeoutSeconds = 2);

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None).Within(40));

        Assert.Contains("did not complete 'initialize' within 2 seconds", ex.Message);
        Assert.Contains(AcpConnectionProbe.NotAcpHint, ex.Message);
        Assert.Contains("booting, loading models", ex.Message);
        var childPid = int.Parse(ex.Message.Split("child-pid: ")[1].Split('\n', '\r')[0].Trim());
        for (var i = 0; i < 50 && ProcessAlive(childPid); i++)
            await Task.Delay(100);
        Assert.False(ProcessAlive(childPid), "the agent's child process is still running");
    }

    [Fact]
    public async Task ARefusedCommand_ExplainsWhy()
    {
        var runtime = Runtime("echo", c => { c.Command = "npx"; c.Args = ["-y", "pkg"]; });

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None));

        Assert.Contains("downloads and runs packages", ex.Message);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task ADisabledAgent_IsNotStarted()
    {
        var runtime = Runtime("echo", c => c.Disabled = true);

        var ex = await Assert.ThrowsAsync<AcpRuntimeException>(() => runtime.ConnectAsync(CancellationToken.None));

        Assert.Contains("disabled", ex.Message);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task Disposing_EndsTheProcess_AndClosesTheRuntime()
    {
        var runtime = Runtime("echo");
        await runtime.ConnectAsync(CancellationToken.None).Within(40);

        await runtime.DisposeAsync();

        Assert.Equal(AcpRuntimeStatus.Closed, runtime.Status);
        Assert.Throws<InvalidOperationException>(() => runtime.Prompt([new TextContent("x")]));
    }

    [Fact]
    public async Task ARuntimeCanBeRestarted_ByMakingANewOne()
    {
        var trust = new InMemoryTrustStore();
        var first = Runtime("echo", trust: trust);
        await first.ConnectAsync(CancellationToken.None).Within(40);
        await first.DisposeAsync();

        var second = Runtime("echo", trust: trust);
        await second.ConnectAsync(CancellationToken.None).Within(40);

        Assert.Equal("echo: again", await ReplyAsync(second, "again").Within(20));
        Assert.Equal(1, _asked);
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public class AcpPaneStartupTests
{
    private sealed class Sink : IAcpStartupSink
    {
        public List<string> Events { get; } = [];
        public void Connecting(string agentName) => Events.Add("connecting:" + agentName);
        public void Connected(string description, IReadOnlyList<string> warnings) => Events.Add("connected:" + description + (warnings.Count > 0 ? " warnings=" + warnings.Count : ""));
        public void Failed(string message) => Events.Add("failed:" + message);
    }

    [Fact]
    public async Task TheStatusIsAnnounced_BeforeTheConnectionFinishes()
    {
        var gate = new TaskCompletionSource();
        var runtime = new FakeRuntime { OnConnect = _ => gate.Task };
        var sink = new Sink();

        var run = AcpPaneStartup.RunAsync(runtime, sink, CancellationToken.None);

        Assert.False(run.IsCompleted);
        Assert.Equal(["connecting:fake"], sink.Events);

        gate.SetResult();
        Assert.True(await run.Within());
        Assert.Equal(["connecting:fake", "connected:fake-adapter 1.2.3 (mode: default)"], sink.Events);
    }

    [Fact]
    public async Task ARuntimeError_IsShown_AndTheStartupDoesNotThrow()
    {
        var runtime = new FakeRuntime { OnConnect = _ => throw new AcpRuntimeException("Cannot start ACP agent 'x'.") };
        var sink = new Sink();

        Assert.False(await AcpPaneStartup.RunAsync(runtime, sink, CancellationToken.None).Within());

        Assert.Equal(["connecting:fake", "failed:Cannot start ACP agent 'x'."], sink.Events);
    }

    [Fact]
    public async Task AnUnexpectedException_AlsoEndsAsAMessage_WithTheRestartHint()
    {
        var runtime = new FakeRuntime { OnConnect = _ => throw new IOException("trust file is locked") };
        var sink = new Sink();

        Assert.False(await AcpPaneStartup.RunAsync(runtime, sink, CancellationToken.None).Within());

        var failure = Assert.Single(sink.Events, e => e.StartsWith("failed:"));
        Assert.Contains("trust file is locked", failure);
        Assert.Contains("/agent-acp restart", failure);
    }

    [Fact]
    public async Task ClosingThePaneWhileConnecting_EndsQuietly()
    {
        using var cts = new CancellationTokenSource();
        var runtime = new FakeRuntime { OnConnect = token => Task.Delay(Timeout.Infinite, token) };
        var sink = new Sink();

        var run = AcpPaneStartup.RunAsync(runtime, sink, cts.Token);
        cts.Cancel();

        Assert.False(await run.Within());
        Assert.Equal(["connecting:fake"], sink.Events);
    }
}

[Collection("TUI console routing")]
public class AcpPaneWorkspaceTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static ServiceProvider Services()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations,
            new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        return registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public async Task AnAcpPane_KnowsItsRuntime_ItsAgent_AndGetsAUniqueName()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        workspace.Add();

        var first = workspace.Add(acpAgent: "claude");
        var second = workspace.Add(acpAgent: "claude");

        Assert.Equal(AgentRuntime.Native, workspace.Panes[0].Runtime);
        Assert.Equal(AgentRuntime.Acp, first.Runtime);
        Assert.Equal("claude", first.AcpAgentName);
        Assert.Equal("claude", first.Name);
        Assert.Equal("claude 2", second.Name);
        Assert.Equal("ACP · claude", first.Model);
    }

    [Fact]
    public async Task FocusCanGoBackToTheOtherPane_AfterAnAcpPaneOpens()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var native = workspace.Add();
        var acp = workspace.Add(acpAgent: "claude");

        Assert.True(acp.Selected);
        Assert.False(native.Selected);

        workspace.Focus(native);
        Assert.True(native.Selected);
        Assert.False(acp.Selected);

        workspace.Move(1);
        Assert.True(acp.Selected);
    }

    [Fact]
    public async Task APane_GoesNativeToAcpToNative_AndKeepsItsOwnModelUnderneath()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var pane = workspace.Add();
        var holder = pane.Services.GetRequiredService<AcpRuntimeHolder>();
        var nativeModel = pane.Model;
        var nativeName = pane.Name;

        AcpPaneMode.Enter(pane, "claude");
        var runtime = new FakeRuntime();
        holder.Runtime = runtime;

        Assert.Equal(AgentRuntime.Acp, pane.Runtime);
        Assert.Equal("claude", pane.AcpAgentName);
        Assert.Equal("ACP · claude", pane.Model);
        Assert.True(holder.IsAcpPane);
        Assert.Single(workspace.Panes);

        await AcpPaneMode.LeaveAsync(pane);

        Assert.True(runtime.Disposed);
        Assert.Null(holder.Runtime);
        Assert.False(holder.IsAcpPane);
        Assert.Equal(AgentRuntime.Native, pane.Runtime);
        Assert.Null(pane.AcpAgentName);
        Assert.Equal(nativeModel, pane.Model);
        Assert.Equal(nativeName, pane.Name);
    }

    [Fact]
    public async Task SwitchingToAnotherAcpAgent_ClosesTheCurrentOne_InTheSamePane()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var pane = workspace.Add();
        var holder = pane.Services.GetRequiredService<AcpRuntimeHolder>();
        AcpPaneMode.Enter(pane, "claude");
        var first = new FakeRuntime();
        holder.Runtime = first;

        await AcpPaneMode.CloseSessionAsync(pane);
        AcpPaneMode.Enter(pane, "other");

        Assert.True(first.Disposed);
        Assert.Equal("other", pane.AcpAgentName);
        Assert.Equal("ACP · other", pane.Model);
        Assert.True(holder.IsAcpPane);
        Assert.Single(workspace.Panes);
    }

    [Fact]
    public async Task ThePaneLimit_StillAppliesToNewPanels_ButNotToASwitch()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        for (var i = 0; i < 4; i++) workspace.Add();
        Assert.Equal(4, workspace.Panes.Count);

        var extra = workspace.Add(acpAgent: "claude");   // a fifth pane is refused, nothing is added
        Assert.Equal(4, workspace.Panes.Count);
        Assert.NotEqual("claude", extra.AcpAgentName);

        AcpPaneMode.Enter(workspace.Panes[0], "claude");  // switching needs no free slot
        Assert.Equal(4, workspace.Panes.Count);
        Assert.Equal(AgentRuntime.Acp, workspace.Panes[0].Runtime);
    }

    [Fact]
    public async Task ClosingASwitchedPane_AlsoEndsTheAgent()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        workspace.Add();
        var pane = workspace.Add();
        AcpPaneMode.Enter(pane, "claude");
        var runtime = new FakeRuntime();
        pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;

        await pane.DisposeAsync();

        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task TheCommandFilter_FollowsTheCurrentMode()
    {
        await using var services = Services();
        var pane = services.GetRequiredService<AgentWorkspace>().Add();
        var holder = pane.Services.GetRequiredService<AcpRuntimeHolder>();
        static bool Hidden(AcpRuntimeHolder h, string command) => h.IsAcpPane && AcpPaneRestrictions.IsCommandBlocked(command);

        Assert.False(Hidden(holder, "plan"));
        AcpPaneMode.Enter(pane, "claude");
        Assert.True(Hidden(holder, "plan"));
        Assert.False(Hidden(holder, "model"));   // answered from the agent's own settings
        Assert.False(Hidden(holder, "agent-acp"));
        await AcpPaneMode.LeaveAsync(pane);
        Assert.False(Hidden(holder, "plan"));
    }

    [Fact]
    public async Task ClosingAnAcpPane_DisposesItsRuntime_SoTheProcessEnds()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        workspace.Add();
        var pane = workspace.Add(acpAgent: "claude");
        var runtime = new FakeRuntime();
        pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;

        await pane.DisposeAsync();

        Assert.True(runtime.Disposed);
    }

    [Fact]
    public async Task EachPaneHasItsOwnHolder()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var native = workspace.Add();
        var acp = workspace.Add(acpAgent: "claude");

        Assert.NotSame(native.Services.GetRequiredService<AcpRuntimeHolder>(), acp.Services.GetRequiredService<AcpRuntimeHolder>());
    }

    [Theory]
    [InlineData("s", "/agent-settings")]
    [InlineData("k", "/skills")]
    [InlineData("m", "/mcp")]
    [InlineData("h", "/history")]
    [InlineData("c", "/context-snap-import")]
    public async Task BlockedAltShortcuts_InAnAcpPane_SubmitTheExplainingCommand_AndOpenNothing(string key, string command)
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var pane = workspace.Add(acpAgent: "claude");
        var submitted = new List<string>();
        var opened = new List<string>();
        pane.SubmitCommand = text => { submitted.Add(text); return Task.CompletedTask; };
        pane.ToggleSettings = () => { opened.Add("settings"); return Task.CompletedTask; };
        pane.ToggleSnapshots = () => { opened.Add("snapshots"); return Task.CompletedTask; };

        var handled = workspace.Key(pane, new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = key, AltKey = true });

        Assert.True(handled);
        Assert.Empty(opened);
        Assert.Equal([command], submitted);
    }

    [Fact]
    public async Task AltS_InANativePane_StillOpensSettings()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var pane = workspace.Add();
        var opened = false;
        pane.SubmitCommand = _ => Task.CompletedTask;
        pane.ToggleSettings = () => { opened = true; return Task.CompletedTask; };

        workspace.Key(pane, new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "s", AltKey = true });

        Assert.True(opened);
    }

    [Theory]
    [InlineData("model", false)]
    [InlineData("/model", false)]
    [InlineData("plan fix the bug", true)]
    [InlineData("plan-resume", true)]
    [InlineData("compact", false)]
    [InlineData("context-snap-create", true)]
    [InlineData("force-skill x", true)]
    [InlineData("setup", true)]
    [InlineData("retry", true)]
    [InlineData("ollama-pull", true)]
    [InlineData("ollama-serve", true)]
    [InlineData("config set x y", true)]
    [InlineData("skills", true)]
    [InlineData("mcp tools x", true)]
    [InlineData("mcp-reload", true)]
    [InlineData("history", true)]
    [InlineData("agent-history", true)]
    [InlineData("agent-settings", true)]
    [InlineData("help", false)]
    [InlineData("copy", false)]
    [InlineData("copy-code", false)]
    [InlineData("change-directory", false)]
    [InlineData("agent-acp restart", false)]
    [InlineData("agent-new", false)]
    [InlineData("clear", false)]
    [InlineData("exit", false)]
    public void NativeOnlyCommands_AreIdentified(string command, bool nativeOnly) =>
        Assert.Equal(nativeOnly, AcpPaneRestrictions.IsCommandBlocked(command));
}

public sealed class AcpPaneRestrictionsTests
{
    [Fact]
    public void ResumingAPane_DoesNotClaimAFreshStart()
    {
        Assert.Contains("fresh start", AcpPaneMode.EnterMessage("claude", resume: false));
        Assert.DoesNotContain("fresh start", AcpPaneMode.EnterMessage("claude", resume: true));
    }

    [Theory]
    [InlineData("s", "/agent-settings")]
    [InlineData("S", "/agent-settings")]
    [InlineData("c", "/context-snap-import")]
    [InlineData("k", "/skills")]
    [InlineData("m", "/mcp")]
    [InlineData("h", "/history")]
    public void BlockedShortcuts_MapToTheCommandThatExplainsThem(string key, string command)
    {
        Assert.Equal(command, AcpPaneRestrictions.BlockedShortcutCommand(key));
        Assert.True(AcpPaneRestrictions.IsCommandBlocked(command));   // one set: the shortcut and the typed command agree
    }

    [Theory]
    [InlineData("n")]
    [InlineData("w")]
    [InlineData("e")]
    [InlineData("g")]
    [InlineData("d")]
    public void WorkspaceAndFolderShortcuts_AreNotBlocked(string key) =>
        Assert.Null(AcpPaneRestrictions.BlockedShortcutCommand(key));

    [Fact]
    public void TheMessage_NamesTheCommandAndTheReason()
    {
        var message = AcpPaneRestrictions.Message("/mcp tools x");
        Assert.StartsWith("/mcp is not available in an ACP agent pane", message);
        Assert.Contains("MCP", message);
    }

    [Fact]
    public void ThePicker_ShowsNoAgentsGroup() => Assert.False(AcpPaneRestrictions.PickerShowsAgents);
}
