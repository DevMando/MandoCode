using MandoCode.Acp.Transport;
using MandoCode.Models;
using MandoCode.Services.Acp;
using Xunit;

namespace MandoCode.Tests.Acp;

[Collection("AcpProcesses")]
public class AcpAgentCommandsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
    private static readonly string MockDll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");

    private readonly InMemoryTrustStore _trust = new();
    private readonly FakeCatalog _catalog = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private int _asked;
    private bool _answer = true;

    public AcpAgentCommandsTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeCatalog : IAcpAgentCatalog
    {
        public Dictionary<string, AcpAgentConfig> Stored { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Saves { get; private set; }
        public IReadOnlyDictionary<string, AcpAgentConfig> Agents => Stored;
        public void Save(IReadOnlyDictionary<string, AcpAgentConfig> agents)
        {
            Stored = new Dictionary<string, AcpAgentConfig>(agents, StringComparer.OrdinalIgnoreCase);
            Saves++;
        }
    }

    private Task Run(string arguments)
    {
        var commands = new AcpAgentCommands(_catalog, new AcpAgentLauncher(_trust), logDirectory: Path.Combine(_work, "logs"));
        return commands.ExecuteAsync(arguments, _work, _ => { _asked++; return Task.FromResult(_answer); }, _out, _err);
    }

    private static string Quoted(string path) => "\"" + path + "\"";

    [Fact]
    public async Task Status_ShowsTheLogPaths_AndWhetherTheTraceIsOn()
    {
        _catalog.Stored["quiet"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"] };
        _catalog.Stored["loud"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"], Trace = true };
        var before = Environment.GetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable);
        Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, null);
        try
        {
            await Run("status");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AcpLogPaths.TraceEnvironmentVariable, before);
        }

        var text = _out.ToString();
        var loud = text[text.IndexOf("loud", StringComparison.Ordinal)..text.IndexOf("quiet", StringComparison.Ordinal)];
        var quiet = text[text.IndexOf("quiet", StringComparison.Ordinal)..];
        Assert.Contains("trace: on", loud);
        Assert.Contains(".trace.log (not created yet)", loud);
        Assert.Contains("stderr log:", loud);
        Assert.Contains("trace: off", quiet);
        Assert.DoesNotContain(".trace.log", quiet);
    }

    [Fact]
    public async Task Status_OfAnUnknownAgent_IsAnError()
    {
        await Run("status nope");

        Assert.Contains("No ACP agent named 'nope'", _err.ToString());
    }

    [Fact]
    public async Task List_WhenEmpty_ExplainsHowToAdd()
    {
        await Run("");

        Assert.Contains("No ACP agents are configured", _out.ToString());
        Assert.Contains("/agent-acp add", _out.ToString());
    }

    [Fact]
    public async Task Add_StoresTheEntry_WithQuotedArguments_AndDoesNotStartOrTrustAnything()
    {
        await Run($"add mock {Quoted(_host)} {Quoted(MockDll)} echo");

        var entry = _catalog.Stored["mock"];
        Assert.Equal(_host, entry.Command);
        Assert.Equal([MockDll, "echo"], entry.Args);
        Assert.Empty(_trust.Hashes);
        Assert.Equal(0, _asked);
        Assert.Contains("Added", _out.ToString());
    }

    [Theory]
    [InlineData("add a npx -y some-package")]
    [InlineData("add a cmd /c something")]
    [InlineData("add a powershell -File x.ps1")]
    public async Task Add_RefusesDownloadersAndShells(string arguments)
    {
        await Run(arguments);

        Assert.Empty(_catalog.Stored);
        Assert.Contains("Not added", _err.ToString());
    }

    [Fact]
    public async Task Add_RefusesNodeWithARelativeScript()
    {
        await Run("add a node ./adapter/index.js");

        Assert.Empty(_catalog.Stored);
        Assert.Contains("Not added", _err.ToString());
    }

    [Theory]
    [InlineData("add bad/name " + "x")]
    [InlineData("add \"\" x")]
    public async Task Add_RejectsBadNames(string arguments)
    {
        await Run(arguments);

        Assert.Empty(_catalog.Stored);
        Assert.NotEmpty(_err.ToString());
    }

    [Fact]
    public async Task Add_DoesNotOverwrite_AnExistingName()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host };

        await Run($"add MOCK {Quoted(_host)}");

        Assert.Contains("already exists", _err.ToString());
        Assert.Equal(0, _catalog.Saves);
    }

    [Fact]
    public async Task Add_Claude_WithoutACommand_ShowsGuidanceAndInventsNothing()
    {
        await Run("add claude");

        Assert.Empty(_catalog.Stored);
        var text = _out.ToString();
        Assert.Contains("no built-in setup", text);
        Assert.DoesNotContain("npx", text.Replace("no npx", ""));
        Assert.DoesNotContain("@", text);
    }

    [Fact]
    public async Task Add_Claude_WithACommand_IsAnOrdinaryEntry()
    {
        await Run($"add claude {Quoted(_host)}");

        Assert.Equal(_host, _catalog.Stored["claude"].Command);
    }

    [Fact]
    public async Task Remove_DeletesTheEntry_AndItsTrust()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host };
        _trust.Set("mock", "hash");

        await Run("remove MOCK");

        Assert.Empty(_catalog.Stored);
        Assert.Empty(_trust.Hashes);
    }

    [Fact]
    public async Task Remove_UnknownName_IsAnError()
    {
        await Run("remove nope");

        Assert.Contains("No ACP agent named 'nope'", _err.ToString());
    }

    [Fact]
    public async Task AddingAgainAfterRemove_StartsWithoutTrust()
    {
        await Run($"add mock {Quoted(_host)} {Quoted(MockDll)}");
        var plan = new AcpAgentLauncher(_trust).Prepare("mock", _catalog.Stored["mock"], _work);
        _trust.Set("mock", plan.Hash);
        await Run("remove mock");
        await Run($"add mock {Quoted(_host)} {Quoted(MockDll)}");

        Assert.Empty(_trust.Hashes);
    }

    [Fact]
    public async Task DisableAndEnable_ToggleTheFlag_AndKeepTrust()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll], Env = { ["K"] = "V" }, AuthHint = "hint" };
        var launcher = new AcpAgentLauncher(_trust);
        _trust.Set("mock", launcher.Prepare("mock", _catalog.Stored["mock"], _work).Hash);

        await Run("disable mock");
        Assert.True(_catalog.Stored["mock"].Disabled);
        Assert.Equal("V", _catalog.Stored["mock"].Env["K"]);
        Assert.Equal("hint", _catalog.Stored["mock"].AuthHint);
        Assert.True(launcher.IsTrusted(launcher.Prepare("mock", _catalog.Stored["mock"], _work)));

        await Run("enable mock");
        Assert.False(_catalog.Stored["mock"].Disabled);
    }

    [Fact]
    public async Task List_ShowsStateAndTrust()
    {
        _catalog.Stored["a"] = new AcpAgentConfig { Command = _host, Args = [MockDll] };
        _catalog.Stored["b"] = new AcpAgentConfig { Command = "npx", Disabled = true };
        _trust.Set("a", new AcpAgentLauncher(_trust).Prepare("a", _catalog.Stored["a"], _work).Hash);

        await Run("list");

        var lines = _out.ToString().Split('\n');
        Assert.Contains("[enabled, trusted]", lines.Single(l => l.StartsWith("a ")));
        Assert.Contains("[disabled, command refused]", lines.Single(l => l.StartsWith("b ")));
    }

    [Fact]
    public async Task Test_AsksFirst_AndDeclineStartsNothing()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"] };
        _answer = false;

        await Run("test mock");

        Assert.Equal(1, _asked);
        Assert.Contains("not approved", _out.ToString());
        Assert.DoesNotContain("initialize: ok", _out.ToString());
        Assert.Empty(_trust.Hashes);
    }

    [Fact]
    public async Task Test_AfterApproval_RunsTheHandshake_AndLaterRunsDoNotAsk()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"] };

        await Run("test mock");
        await Run("test mock");

        Assert.Equal(1, _asked);
        Assert.Equal(2, _out.ToString().Split("initialize: ok").Length - 1);
        Assert.Contains("mock-echo", _out.ToString());
    }

    [Fact]
    public async Task Test_AfterTheEntryChanges_AsksAgain()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"] };
        await Run("test mock");

        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo", "extra"] };
        await Run("test mock");

        Assert.Equal(2, _asked);
    }

    [Fact]
    public async Task Test_RefusesADisabledAgent_WithoutAsking()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll], Disabled = true };

        await Run("test mock");

        Assert.Equal(0, _asked);
        Assert.Contains("disabled", _err.ToString());
    }

    [Fact]
    public async Task Test_ReportsAMissingLogin_WithTheConfiguredHint()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "auth-required"], AuthHint = "run mock-login first" };

        await Run("test mock");

        Assert.Contains("not signed in", _err.ToString());
        Assert.Contains("run mock-login first", _err.ToString());
    }

    [Fact]
    public async Task Test_ReportsAnAgentThatDiesAtStartup_WithItsStderr()
    {
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "exit-immediately"] };

        await Run("test mock");

        Assert.Contains("exited with code 7", _err.ToString());
        Assert.Contains("cannot start", _err.ToString());
    }

    [Fact]
    public async Task Test_UnknownAgent_ListsWhatIsConfigured()
    {
        _catalog.Stored["alpha"] = new AcpAgentConfig { Command = _host };

        await Run("test nope");

        Assert.Contains("No ACP agent named 'nope'", _err.ToString());
        Assert.Contains("alpha", _err.ToString());
    }

    [Fact]
    public async Task Test_DoesNotPassTheApiKey_ByDefault()
    {
        // The mock reports ANTHROPIC_API_KEY from its own environment in initialize._meta; here we only check the plan text.
        _catalog.Stored["mock"] = new AcpAgentConfig { Command = _host, Args = [MockDll, "echo"] };
        var seen = new List<string>();
        var commands = new AcpAgentCommands(_catalog, new AcpAgentLauncher(_trust), logDirectory: Path.Combine(_work, "logs"));

        await commands.ExecuteAsync("test mock", _work, plan => { seen.AddRange(plan.Describe()); return Task.FromResult(false); }, _out, _err);

        Assert.Contains(seen, l => l.Contains("ANTHROPIC_API_KEY: removed"));
    }

    [Fact]
    public async Task UnknownSubcommand_PrintsUsage()
    {
        await Run("frobnicate");

        Assert.Contains("Unknown /agent-acp command", _err.ToString());
        Assert.Contains("Usage: /agent-acp", _err.ToString());
    }

    [Theory]
    [InlineData("a b c", new[] { "a", "b", "c" })]
    [InlineData("a   \"b c\"  d", new[] { "a", "b c", "d" })]
    [InlineData(@"add x ""C:\Program Files\n\i.js""", new[] { "add", "x", @"C:\Program Files\n\i.js" })]
    [InlineData("a \"\" b", new[] { "a", "", "b" })]
    [InlineData("", new string[0])]
    public void Tokenize_GroupsQuotedText_AndKeepsBackslashes(string text, string[] expected) =>
        Assert.Equal(expected, AcpAgentCommands.Tokenize(text));

    [Fact]
    public void ConfigCatalog_UpdatesEveryPanesConfig_NotJustTheCurrentOne()
    {
        var current = new MandoCodeConfig { AllowPersistence = false };
        var other = new MandoCodeConfig { AllowPersistence = false };
        var catalog = new ConfigAcpAgentCatalog(current, [current, other]);

        catalog.Save(new Dictionary<string, AcpAgentConfig> { ["x"] = new() { Command = "c" } });

        Assert.Equal("c", current.AcpAgents["X"].Command);
        Assert.Equal("c", other.AcpAgents["x"].Command);
    }

    [Fact]
    public void ProbeArguments_TakeAConfiguredNameAndAnOptionalFolder()
    {
        var error = new StringWriter();

        var parsed = AcpConnectionProbe.Parse(["--cwd", _work, "claude"], "/default", error);

        Assert.Equal("claude", parsed!.Value.Name);
        Assert.Equal(Path.GetFullPath(_work), parsed.Value.WorkingDirectory);
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("--yes", "claude")]
    [InlineData("a", "b")]
    [InlineData("--cwd")]
    public void ProbeArguments_RejectCommandsAndBypassFlags(params string[] args)
    {
        var error = new StringWriter();

        Assert.Null(AcpConnectionProbe.Parse(args, "/default", error));
        Assert.Contains("Usage: mandocode --acp-probe", error.ToString());
    }

    private sealed class FakeHost : IAcpPaneHost
    {
        public List<string> Opened { get; } = [];
        public List<string> Switched { get; } = [];
        public int Restarts;
        public int Stops;
        public string? OpenError { get; set; }
        public string? SwitchError { get; set; }
        public List<string> Resumed { get; } = [];
        public int SessionListings;
        public string? SessionsError { get; set; }
        public Task<string?> OpenPaneAsync(string agentName, bool resume = false) { Opened.Add(agentName); if (resume) Resumed.Add("open:" + agentName); return Task.FromResult(OpenError); }
        public Task<string?> SwitchAsync(string agentName, bool resume = false) { Switched.Add(agentName); if (resume) Resumed.Add("switch:" + agentName); return Task.FromResult(SwitchError); }
        public Task<string?> SessionsAsync() { SessionListings++; return Task.FromResult(SessionsError); }
        public Task<string?> StopAsync() { Stops++; return Task.FromResult<string?>(null); }
        public Task<string?> RestartAsync() { Restarts++; return Task.FromResult<string?>(null); }
    }

    private Task RunWith(string arguments, IAcpPaneHost? host) =>
        new AcpAgentCommands(_catalog, new AcpAgentLauncher(_trust), logDirectory: Path.Combine(_work, "logs"), host: host)
            .ExecuteAsync(arguments, _work, _ => Task.FromResult(true), _out, _err);

    [Fact]
    public async Task AgentName_SwitchesTheCurrentPane_ByDefault()
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };
        var host = new FakeHost();

        await RunWith("CLAUDE", host);

        Assert.Equal(["CLAUDE"], host.Switched);
        Assert.Empty(host.Opened);
    }

    [Theory]
    [InlineData("claude --resume", "switch:claude")]
    [InlineData("--resume claude", "switch:claude")]
    [InlineData("claude --new-panel --resume", "open:claude")]
    public async Task ResumeFlag_IsPassedToTheHost(string arguments, string expected)
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };
        var host = new FakeHost();

        await RunWith(arguments, host);

        Assert.Equal([expected], host.Resumed);
    }

    [Fact]
    public async Task WithoutTheResumeFlag_NothingIsResumed()
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };
        var host = new FakeHost();

        await RunWith("claude", host);

        Assert.Empty(host.Resumed);
    }

    [Fact]
    public async Task Sessions_AsksTheHost_AndPrintsItsError()
    {
        var host = new FakeHost { SessionsError = "no list support" };

        await RunWith("sessions", host);

        Assert.Equal(1, host.SessionListings);
        Assert.Contains("no list support", _err.ToString());
    }

    [Fact]
    public async Task Sessions_WithoutAPane_SaysWhereItWorks()
    {
        await RunWith("sessions", host: null);

        Assert.Contains("inside an ACP agent pane", _err.ToString());
    }

    [Theory]
    [InlineData("claude --new-panel")]
    [InlineData("--new-panel claude")]
    public async Task NewPanelFlag_OpensANewPane_InEitherPosition(string arguments)
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };
        var host = new FakeHost();

        await RunWith(arguments, host);

        Assert.Equal(["claude"], host.Opened);
        Assert.Empty(host.Switched);
        Assert.Contains("Opened ACP agent", _out.ToString());
    }

    [Fact]
    public async Task NewPanel_ReportsThePaneLimit()
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };

        await RunWith("claude --new-panel", new FakeHost { OpenError = "Four agent panes are already open. Close one first." });

        Assert.Contains("Four agent panes", _err.ToString());
        Assert.DoesNotContain("Opened ACP agent", _out.ToString());
    }

    [Fact]
    public async Task ABusyPane_ExplainsWhyItWasNotSwitched()
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };

        await RunWith("claude", new FakeHost { SwitchError = "This agent is busy. Wait for it to finish, or use --new-panel." });

        Assert.Contains("busy", _err.ToString());
    }

    [Theory]
    [InlineData("claude --bogus", "Unknown option '--bogus'")]
    [InlineData("--new-panel", "Usage")]
    [InlineData("claude other", "Usage")]
    public async Task BadFlagsOrNames_AreRejected_WithoutTouchingAPane(string arguments, string expected)
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host };
        _catalog.Stored["other"] = new AcpAgentConfig { Command = _host };
        var host = new FakeHost();

        await RunWith(arguments, host);

        Assert.Contains(expected, _err.ToString());
        Assert.Empty(host.Opened);
        Assert.Empty(host.Switched);
    }

    [Fact]
    public async Task AnAgentCannotBeNamedLikeAFlag()
    {
        await Run($"add --new-panel {Quoted(_host)}");
        await Run($"add -x {Quoted(_host)}");

        Assert.Empty(_catalog.Stored);
    }

    [Fact]
    public async Task Stop_GoesToThePaneHost()
    {
        var host = new FakeHost();

        await RunWith("stop", host);

        Assert.Equal(1, host.Stops);
    }

    [Fact]
    public async Task Stop_WithoutAHost_SaysWhere()
    {
        await RunWith("stop", host: null);

        Assert.Contains("inside an ACP agent pane", _err.ToString());
    }

    [Fact]
    public async Task ADisabledAgent_IsNotOpened()
    {
        _catalog.Stored["claude"] = new AcpAgentConfig { Command = _host, Disabled = true };
        var host = new FakeHost();

        await RunWith("claude", host);

        Assert.Empty(host.Opened);
        Assert.Contains("disabled", _err.ToString());
    }

    [Fact]
    public async Task Restart_GoesToThePaneHost()
    {
        var host = new FakeHost();

        await RunWith("restart", host);

        Assert.Equal(1, host.Restarts);
    }

    [Fact]
    public async Task Restart_WithoutAnAcpPane_SaysWhere()
    {
        await RunWith("restart", host: null);

        Assert.Contains("inside an ACP agent pane", _err.ToString());
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("stop")]
    [InlineData("List")]
    [InlineData("add")]
    public async Task CommandWords_CannotBeAgentNames(string name)
    {
        await Run($"add {name} {Quoted(_host)}");

        Assert.Empty(_catalog.Stored);
        Assert.Contains("command word", _err.ToString());
    }
}
