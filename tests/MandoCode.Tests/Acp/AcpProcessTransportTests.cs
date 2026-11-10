using System.Diagnostics;
using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.JsonRpc;
using MandoCode.Acp.Protocol;
using MandoCode.Acp.Transport;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>
/// Runs the real <see cref="AcpProcessTransport"/> against the mock agent (a separate .NET process),
/// so process launching, stdio plumbing, environment and shutdown are exercised for real.
/// </summary>
public class AcpProcessTransportTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "mandocode-acp-work-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _cleanup = [];

    public AcpProcessTransportTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        foreach (var item in _cleanup)
            item.DisposeAsync().AsTask().GetAwaiter().GetResult();

        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private static string DotnetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;

    private static string MockAgentDll() =>
        Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");

    private AcpLaunchSpec Spec(
        string scenario,
        IReadOnlyList<string>? extraArgs = null,
        Dictionary<string, string>? env = null,
        bool passApiKey = false,
        string? workingDirectory = null) =>
        new("mock-" + scenario, DotnetHost(), [MockAgentDll(), scenario, .. extraArgs ?? []], env, passApiKey, workingDirectory ?? _workDir);

    private AcpProcessTransport Start(AcpLaunchSpec spec, StderrLog? log = null, AcpProcessOptions? options = null)
    {
        var transport = AcpProcessTransport.Start(spec, new ResolvedCommand(spec.Command, spec.Args), log ?? new StderrLog(), options);
        _cleanup.Add(transport);
        return transport;
    }

    private JsonRpcConnection Connect(AcpProcessTransport transport)
    {
        var connection = new JsonRpcConnection(transport.Input, transport.Output,
            new JsonRpcConnectionOptions { DescribeClose = transport.DescribeCloseAsync, MaxConsecutiveParseErrors = 3 });
        _cleanup.Add(connection);
        return connection;
    }

    private static Task<InitializeResponse> Initialize(JsonRpcConnection connection) =>
        connection.SendRequestAsync(AcpMethods.Initialize, new InitializeRequest(),
            AcpJsonContext.Default.InitializeRequest, AcpJsonContext.Default.InitializeResponse);

    [Fact]
    public async Task LaunchesTheAgent_AndTalksJsonRpcOverItsStdio()
    {
        var transport = Start(Spec("echo"));
        var connection = Connect(transport);

        var response = await Initialize(connection).Within(20);

        Assert.Equal("mock-echo", response.AgentInfo!.Name);
        Assert.True(response.AgentCapabilities.PromptCapabilities.EmbeddedContext);
    }

    [Fact]
    public async Task ArgumentsAndWorkingDirectory_ArriveExactly_WithoutAShell()
    {
        string[] tricky =
        [
            "plain",
            "with space",
            "\"quoted\"",
            @"C:\path with space\dir\",
            "--flag=va lue",
            "semi;colon & ampersand | pipe > redirect",
            "$HOME `whoami` %PATH%",
            "ünï-ко́д-日本",
        ];
        var transport = Start(Spec("echo", tricky));
        var connection = Connect(transport);

        var response = await Initialize(connection).Within(20);
        var meta = response.Meta!.Value;

        Assert.Equal(tricky, meta.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
        Assert.Equal(Path.GetFullPath(_workDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(meta.GetProperty("cwd").GetString()!).TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public async Task ApiKey_IsNotInheritedByDefault_ButOtherVariablesAre()
    {
        using var scope = new EnvScope(("ANTHROPIC_API_KEY", "sk-from-the-test-process"), ("MOCK_INHERITED", "yes"));

        var transport = Start(Spec("echo", env: new Dictionary<string, string> { ["MOCK_FOO"] = "bar" }));
        var connection = Connect(transport);

        var env = (await Initialize(connection).Within(20)).Meta!.Value.GetProperty("env");

        Assert.Equal(JsonValueKind.Null, env.GetProperty("ANTHROPIC_API_KEY").ValueKind);
        Assert.Equal("yes", env.GetProperty("MOCK_INHERITED").GetString());
        Assert.Equal("bar", env.GetProperty("MOCK_FOO").GetString());
    }

    [Fact]
    public async Task ApiKey_IsPassedWhenTheEntryOptsIn()
    {
        using var scope = new EnvScope(("ANTHROPIC_API_KEY", "sk-from-the-test-process"));

        var transport = Start(Spec("echo", passApiKey: true));
        var connection = Connect(transport);

        var env = (await Initialize(connection).Within(20)).Meta!.Value.GetProperty("env");
        Assert.Equal("sk-from-the-test-process", env.GetProperty("ANTHROPIC_API_KEY").GetString());
    }

    [Fact]
    public async Task RealPromptRoundTrip_StreamsAnUpdateBeforeTheResponse()
    {
        var transport = Start(Spec("echo"));
        var connection = Connect(transport);

        var chunks = new List<string>();
        connection.RegisterNotificationHandler(AcpMethods.SessionUpdate, AcpJsonContext.Default.SessionNotification, n =>
        {
            if (n.Update is AgentMessageChunk { Content: TextContent text })
                chunks.Add(text.Text);
            return ValueTask.CompletedTask;
        });

        await Initialize(connection).Within(20);
        var session = await connection.SendRequestAsync(AcpMethods.SessionNew, new NewSessionRequest { Cwd = _workDir },
            AcpJsonContext.Default.NewSessionRequest, AcpJsonContext.Default.NewSessionResponse).Within();
        var prompt = await connection.SendRequestAsync(AcpMethods.SessionPrompt,
            new PromptRequest { SessionId = session.SessionId, Prompt = [new TextContent("hello")] },
            AcpJsonContext.Default.PromptRequest, AcpJsonContext.Default.PromptResponse).Within();
        await connection.DrainNotificationsAsync().Within();

        Assert.Equal(StopReasons.EndTurn, prompt.StopReason);
        Assert.Equal(["echo: hello"], chunks);
    }

    [Fact]
    public async Task ExitDuringStartup_FailsThePendingRequest_WithExitCodeAndStderrTail()
    {
        var transport = Start(Spec("exit-immediately"));
        var connection = Connect(transport);

        var ex = await Assert.ThrowsAsync<AcpConnectionClosedException>(() => Initialize(connection).Within(20));

        Assert.Equal(7, ex.ExitCode);
        Assert.Contains("fatal: cannot start", ex.StderrTail);
        Assert.Equal(7, await transport.Exited.Within());
    }

    [Fact]
    public async Task StderrFlood_DoesNotDeadlock_AndIsBoundedInMemory()
    {
        var log = new StderrLog(capacity: 200);
        var transport = Start(Spec("stderr-flood"), log);
        var connection = Connect(transport);

        // The agent writes ~1.7 MB to stderr before it reads stdin; this only completes if stderr is drained.
        await Initialize(connection).Within(30);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!log.Tail(1).Contains("flood line 19999") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        var lines = log.Tail().Split('\n');
        Assert.True(lines.Length <= 200, $"expected at most 200 buffered lines, got {lines.Length}");
        Assert.Contains("flood line 19999", lines[^1]);
    }

    [Fact]
    public async Task Stderr_NeverReachesTheProtocolStream()
    {
        var log = new StderrLog();
        var transport = Start(Spec("exit-immediately"), log);
        var connection = Connect(transport);

        await Assert.ThrowsAsync<AcpConnectionClosedException>(() => Initialize(connection).Within(20));

        Assert.Contains("mock-agent: fatal", log.Tail());
    }

    [Fact]
    public async Task GarbageOnStdout_BreaksTheConnection_WithAnExplanation()
    {
        var transport = Start(Spec("stdout-garbage"));
        var connection = Connect(transport);

        var violation = await Assert.ThrowsAsync<AcpProtocolViolationException>(() => connection.Completion.Within(20));

        Assert.Contains("stdout", violation.Message);
    }

    [Fact]
    public async Task PoliteShutdown_ClosesStdin_AndTheAgentExitsOnItsOwn()
    {
        var transport = Start(Spec("echo"));
        var connection = Connect(transport);
        await Initialize(connection).Within(20);

        var stopwatch = Stopwatch.StartNew();
        await connection.DisposeAsync();
        await transport.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"shutdown took {stopwatch.Elapsed}");
        Assert.Equal(0, await transport.Exited.Within());
    }

    [Fact]
    public async Task AgentThatIgnoresStdinEof_IsKilled_TogetherWithItsChildProcess()
    {
        var log = new StderrLog();
        var transport = Start(Spec("hang-on-shutdown"), log,
            new AcpProcessOptions { ShutdownGrace = TimeSpan.FromMilliseconds(300) });
        var connection = Connect(transport);
        await Initialize(connection).Within(20);

        var childPid = await WaitForChildPid(log);
        var agentPid = transport.ProcessId;
        Assert.False(IsGone(childPid), "the child process should be alive before shutdown");

        await connection.DisposeAsync();
        await transport.DisposeAsync();

        await WaitUntil(() => IsGone(agentPid) && IsGone(childPid), TimeSpan.FromSeconds(10));
        Assert.True(IsGone(agentPid), "the agent process survived shutdown");
        Assert.True(IsGone(childPid), "the agent's child process survived shutdown (the tree was not killed)");
    }

    [Fact]
    public void StartFailure_IsReportedAsALaunchError()
    {
        var spec = Spec("echo", workingDirectory: Path.Combine(_workDir, "does", "not", "exist"));

        var ex = Assert.Throws<AcpLaunchException>(() =>
            AcpProcessTransport.Start(spec, new ResolvedCommand(spec.Command, spec.Args), new StderrLog()));

        Assert.Equal(AcpLaunchFailure.StartFailed, ex.Failure);
    }

    [Fact]
    public void TheRealResolver_FindsDotnet_AsANativeExecutable()
    {
        var resolved = AcpCommandResolver.Resolve("dotnet", [MockAgentDll(), "echo"]);

        Assert.True(Path.IsPathRooted(resolved.ExecutablePath));
        Assert.True(File.Exists(resolved.ExecutablePath));
        if (OperatingSystem.IsWindows())
            Assert.EndsWith(".exe", resolved.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── helpers ─────────────

    private static async Task<int> WaitForChildPid(StderrLog log)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var line = log.Tail().Split('\n').FirstOrDefault(l => l.StartsWith("child-pid: ", StringComparison.Ordinal));
            if (line is not null)
                return int.Parse(line["child-pid: ".Length..]);
            await Task.Delay(50);
        }

        throw new TimeoutException("The mock agent never reported its child pid.");
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    /// <summary>Sets environment variables for the test and restores them afterwards.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly (string Name, string? Old)[] _saved;

        public EnvScope(params (string Name, string Value)[] variables)
        {
            _saved = variables.Select(v => (v.Name, Environment.GetEnvironmentVariable(v.Name))).ToArray();
            foreach (var (name, value) in variables)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, old) in _saved)
                Environment.SetEnvironmentVariable(name, old);
        }
    }
}
