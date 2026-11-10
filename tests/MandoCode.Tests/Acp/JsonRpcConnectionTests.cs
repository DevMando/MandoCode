using System.Collections.Concurrent;
using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.JsonRpc;
using MandoCode.Acp.Protocol;
using Xunit;

namespace MandoCode.Tests.Acp;

public class JsonRpcIdTests
{
    [Fact]
    public void NumberAndStringIds_RoundTripUnchanged()
    {
        using var doc = JsonDocument.Parse("""{"n":7,"s":"abc-7","big":9007199254740993}""");
        Assert.True(JsonRpcId.TryRead(doc.RootElement.GetProperty("n"), out var number));
        Assert.True(JsonRpcId.TryRead(doc.RootElement.GetProperty("s"), out var text));
        Assert.True(JsonRpcId.TryRead(doc.RootElement.GetProperty("big"), out var big));

        Assert.False(number.IsString);
        Assert.True(number.TryGetInt64(out var n));
        Assert.Equal(7, n);

        Assert.True(text.IsString);
        Assert.False(text.TryGetInt64(out _));

        Assert.True(big.TryGetInt64(out var b));
        Assert.Equal(9007199254740993, b);

        Assert.Equal("7", Write(number));
        Assert.Equal("\"abc-7\"", Write(text));
    }

