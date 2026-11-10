using MandoCode.Acp;
using MandoCode.Acp.Client;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>End-to-end: <see cref="AcpClient"/> over a real <see cref="AcpProcessTransport"/> talking to the mock agent.</summary>
public class AcpClientProcessTests : IAsyncDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-client-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public AcpClientProcessTests() => Directory.CreateDirectory(_work);

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup)
            await item.DisposeAsync();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private sealed class Handler : IAcpClientHandler
    {
        public ClientCapabilities Capabilities { get; } =
            new() { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true } };
        public string PermissionOption { get; init; } = "allow";
        public TaskCompletionSource PermissionAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldPermission { get; init; }

        public async ValueTask<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct)
        {
            PermissionAsked.TrySetResult();
            if (HoldPermission)
                await Task.Delay(Timeout.Infinite, ct);
            return new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = PermissionOption } };
        }

        public ValueTask<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct) =>
            ValueTask.FromResult(new ReadTextFileResponse { Content = File.ReadAllText(request.Path) });

        public ValueTask<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct)
        {
            File.WriteAllText(request.Path, request.Content);
            return ValueTask.FromResult(new WriteTextFileResponse());
        }
    }

    private AcpClient Start(string scenario, IAcpClientHandler? handler = null, TimeSpan? cancelGrace = null)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
        var dll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");
        var spec = new AcpLaunchSpec("mock-" + scenario, host, [dll, scenario], null, false, _work);
        var transport = AcpProcessTransport.Start(spec, new ResolvedCommand(spec.Command, spec.Args), new StderrLog(),
            new AcpProcessOptions { ShutdownGrace = TimeSpan.FromSeconds(1) });
        var client = new AcpClient(transport, handler ?? new Handler(),
            new AcpClientOptions { StartupTimeout = TimeSpan.FromSeconds(30), CancelGrace = cancelGrace ?? TimeSpan.FromSeconds(10) });
        _cleanup.Add(client);
        return client;
    }

    private async Task<AcpSession> SessionAsync(AcpClient client)
    {
        await client.ConnectAsync().Within(40);
        return await client.NewSessionAsync(_work).Within(20);
    }

    private static async Task<string> TextAsync(AcpTurn turn)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var update in turn.Updates)
            if (update is AgentMessageChunk { Content: TextContent t })
                text.Append(t.Text);
        return text.ToString();
    }

    [Fact]
    public async Task Echo_StreamsTheReply()
    {
        var session = await SessionAsync(Start("echo"));

        var turn = session.Prompt([new TextContent("ping")]);

        Assert.Equal("echo: ping", await TextAsync(turn).Within(20));
        Assert.Equal(StopReasons.EndTurn, await turn.Completion);
        Assert.Equal("default", session.CurrentModeId);
    }

    [Fact]
    public async Task ClientCapabilities_ReachTheAgent()
    {
        var client = Start("echo");
        var response = await client.ConnectAsync().Within(40);

        var fs = response.Meta!.Value.GetProperty("clientCapabilities").GetProperty("fs");
        Assert.True(fs.GetProperty("readTextFile").GetBoolean());
    }

    [Fact]
    public async Task AuthRequired_SurfacesWithTheAgentsMethods()
    {
        var client = Start("auth-required");
        await client.ConnectAsync().Within(40);

        var ex = await Assert.ThrowsAsync<AcpAuthRequiredException>(() => client.NewSessionAsync(_work).Within(20));
        Assert.Equal("mock-login", ex.AuthMethods[0].Id);
    }

    [Fact]
    public async Task Permission_RoundTrips()
    {
        var session = await SessionAsync(Start("permission", new Handler { PermissionOption = "reject" }));

        var turn = session.Prompt([new TextContent("go")]);

        Assert.Equal("permission: selected:reject", await TextAsync(turn).Within(20));
    }

    [Fact]
    public async Task Cancel_WhilePermissionIsOpen_EndsTheTurnCancelled()
    {
        var handler = new Handler { HoldPermission = true };
        var session = await SessionAsync(Start("permission", handler));

        var turn = session.Prompt([new TextContent("go")]);
        await handler.PermissionAsked.Task.Within(20);

        Assert.True(await session.CancelAsync().Within(20));
        Assert.Equal(StopReasons.Cancelled, await turn.Completion.Within(20));
        Assert.Equal("permission: cancelled", await TextAsync(turn));
    }

    [Fact]
    public async Task Cancel_StopsAStreamingTurn()
    {
        var session = await SessionAsync(Start("slow-cancel"));

        var turn = session.Prompt([new TextContent("go")]);
        var enumerator = turn.Updates.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().Within(20));

        Assert.True(await session.CancelAsync().Within(20));
        Assert.Equal(StopReasons.Cancelled, await turn.Completion.Within(20));
    }

    [Fact]
    public async Task Cancel_ToAnAgentThatIgnoresIt_ReportsUnresponsive()
    {
        var session = await SessionAsync(Start("ignore-cancel", cancelGrace: TimeSpan.FromMilliseconds(500)));

        session.Prompt([new TextContent("go")]);
        await Task.Delay(200);

        Assert.False(await session.CancelAsync().Within(20));
    }

    [Fact]
    public async Task CrashMidTurn_FailsWithExitCodeAndStderr()
    {
        var session = await SessionAsync(Start("crash-mid-turn"));

        var turn = session.Prompt([new TextContent("go")]);
        var text = await TextAsync(turn).Within(20);

        var ex = await Assert.ThrowsAsync<AcpConnectionClosedException>(() => turn.Completion.Within(20));
        Assert.Equal("about to crash", text);
        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("panic: unrecoverable", ex.StderrTail);
    }

    [Fact]
    public async Task UnknownUpdates_AreSkipped_AndTheKnownOnesArrive()
    {
        var session = await SessionAsync(Start("unknown-updates"));

        var turn = session.Prompt([new TextContent("go")]);

        Assert.Equal("after unknowns", await TextAsync(turn).Within(20));
        Assert.Equal("a_future_stop_reason", await turn.Completion);
    }

    [Fact]
    public async Task FileSystem_ReadsAndWritesThroughTheHandler()
    {
        var path = Path.Combine(_work, "note.txt");
        File.WriteAllText(path, "hello");
        var session = await SessionAsync(Start("fs-roundtrip"));

        var turn = session.Prompt([new TextContent(path)]);

        Assert.Equal("read 5 chars, wrote copy", await TextAsync(turn).Within(20));
        Assert.Equal("HELLO", File.ReadAllText(path + ".out"));
    }
}
