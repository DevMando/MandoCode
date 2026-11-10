using System.Collections.Concurrent;
using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>Protocol behaviour of <see cref="AcpClient"/> against a hand-driven agent over in-memory streams.</summary>
public class AcpClientTests : IAsyncDisposable
{
    private readonly FakeAgentPeer _peer = new();
    private readonly ConcurrentQueue<string> _diagnostics = new();
    private AcpClient? _client;

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _peer.Dispose();
    }

    private sealed class ScriptedHandler : IAcpClientHandler
    {
        public ClientCapabilities Capabilities { get; init; } = new();
        public Func<RequestPermissionRequest, CancellationToken, ValueTask<RequestPermissionResponse>>? Permission { get; init; }
        public Func<ReadTextFileRequest, CancellationToken, ValueTask<ReadTextFileResponse>>? Read { get; init; }
        public Func<WriteTextFileRequest, CancellationToken, ValueTask<WriteTextFileResponse>>? Write { get; init; }

        public ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct) =>
            Permission is null
                ? ValueTask.FromResult(new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() })
                : Permission(request, ct);

        public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct) =>
            Read is null ? throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "no read") : Read(request, ct);

        public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct) =>
            Write is null ? throw new JsonRpcException(AcpErrorCodes.MethodNotFound, "no write") : Write(request, ct);
    }

    private static ClientCapabilities FsCapabilities => new() { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } };

    private AcpClient CreateClient(IAcpClientHandler? handler = null, TimeSpan? startupTimeout = null, TimeSpan? cancelGrace = null)
    {
        _client = new AcpClient(new StreamTransport(_peer.ToClient, _peer.FromClient), handler ?? new ScriptedHandler(),
            new AcpClientOptions
            {
                ClientName = "MandoCodeTests",
                ClientVersion = "9.9",
                StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10),
                CancelGrace = cancelGrace ?? TimeSpan.FromSeconds(10),
                Diagnostics = _diagnostics.Enqueue,
            });
        return _client;
    }

    private async Task<InitializeResponse> HandshakeAsync(AcpClient client, string initializeResultJson = """{"protocolVersion":1}""")
    {
        var connect = client.ConnectAsync();
        var message = await _peer.ReadMessageAsync();
        Assert.Equal("initialize", message.GetProperty("method").GetString());
        _peer.SendResult(message.GetProperty("id"), initializeResultJson);
        return await connect.Within();
    }

    private async Task<AcpSession> StartSessionAsync(AcpClient client, string initializeResultJson = """{"protocolVersion":1}""")
    {
        await HandshakeAsync(client, initializeResultJson);
        var create = client.NewSessionAsync("/work/proj");
        var message = await _peer.ReadMessageAsync();
        Assert.Equal("session/new", message.GetProperty("method").GetString());
        _peer.SendResult(message.GetProperty("id"), """{"sessionId":"s1"}""");
        return await create.Within();
    }

    private static string UpdateJson(string sessionId, string updateBody) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"" + sessionId + "\",\"update\":{" + updateBody + "}}}";

    private static string ChunkBody(string text) =>
        "\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"" + text + "\"}";

    private static async Task<List<SessionUpdate>> CollectAsync(AcpTurn turn)
    {
        var updates = new List<SessionUpdate>();
        await foreach (var update in turn.Updates)
            updates.Add(update);
        return updates;
    }

    // ───────────── handshake ─────────────

    [Fact]
    public async Task Initialize_AdvertisesProtocolV1_AndOnlyTheCapabilitiesTheHandlerHas()
    {
        var client = CreateClient(new ScriptedHandler { Capabilities = FsCapabilities });

        var connect = client.ConnectAsync();
        var message = await _peer.ReadMessageAsync();
        var parameters = message.GetProperty("params");

        Assert.Equal(1, parameters.GetProperty("protocolVersion").GetInt32());
        Assert.True(parameters.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.True(parameters.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.False(parameters.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.Equal("MandoCodeTests", parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.9", parameters.GetProperty("clientInfo").GetProperty("version").GetString());

        _peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1}""");
        await connect.Within();
    }

    [Fact]
    public async Task Initialize_ExposesTheAgentsCapabilities()
    {
        var client = CreateClient();

        var response = await HandshakeAsync(client,
            """{"protocolVersion":1,"agentInfo":{"name":"claude","version":"1"},"agentCapabilities":{"promptCapabilities":{"image":true}},"authMethods":[{"id":"login","name":"Login"}]}""");

        Assert.Same(response, client.Initialized);
        Assert.True(response.AgentCapabilities.PromptCapabilities.Image);
        Assert.Equal("login", response.AuthMethods[0].Id);
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_IsReported()
    {
        var client = CreateClient();

        var connect = client.ConnectAsync();
        var message = await _peer.ReadMessageAsync();
        _peer.SendResult(message.GetProperty("id"), """{"protocolVersion":2}""");

        var ex = await Assert.ThrowsAsync<AcpProtocolVersionException>(() => connect.Within());
        Assert.Equal(2, ex.AgentVersion);
        Assert.Contains("version 2", ex.Message);
    }

    [Fact]
    public async Task SilentAgent_TimesOutTheHandshake()
    {
        var client = CreateClient(startupTimeout: TimeSpan.FromMilliseconds(200));

        var connect = client.ConnectAsync();
        await _peer.ReadMessageAsync();

        await Assert.ThrowsAsync<AcpTimeoutException>(() => connect.Within());
    }

    [Fact]
    public async Task NewSession_BeforeConnect_IsAProgrammingError()
    {
        var client = CreateClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.NewSessionAsync("/x"));
    }

    [Fact]
    public async Task NewSession_SendsCwdAndNoMcpServers_AndKeepsTheModes()
    {
        var client = CreateClient();
        await HandshakeAsync(client);

        var create = client.NewSessionAsync(@"C:\work\proj");
        var message = await _peer.ReadMessageAsync();
        Assert.Equal(@"C:\work\proj", message.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Equal(0, message.GetProperty("params").GetProperty("mcpServers").GetArrayLength());

        _peer.SendResult(message.GetProperty("id"),
            """{"sessionId":"abc","modes":{"currentModeId":"plan","availableModes":[{"id":"plan","name":"Plan"}]}}""");
        var session = await create.Within();

        Assert.Equal("abc", session.SessionId);
        Assert.Equal("plan", session.CurrentModeId);
        Assert.Single(session.Modes!.AvailableModes);
    }

    [Fact]
    public async Task Auth_RequiredOnSessionNew_BecomesAuthException_WithTheAdvertisedMethods()
    {
        var client = CreateClient();
        await HandshakeAsync(client, """{"protocolVersion":1,"authMethods":[{"id":"claude-login","name":"Claude login"}]}""");

        var create = client.NewSessionAsync("/p");
        var message = await _peer.ReadMessageAsync();
        _peer.SendError(message.GetProperty("id"), AcpErrorCodes.AuthRequired, "Authentication required");

        var ex = await Assert.ThrowsAsync<AcpAuthRequiredException>(() => create.Within());
        Assert.Equal("claude-login", ex.AuthMethods[0].Id);
    }

    [Fact]
    public async Task Auth_RequiredOnPrompt_FailsTheTurn_WithoutCallingAuthenticate()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("hi")]);
        var prompt = await _peer.ReadMessageAsync();
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());
        _peer.SendError(prompt.GetProperty("id"), AcpErrorCodes.AuthRequired, "Authentication required");

        await Assert.ThrowsAsync<AcpAuthRequiredException>(() => turn.Completion.Within());
        Assert.False(session.IsBusy);
    }

    // ───────────── turns and updates ─────────────

    [Fact]
    public async Task Prompt_StreamsUpdatesInOrder_ThenCompletesWithTheStopReason()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("hello"), new ResourceLink { Name = "a.cs", Uri = "file:///a.cs" }]);
        var prompt = await _peer.ReadMessageAsync();

        Assert.Equal("s1", prompt.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(2, prompt.GetProperty("params").GetProperty("prompt").GetArrayLength());

        _peer.SendLine(UpdateJson("s1", ChunkBody("one")));
        _peer.SendLine(UpdateJson("s1", ChunkBody("two")));
        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"end_turn"}""");

        var updates = await CollectAsync(turn).Within();

        Assert.Equal(["one", "two"], updates.Cast<AgentMessageChunk>().Select(u => ((TextContent)u.Content).Text));
        Assert.Equal(StopReasons.EndTurn, await turn.Completion.Within());
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task UpdatesThatPrecedeTheResponse_AreAllDelivered_EvenWhenTheyArriveInOneBurst()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();

        // One write, so the reader sees the response right behind 200 queued notifications.
        var burst = string.Join("\n", Enumerable.Range(0, 200).Select(i => UpdateJson("s1", ChunkBody(i.ToString()))))
                    + "\n{\"jsonrpc\":\"2.0\",\"id\":" + prompt.GetProperty("id").GetRawText() + ",\"result\":{\"stopReason\":\"end_turn\"}}\n";
        _peer.SendRaw(burst);

        var updates = await CollectAsync(turn).Within();

        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString()),
            updates.Cast<AgentMessageChunk>().Select(u => ((TextContent)u.Content).Text));
        await turn.Completion.Within();
    }

    [Fact]
    public async Task UnknownUpdatesFieldsAndStopReasons_AreIgnoredNotFatal()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();

        _peer.SendLine(UpdateJson("s1", "\"sessionUpdate\":\"usage_update\",\"used\":1"));
        _peer.SendLine(UpdateJson("s1", "\"sessionUpdate\":\"future\",\"x\":{\"y\":[1]}"));
        _peer.SendLine(UpdateJson("s1", ChunkBody("known") + ",\"futureField\":true"));
        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"from_the_future","extra":1}""");

        var updates = await CollectAsync(turn).Within();

        Assert.Equal(3, updates.Count);
        Assert.IsType<UnknownSessionUpdate>(updates[0]);
        Assert.IsType<UnknownSessionUpdate>(updates[1]);
        Assert.IsType<AgentMessageChunk>(updates[2]);
        Assert.Equal("from_the_future", await turn.Completion.Within());
    }

    [Fact]
    public async Task OutOfTurnStateUpdates_AreRemembered_ButNotDeliveredToTurns()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        _peer.SendLine(UpdateJson("s1",
            "\"sessionUpdate\":\"available_commands_update\",\"availableCommands\":[{\"name\":\"init\",\"description\":\"Initialise\"}]"));
        _peer.SendLine(UpdateJson("s1", "\"sessionUpdate\":\"current_mode_update\",\"currentModeId\":\"plan\""));
        await WaitFor(() => session.CurrentModeId == "plan");

        Assert.Equal("init", Assert.Single(session.AvailableCommands).Name);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();
        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"end_turn"}""");

        Assert.Empty(await CollectAsync(turn).Within());
    }

    [Fact]
    public async Task UpdateForAnUnknownSession_IsDropped()
    {
        var client = CreateClient();
        await StartSessionAsync(client);

        _peer.SendLine(UpdateJson("someone-else", ChunkBody("x")));
        await WaitFor(() => _diagnostics.Any(d => d.Contains("unknown session")));
    }

    [Fact]
    public async Task OnlyOneTurnRunsAtATime()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        session.Prompt([new TextContent("first")]);
        await _peer.ReadMessageAsync();

        Assert.Throws<InvalidOperationException>(() => session.Prompt([new TextContent("second")]));
        Assert.True(session.IsBusy);
    }

    [Fact]
    public async Task AgentDyingMidTurn_FailsTheTurn_AndEndsTheUpdateStream()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        await _peer.ReadMessageAsync();
        _peer.SendLine(UpdateJson("s1", ChunkBody("partial")));
        _peer.CloseOutput();

        var updates = await CollectAsync(turn).Within();

        Assert.Single(updates);
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => turn.Completion.Within());
        Assert.False(session.IsBusy);
    }

    // ───────────── cancellation ─────────────

    [Fact]
    public async Task Cancel_SendsSessionCancel_AndTheTurnCompletesCancelled_KeepingLateUpdates()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();

        var cancel = session.CancelAsync();
        var notification = await _peer.ReadMessageAsync();
        Assert.Equal("session/cancel", notification.GetProperty("method").GetString());
        Assert.False(notification.TryGetProperty("id", out _));
        Assert.Equal("s1", notification.GetProperty("params").GetProperty("sessionId").GetString());

        _peer.SendLine(UpdateJson("s1", ChunkBody("late words")));
        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"cancelled"}""");

        Assert.True(await cancel.Within());
        Assert.Equal(StopReasons.Cancelled, await turn.Completion.Within());
        Assert.Single(await CollectAsync(turn));
    }

    [Fact]
    public async Task Cancel_AnswersPendingPermissionRequestsAsCancelled()
    {
        var handlerSawCancellation = new TaskCompletionSource();
        var handler = new ScriptedHandler
        {
            Permission = async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { handlerSawCancellation.SetResult(); throw; }
                return new RequestPermissionResponse();
            },
        };
        var client = CreateClient(handler);
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();

        _peer.SendLine("""
            {"jsonrpc":"2.0","id":"perm-1","method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"c","title":"Edit"},"options":[{"optionId":"a","name":"Allow","kind":"allow_once"}]}}
            """.ReplaceLineEndings(" "));
        await Task.Delay(100); // let the prompt open

        var cancel = session.CancelAsync();
        await handlerSawCancellation.Task.Within();

        var cancelNotification = await _peer.ReadMessageAsync();
        var permissionAnswer = await _peer.ReadMessageAsync();
        var messages = new[] { cancelNotification, permissionAnswer };

        var answer = messages.Single(m => m.TryGetProperty("id", out _));
        Assert.Equal("perm-1", answer.GetProperty("id").GetString());
        Assert.Equal("cancelled", answer.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Contains(messages, m => m.TryGetProperty("method", out var method) && method.GetString() == "session/cancel");

        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"cancelled"}""");
        Assert.True(await cancel.Within());
        await turn.Completion.Within();
    }

    [Fact]
    public async Task PermissionRequestedAfterCancel_IsRefusedWithoutBotheringTheUser()
    {
        var asked = 0;
        var client = CreateClient(new ScriptedHandler
        {
            Permission = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return ValueTask.FromResult(new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = "a" } });
            },
        });
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();
        var cancel = session.CancelAsync();
        await _peer.ReadMessageAsync(); // session/cancel

        _peer.SendLine("""
            {"jsonrpc":"2.0","id":7,"method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"c"},"options":[]}}
            """.ReplaceLineEndings(" "));
        var answer = await _peer.ReadMessageAsync();

        Assert.Equal("cancelled", answer.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal(0, Volatile.Read(ref asked));

        _peer.SendResult(prompt.GetProperty("id"), """{"stopReason":"cancelled"}""");
        await cancel.Within();
        await turn.Completion.Within();
    }

    [Fact]
    public async Task Cancel_WhenTheAgentIgnoresIt_ReportsFalseAfterTheGracePeriod()
    {
        var client = CreateClient(cancelGrace: TimeSpan.FromMilliseconds(250));
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        await _peer.ReadMessageAsync();

        Assert.False(await session.CancelAsync().Within());
        Assert.False(turn.Completion.IsCompleted);
        Assert.True(session.IsBusy);
    }

    [Fact]
    public async Task Cancel_WithNoTurnRunning_IsANoOp()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        Assert.True(await session.CancelAsync().Within());
    }

    [Fact]
    public async Task CancelledErrorResponse_ToACancelledTurn_IsTreatedAsStopReasonCancelled()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);

        var turn = session.Prompt([new TextContent("go")]);
        var prompt = await _peer.ReadMessageAsync();
        var cancel = session.CancelAsync();
        await _peer.ReadMessageAsync();

        _peer.SendError(prompt.GetProperty("id"), AcpErrorCodes.RequestCancelled, "Request cancelled");

        Assert.Equal(StopReasons.Cancelled, await turn.Completion.Within());
        await cancel.Within();
    }

    [Fact]
    public async Task CancellingThePromptToken_CancelsTheTurn()
    {
        var client = CreateClient();
        var session = await StartSessionAsync(client);
        using var cts = new CancellationTokenSource();

        session.Prompt([new TextContent("go")], cts.Token);
        await _peer.ReadMessageAsync();

        cts.Cancel();
        var notification = await _peer.ReadMessageAsync();
        Assert.Equal("session/cancel", notification.GetProperty("method").GetString());
    }

    // ───────────── agent -> client requests ─────────────

    [Fact]
    public async Task PermissionRequest_ReachesTheHandler_AndTheChosenOptionGoesBack()
    {
        RequestPermissionRequest? seen = null;
        var client = CreateClient(new ScriptedHandler
        {
            Permission = (request, _) =>
            {
                seen = request;
                return ValueTask.FromResult(new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = "allow" } });
            },
        });
        await StartSessionAsync(client);

        _peer.SendLine("""
            {"jsonrpc":"2.0","id":3,"method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"c1","title":"Run tests","kind":"execute"},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"},{"optionId":"no","name":"No","kind":"reject_once"}]}}
            """.ReplaceLineEndings(" "));
        var answer = await _peer.ReadMessageAsync();

        Assert.Equal("Run tests", seen!.ToolCall.Title);
        Assert.Equal(2, seen.Options.Count);
        Assert.Equal("allow", answer.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task FileSystemRequests_AreRoutedToTheHandler_WhenAdvertised()
    {
        var client = CreateClient(new ScriptedHandler
        {
            Capabilities = FsCapabilities,
            Read = (request, _) => ValueTask.FromResult(new ReadTextFileResponse { Content = "contents of " + request.Path }),
            Write = (request, _) => ValueTask.FromResult(new WriteTextFileResponse()),
        });
        await StartSessionAsync(client);

        _peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/p/a.txt"}}""");
        Assert.Equal("contents of /p/a.txt", (await _peer.ReadMessageAsync()).GetProperty("result").GetProperty("content").GetString());

        _peer.SendLine("""{"jsonrpc":"2.0","id":2,"method":"fs/write_text_file","params":{"sessionId":"s1","path":"/p/a.txt","content":"x"}}""");
        Assert.True((await _peer.ReadMessageAsync()).TryGetProperty("result", out _));
    }

    [Fact]
    public async Task FileSystemRequests_WithoutTheCapability_GetMethodNotFound()
    {
        var client = CreateClient(new ScriptedHandler());
        await StartSessionAsync(client);

        _peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/p/a.txt"}}""");

        Assert.Equal(AcpErrorCodes.MethodNotFound,
            (await _peer.ReadMessageAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task TerminalRequests_GetMethodNotFound_BecauseTheCapabilityIsNotAdvertised()
    {
        var client = CreateClient(new ScriptedHandler { Capabilities = FsCapabilities });
        await StartSessionAsync(client);

        _peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"terminal/create","params":{"sessionId":"s1","command":"ls"}}""");

        Assert.Equal(AcpErrorCodes.MethodNotFound,
            (await _peer.ReadMessageAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HandlerErrors_ReachTheAgentAsJsonRpcErrors()
    {
        var client = CreateClient(new ScriptedHandler
        {
            Capabilities = FsCapabilities,
            Read = (_, _) => throw new JsonRpcException(AcpErrorCodes.ResourceNotFound, "File not found"),
        });
        await StartSessionAsync(client);

        _peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"sessionId":"s1","path":"/nope"}}""");

        var error = (await _peer.ReadMessageAsync()).GetProperty("error");
        Assert.Equal(AcpErrorCodes.ResourceNotFound, error.GetProperty("code").GetInt32());
        Assert.Equal("File not found", error.GetProperty("message").GetString());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met within 5s");
    }
}