    [Fact]
    public void StringId_ThatLooksNumeric_IsNotConfusedWithANumber()
    {
        Assert.NotEqual(JsonRpcId.FromNumber(1), JsonRpcId.FromString("1"));
        Assert.False(JsonRpcId.FromString("1").TryGetInt64(out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void NonScalarIds_AreRejected(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(JsonRpcId.TryRead(doc.RootElement, out _));
    }

    private static string Write(JsonRpcId id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            id.WriteTo(writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

public class JsonRpcConnectionTests
{
    private static InitializeRequest Init() => new() { ClientInfo = new Implementation { Name = "t", Version = "1" } };

    private static Task<InitializeResponse> SendInitialize(JsonRpcConnection c, CancellationToken ct = default) =>
        c.SendRequestAsync(AcpMethods.Initialize, Init(), AcpJsonContext.Default.InitializeRequest,
            AcpJsonContext.Default.InitializeResponse, ct);

    // ───────────── correlation ─────────────

    [Fact]
    public async Task Request_IsCorrelatedWithItsResponse()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();

        Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString());
        Assert.Equal("initialize", message.GetProperty("method").GetString());
        Assert.Equal(1, message.GetProperty("params").GetProperty("protocolVersion").GetInt32());

        peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1,"agentInfo":{"name":"a","version":"2"}}""");

        var response = await pending.Within();
        Assert.Equal("a", response.AgentInfo!.Name);
    }

    [Fact]
    public async Task ParallelRequests_GetUniqueIds_AndOutOfOrderResponsesReachTheRightCaller()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var first = connection.SendRequestAsync(AcpMethods.SessionPrompt,
            new PromptRequest { SessionId = "s", Prompt = [new TextContent("one")] },
            AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse);
        var second = connection.SendRequestAsync(AcpMethods.SessionPrompt,
            new PromptRequest { SessionId = "s", Prompt = [new TextContent("two")] },
            AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse);

        var m1 = await peer.ReadMessageAsync();
        var m2 = await peer.ReadMessageAsync();
        Assert.NotEqual(m1.GetProperty("id").GetInt64(), m2.GetProperty("id").GetInt64());

        // Answer in reverse order of arrival.
        peer.SendResult(m2.GetProperty("id"), """{"stopReason":"refusal"}""");
        peer.SendResult(m1.GetProperty("id"), """{"stopReason":"end_turn"}""");

        var firstText = m1.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString();
        var expectedFirst = firstText == "one" ? "end_turn" : "refusal";
        Assert.Equal(expectedFirst, (await first.Within()).StopReason);
        Assert.Equal(expectedFirst == "end_turn" ? "refusal" : "end_turn", (await second.Within()).StopReason);
    }

    [Fact]
    public async Task ErrorResponse_BecomesJsonRpcException_WithCodeAndData()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        peer.SendError(message.GetProperty("id"), AcpErrorCodes.AuthRequired, "Authentication required", """{"hint":"login"}""");

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => pending.Within());
        Assert.Equal(AcpErrorCodes.AuthRequired, ex.Code);
        Assert.Equal("Authentication required", ex.Message);
        Assert.Equal("login", ex.ErrorData!.Value.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task ResponseWithUnknownId_IsIgnored()
    {
        using var peer = new FakeAgentPeer();
        var diagnostics = new ConcurrentQueue<string>();
        await using var connection = peer.CreateConnection(new JsonRpcConnectionOptions { Diagnostics = diagnostics.Enqueue });

        peer.SendLine("""{"jsonrpc":"2.0","id":999,"result":{}}""");

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1}""");

        Assert.Equal(1, (await pending.Within()).ProtocolVersion);
        Assert.Contains(diagnostics, d => d.Contains("unknown id"));
    }

    [Fact]
    public async Task CancellingTheToken_CancelsTheRequest_AndALateResponseIsHarmless()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();
        using var cts = new CancellationTokenSource();

        var pending = SendInitialize(connection, cts.Token);
        var message = await peer.ReadMessageAsync();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.Within());

        peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1}""");

        var second = SendInitialize(connection);
        var next = await peer.ReadMessageAsync();
        peer.SendResult(next.GetProperty("id"), """{"protocolVersion":1}""");
        Assert.Equal(1, (await second.Within()).ProtocolVersion);
    }

    // ───────────── framing ─────────────

    [Fact]
    public async Task EachMessage_IsExactlyOneLine_EvenWithNewlinesInText()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        _ = connection.SendRequestAsync(AcpMethods.SessionPrompt,
            new PromptRequest { SessionId = "s", Prompt = [new TextContent("line1\nline2\r\nline3")] },
            AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse);

        var raw = await peer.ReadRawLineAsync();
        Assert.NotNull(raw);
        using var doc = JsonDocument.Parse(raw!);
        Assert.Equal("line1\nline2\r\nline3",
            doc.RootElement.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task FragmentedCrlfAndBlankLines_AreHandled()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        var id = message.GetProperty("id").GetRawText();

        // Blank lines, a CRLF terminator, and a body split into 3-byte reads.
        peer.SendRaw("\n\r\n   \n");
        peer.SendRawInChunks($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"protocolVersion":1}}""" + "\r\n", 3);

        Assert.Equal(1, (await pending.Within()).ProtocolVersion);
    }

    [Fact]
    public async Task FinalLineWithoutNewline_IsStillDelivered_BeforeEof()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();

        peer.SendRaw($$$"""{"jsonrpc":"2.0","id":{{{message.GetProperty("id").GetRawText()}}},"result":{"protocolVersion":1}}""");
        peer.CloseOutput();

        Assert.Equal(1, (await pending.Within()).ProtocolVersion);
    }

    [Fact]
    public async Task LargeMessages_AreReassembled()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();

        var padding = new string('x', 300_000);
        peer.SendResult(message.GetProperty("id"), "{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"" + padding + "\",\"version\":\"1\"}}");

        Assert.Equal(300_000, (await pending.Within()).AgentInfo!.Name.Length);
    }

    [Fact]
    public async Task OverlongLine_BreaksTheConnection_WithAProtocolViolation()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection(new JsonRpcConnectionOptions { MaxLineBytes = 4096 });

        var pending = SendInitialize(connection);
        await peer.ReadMessageAsync();

        peer.SendRaw(new string('a', 10_000));

        await Assert.ThrowsAsync<AcpProtocolViolationException>(() => connection.Completion.Within());
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending.Within());
    }

    [Fact]
    public async Task RepeatedNonJsonOutput_BreaksTheConnection_WithAnExplanation()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection(new JsonRpcConnectionOptions { MaxConsecutiveParseErrors = 3 });

        var pending = SendInitialize(connection);
        await peer.ReadMessageAsync();

        peer.SendLine("[info] starting up");
        peer.SendLine("Listening on stdio");
        peer.SendLine("123");

        var violation = await Assert.ThrowsAsync<AcpProtocolViolationException>(() => connection.Completion.Within());
        Assert.Contains("stdout", violation.Message);
        var closed = await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending.Within());
        Assert.IsType<AcpProtocolViolationException>(closed.InnerException);
    }

    [Fact]
    public async Task SingleGarbageLine_IsSkipped_AndTheCounterResets()
    {
        using var peer = new FakeAgentPeer();
        var diagnostics = new ConcurrentQueue<string>();
        await using var connection = peer.CreateConnection(new JsonRpcConnectionOptions
        {
            MaxConsecutiveParseErrors = 2,
            Diagnostics = diagnostics.Enqueue,
        });

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        var id = message.GetProperty("id").GetRawText();

        peer.SendLine("some banner");
        peer.SendLine("""{"jsonrpc":"2.0","method":"noise","params":{}}""");
        peer.SendLine("another banner");
        peer.SendLine($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"protocolVersion":1}}""");

        Assert.Equal(1, (await pending.Within()).ProtocolVersion);
        Assert.False(connection.Completion.IsCompleted);
        Assert.Equal(2, diagnostics.Count(d => d.Contains("not valid JSON")));
    }

    // ───────────── incoming requests ─────────────

    [Fact]
    public async Task IncomingRequest_DoesNotBlockTheReader_WhilePermissionIsPending()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var userDecision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource();
        connection.RegisterRequestHandler(
            AcpMethods.SessionRequestPermission,
            AcpJsonContext.Default.RequestPermissionRequest,
            AcpJsonContext.Default.RequestPermissionResponse,
            async (request, _) =>
            {
                handlerStarted.TrySetResult();
                var optionId = await userDecision.Task;
                return new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = optionId } };
            });

        // session/prompt is in flight...
        var prompt = connection.SendRequestAsync(AcpMethods.SessionPrompt,
            new PromptRequest { SessionId = "s", Prompt = [new TextContent("go")] },
            AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse);
        var promptMessage = await peer.ReadMessageAsync();

        // ...and the agent asks for permission (string id on purpose) before answering it.
        peer.SendLine("""
            {"jsonrpc":"2.0","id":"perm-1","method":"session/request_permission","params":{"sessionId":"s",
             "toolCall":{"toolCallId":"c","title":"Edit"},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}
            """.ReplaceLineEndings(" "));
        await handlerStarted.Task.Within();

        // The reader is still live: the prompt response is processed while the handler waits.
        peer.SendResult(promptMessage.GetProperty("id"), """{"stopReason":"end_turn"}""");
        Assert.Equal("end_turn", (await prompt.Within()).StopReason);

        userDecision.SetResult("allow");
        var answer = await peer.ReadMessageAsync();
        Assert.Equal(JsonValueKind.String, answer.GetProperty("id").ValueKind);
        Assert.Equal("perm-1", answer.GetProperty("id").GetString());
        Assert.Equal("selected", answer.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("allow", answer.GetProperty("result").GetProperty("outcome").GetProperty("optionId").GetString());
    }

    [Fact]
    public async Task UnknownMethod_GetsMethodNotFound()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        peer.SendLine("""{"jsonrpc":"2.0","id":5,"method":"terminal/create","params":{}}""");

        var reply = await peer.ReadMessageAsync();
        Assert.Equal(5, reply.GetProperty("id").GetInt32());
        Assert.Equal(AcpErrorCodes.MethodNotFound, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task InvalidParams_GetInvalidParams()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();
        connection.RegisterRequestHandler(AcpMethods.FsReadTextFile,
            AcpJsonContext.Default.ReadTextFileRequest, AcpJsonContext.Default.ReadTextFileResponse,
            (_, _) => ValueTask.FromResult(new ReadTextFileResponse { Content = "x" }));

        peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"path":{"not":"a string"}}}""");
        Assert.Equal(AcpErrorCodes.InvalidParams,
            (await peer.ReadMessageAsync()).GetProperty("error").GetProperty("code").GetInt32());

        peer.SendLine("""{"jsonrpc":"2.0","id":2,"method":"fs/read_text_file"}""");
        Assert.Equal(AcpErrorCodes.InvalidParams,
            (await peer.ReadMessageAsync()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HandlerFailures_MapToErrorResponses_WithoutLeakingDetails()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        connection.RegisterRequestHandler(AcpMethods.FsReadTextFile,
            AcpJsonContext.Default.ReadTextFileRequest, AcpJsonContext.Default.ReadTextFileResponse,
            (request, _) => request.Path switch
            {
                "missing" => throw new JsonRpcException(AcpErrorCodes.ResourceNotFound, "No such file"),
                _ => throw new InvalidOperationException("secret internal detail"),
            });

        peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"sessionId":"s","path":"missing"}}""");
        var notFound = (await peer.ReadMessageAsync()).GetProperty("error");
        Assert.Equal(AcpErrorCodes.ResourceNotFound, notFound.GetProperty("code").GetInt32());
        Assert.Equal("No such file", notFound.GetProperty("message").GetString());

        peer.SendLine("""{"jsonrpc":"2.0","id":2,"method":"fs/read_text_file","params":{"sessionId":"s","path":"boom"}}""");
        var internalError = (await peer.ReadMessageAsync()).GetProperty("error");
        Assert.Equal(AcpErrorCodes.InternalError, internalError.GetProperty("code").GetInt32());
        Assert.DoesNotContain("secret", internalError.GetProperty("message").GetString());
    }

    // ───────────── notifications ─────────────

    [Fact]
    public async Task Notifications_AreHandledInOrder_AndDrainWaitsForThem()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var seen = new List<string>();
        connection.RegisterNotificationHandler(AcpMethods.SessionUpdate, AcpJsonContext.Default.SessionNotification,
            async n =>
            {
                await Task.Delay(1); // yield so an unordered consumer would interleave
                lock (seen)
                    seen.Add(((AgentMessageChunk)n.Update).Content is TextContent t ? t.Text : "?");
            });

        for (var i = 0; i < 40; i++)
        {
            peer.SendLine(FakeAgentPeer.ChunkNotification(i.ToString()));
        }

        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1}""");
        await pending.Within();

        await connection.DrainNotificationsAsync().Within();

        lock (seen)
            Assert.Equal(Enumerable.Range(0, 40).Select(i => i.ToString()), seen);
    }

    [Fact]
    public async Task FailingNotificationHandler_DoesNotStopLaterNotifications()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var seen = new ConcurrentQueue<string>();
        connection.RegisterNotificationHandler(AcpMethods.SessionUpdate, AcpJsonContext.Default.SessionNotification,
            n =>
            {
                var text = ((TextContent)((AgentMessageChunk)n.Update).Content).Text;
                if (text == "bad")
                    throw new InvalidOperationException("boom");
                seen.Enqueue(text);
                return ValueTask.CompletedTask;
            });

        foreach (var text in new[] { "a", "bad", "b" })
            peer.SendLine(FakeAgentPeer.ChunkNotification(text));

        // A response that follows the notifications proves the reader has dispatched all of them
        // (DrainNotificationsAsync only covers notifications that were already received).
        var pending = SendInitialize(connection);
        var message = await peer.ReadMessageAsync();
        peer.SendResult(message.GetProperty("id"), """{"protocolVersion":1}""");
        await pending.Within();

        await connection.DrainNotificationsAsync().Within();
        Assert.Equal(["a", "b"], seen.ToArray());
    }

    [Fact]
    public async Task NotificationWithoutHandler_IsIgnored()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        peer.SendLine("""{"jsonrpc":"2.0","method":"_vendor/thing","params":{"a":1}}""");
        await connection.DrainNotificationsAsync().Within();
        Assert.False(connection.Completion.IsCompleted);
    }

    [Fact]
    public async Task OutgoingNotification_HasNoId()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        await connection.SendNotificationAsync(AcpMethods.SessionCancel,
            new CancelNotification { SessionId = "s1" }, AcpJsonContext.Default.CancelNotification);

        var message = await peer.ReadMessageAsync();
        Assert.Equal("session/cancel", message.GetProperty("method").GetString());
        Assert.False(message.TryGetProperty("id", out _));
        Assert.Equal("s1", message.GetProperty("params").GetProperty("sessionId").GetString());
    }

    // ───────────── shutdown ─────────────

    [Fact]
    public async Task Eof_FailsEveryPendingRequest_AndLaterSendsFailFast()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        var a = SendInitialize(connection);
        var b = SendInitialize(connection);
        await peer.ReadMessageAsync();
        await peer.ReadMessageAsync();

        peer.CloseOutput();

        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => a.Within());
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => b.Within());
        await connection.Completion.Within();
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => SendInitialize(connection));
    }

    [Fact]
    public async Task Eof_UsesTheTransportsDescription_ForExitCodeAndStderr()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection(new JsonRpcConnectionOptions
        {
            DescribeClose = () => new AcpConnectionClosedException("agent exited", exitCode: 3, stderrTail: "fatal: no login"),
        });

        var pending = SendInitialize(connection);
        await peer.ReadMessageAsync();
        peer.CloseOutput();

        var ex = await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending.Within());
        Assert.Equal(3, ex.ExitCode);
        Assert.Equal("fatal: no login", ex.StderrTail);
    }

    [Fact]
    public async Task WriteFailure_ClosesTheConnection()
    {
        using var peer = new FakeAgentPeer();
        await using var connection = peer.CreateConnection();

        peer.FromClient.CompleteWriting(); // the agent's stdin is gone: writes now throw IOException

        var pending = SendInitialize(connection);
        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending.Within());
    }

    [Fact]
    public async Task Dispose_FailsPending_AndIsIdempotent()
    {
        using var peer = new FakeAgentPeer();
        var connection = peer.CreateConnection();

        var pending = SendInitialize(connection);
        await peer.ReadMessageAsync();

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => pending.Within());
        await connection.Completion.Within();
    }

    [Fact]
    public async Task Dispose_CancelsRunningHandlers()
    {
        using var peer = new FakeAgentPeer();
        var connection = peer.CreateConnection();

        var cancelled = new TaskCompletionSource();
        connection.RegisterRequestHandler(AcpMethods.FsReadTextFile,
            AcpJsonContext.Default.ReadTextFileRequest, AcpJsonContext.Default.ReadTextFileResponse,
            async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
                return new ReadTextFileResponse();
            });

        peer.SendLine("""{"jsonrpc":"2.0","id":1,"method":"fs/read_text_file","params":{"sessionId":"s","path":"/x"}}""");
        await Task.Delay(50);

        await connection.DisposeAsync();
        await cancelled.Task.Within();
    }
}
