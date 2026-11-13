using System.Diagnostics;
using System.Text.Json.Nodes;
using MandoCode.Acp.Transport;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpCatalogSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mandocode-acp-save-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public AcpCatalogSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Dictionary<string, AcpAgentConfig> One(string command = "c") => new() { ["x"] = new() { Command = command } };

    [Fact]
    public void Save_ChangesOnlyTheAcpAgentsSection_AndKeepsWhatIsOnDisk()
    {
        // The file was changed by someone else after this pane loaded its copy.
        File.WriteAllText(ConfigPath, """{ "modelName": "changed-on-disk", "temperature": 0.9, "somethingNew": { "a": [1, 2] } }""");
        var stale = new MandoCodeConfig { ModelName = "stale-in-memory", Temperature = 0.1 };

        new ConfigAcpAgentCatalog(stale, [stale], ConfigPath).Save(One());

        var root = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("changed-on-disk", (string?)root["modelName"]);
        Assert.Equal(0.9, (double?)root["temperature"]);
        Assert.Equal(2, root["somethingNew"]!["a"]!.AsArray().Count);
        Assert.Equal("c", (string?)root["acpAgents"]!["x"]!["command"]);
    }

    [Fact]
    public void Save_UpdatesEveryPanesCopy_WithoutTouchingTheirOtherSettings()
    {
        var current = new MandoCodeConfig { ModelName = "a" };
        var other = new MandoCodeConfig { ModelName = "b" };

        new ConfigAcpAgentCatalog(current, [current, other], ConfigPath).Save(One());

        Assert.Equal("c", current.AcpAgents["X"].Command);
        Assert.Equal("c", other.AcpAgents["x"].Command);
        Assert.Equal("b", other.ModelName);
    }

    [Fact]
    public void Save_CreatesTheFile_WhenThereIsNone()
    {
        var config = new MandoCodeConfig();

        new ConfigAcpAgentCatalog(config, [config], Path.Combine(_dir, "new", "config.json")).Save(One());

        Assert.True(File.Exists(Path.Combine(_dir, "new", "config.json")));
    }

    [Fact]
    public void Save_RefusesToOverwriteAFileItCannotParse()
    {
        File.WriteAllText(ConfigPath, "{ definitely not json");
        var config = new MandoCodeConfig();

        Assert.ThrowsAny<Exception>(() => new ConfigAcpAgentCatalog(config, [config], ConfigPath).Save(One()));

        Assert.Equal("{ definitely not json", File.ReadAllText(ConfigPath));
        Assert.Empty(config.AcpAgents);
    }

    [Fact]
    public void Save_WithPersistenceOff_OnlyUpdatesMemory()
    {
        var config = new MandoCodeConfig { AllowPersistence = false };

        new ConfigAcpAgentCatalog(config, [config], ConfigPath).Save(One());

        Assert.False(File.Exists(ConfigPath));
        Assert.Equal("c", config.AcpAgents["x"].Command);
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCorruptTheFile()
    {
        File.WriteAllText(ConfigPath, """{ "modelName": "keep" }""");
        var config = new MandoCodeConfig();
        var catalog = new ConfigAcpAgentCatalog(config, [config], ConfigPath);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => catalog.Save(One("c" + i)))));

        var root = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
        Assert.Equal("keep", (string?)root["modelName"]);
        Assert.StartsWith("c", (string?)root["acpAgents"]!["x"]!["command"]);
    }
}

public class AcpTrustPromptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mandocode acp [prompt] @x." + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;

    public AcpTrustPromptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Harness
    {
        public ApprovalSelectCoordinator Menu { get; } = new();
        public ApprovalPromptGate Gate { get; } = new();
        public List<string> Lines { get; } = [];
        public int KeysSuppressed;
        public int KeysReleased;

        public AcpTrustPrompt Prompt() => new(Gate, Menu, () =>
        {
            KeysSuppressed++;
            return new Releaser(this);
        }, Lines.Add);

        private sealed class Releaser(Harness owner) : IDisposable
        {
            public void Dispose() => owner.KeysReleased++;
        }

        /// <summary>Answers the menu the way the VDOM component would, once it is shown.</summary>
        public async Task Answer(string choice)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Menu.IsActive && DateTime.UtcNow < deadline)
                await Task.Delay(5);
            Assert.True(Menu.IsActive, "the menu was never shown");
            Assert.Equal([AcpTrustPrompt.TrustLabel, AcpTrustPrompt.CancelLabel], Menu.Options.Select(o => o.Text));
            Menu.Submit(choice);
        }
    }

    private AcpAgentConfig Entry(params string[] args) => new() { Command = _host, Args = [.. args] };

    [Fact]
    public async Task Approving_StartsTheAgent_AndRemembersIt()
    {
        var h = new Harness();
        var trust = new InMemoryTrustStore();
        var launcher = new AcpAgentLauncher(trust);
        var plan = launcher.Prepare("agent", Entry("a"), _dir);

        var decision = launcher.EnsureTrustedAsync(plan, h.Prompt().ConfirmAsync);
        await h.Answer(AcpTrustPrompt.TrustLabel);

        Assert.True(await decision);
        Assert.Equal(plan.Hash, trust.GetHash("agent"));
        Assert.Equal(h.KeysSuppressed, h.KeysReleased);
        Assert.False(h.Menu.IsActive);
    }

    [Fact]
    public async Task Cancelling_StartsNothing_AndStoresNothing()
    {
        var h = new Harness();
        var trust = new InMemoryTrustStore();
        var launcher = new AcpAgentLauncher(trust);

        var decision = launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry(), _dir), h.Prompt().ConfirmAsync);
        await h.Answer(AcpTrustPrompt.CancelLabel);

        Assert.False(await decision);
        Assert.Empty(trust.Hashes);
    }

    [Fact]
    public async Task AnAbandonedMenu_IsNotAnApproval()
    {
        var h = new Harness();
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());

        var decision = launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry(), _dir), h.Prompt().ConfirmAsync);
        await h.Answer(string.Empty); // what the coordinator yields when a newer request replaces this one

        Assert.False(await decision);
    }

    [Fact]
    public async Task ATrustedLaunch_IsNotAskedAgain_ButAChangedOneIs()
    {
        var h = new Harness();
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var first = launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry("a"), _dir), h.Prompt().ConfirmAsync);
        await h.Answer(AcpTrustPrompt.TrustLabel);
        Assert.True(await first);

        Assert.True(await launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry("a"), _dir), h.Prompt().ConfirmAsync));
        Assert.False(h.Menu.IsActive);

        var changed = launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry("a", "--new-flag"), _dir), h.Prompt().ConfirmAsync);
        await h.Answer(AcpTrustPrompt.CancelLabel);
        Assert.False(await changed);
    }

    [Fact]
    public async Task TwoQuestions_AreAskedOneAtATime()
    {
        var h = new Harness();
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var one = launcher.EnsureTrustedAsync(launcher.Prepare("one", Entry(), _dir), h.Prompt().ConfirmAsync);
        var two = launcher.EnsureTrustedAsync(launcher.Prepare("two", Entry(), _dir), h.Prompt().ConfirmAsync);

        await h.Answer(AcpTrustPrompt.TrustLabel);
        await h.Answer(AcpTrustPrompt.TrustLabel);

        Assert.True(await one);
        Assert.True(await two);
    }

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\Users\me\agents\claude-agent-acp\node_modules\@agentclientprotocol\claude-agent-acp\dist\index.js")]
    [InlineData(@"D:\my.dir\index.js")]
    [InlineData("/opt/my agent/dist/index.js")]
    [InlineData("[red]not markup[/]")]
    [InlineData(@"\\server\share\a b\x.exe")]
    public async Task PathsAndArguments_ArePrintedLiterally_InQuotes_OnTheirOwnLine(string argument)
    {
        var h = new Harness();
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var plan = launcher.Prepare("agent", Entry(argument), _dir);

        var decision = h.Prompt().ConfirmAsync(plan);
        await h.Answer(AcpTrustPrompt.CancelLabel);
        await decision;

        Assert.Contains("    \"" + argument + "\"", h.Lines);
        Assert.Contains("    \"" + plan.Resolved.ExecutablePath + "\"", h.Lines);
        Assert.Contains("    \"" + _dir + "\"", h.Lines); // the folder has spaces, '[', ']' and '@'
    }

    [Fact]
    public void ThePlanText_HasNoMarkupEscaping_ThatCouldAltertheDisplayedPath()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var plan = launcher.Prepare("agent", Entry("[bold]x[/]", @"C:\a b\c.d"), _dir);

        var text = string.Join("\n", plan.Describe());

        Assert.Contains("\"[bold]x[/]\"", text);
        Assert.DoesNotContain("[[", text);
        Assert.Contains("\"C:\\a b\\c.d\"", text);
    }
}

[Collection("AcpProcesses")]
public class AcpProbeFailureTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "mandocode-acp-probe-" + Guid.NewGuid().ToString("N"));
    private readonly string _host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                    ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;
    private static readonly string MockDll = Path.Combine(AppContext.BaseDirectory, "MandoCode.Acp.MockAgent.dll");

    public AcpProbeFailureTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ATimeout_ShowsTheStderrTail_TheHint_AndTheLogPath_AndKillsTheWholeTree()
    {
        var agents = new Dictionary<string, AcpAgentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["silent"] = new() { Command = _host, Args = [MockDll, "silent-with-child"], StartupTimeoutSeconds = 2 },
        };
        var output = new StringWriter();
        var error = new StringWriter();
        var logs = Path.Combine(_work, "logs");

        var exit = await AcpConnectionProbe.RunAsync("silent", agents, _work, new AcpAgentLauncher(new InMemoryTrustStore()),
            _ => Task.FromResult(true), output, error, logDirectory: logs);

        var text = error.ToString();
        Assert.Equal(AcpConnectionProbe.ExitFailed, exit);
        Assert.Contains("did not complete 'initialize' within 2 seconds", text);
        Assert.Contains(AcpConnectionProbe.NotAcpHint, text);
        Assert.Contains("booting, loading models", text);
        Assert.Contains("Full agent stderr: ", text);
        Assert.Contains("booting, loading models", File.ReadAllText(Directory.GetFiles(logs).Single()));

        // The process tree is gone: the child the agent started is not left running.
        var childPid = int.Parse(text.Split("child-pid: ")[1].Split('\n')[0].Trim());
        Assert.True(await IsGone(childPid), $"child process {childPid} is still running");
    }

    [Fact]
    public async Task TheStderrTail_IsCapped_AtTwentyLines_WhileTheLogKeepsEverything()
    {
        var agents = new Dictionary<string, AcpAgentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["flood"] = new() { Command = _host, Args = [MockDll, "stderr-flood"], StartupTimeoutSeconds = 1 },
        };
        var error = new StringWriter();
        var logs = Path.Combine(_work, "logs");

        // The flood scenario answers, so use an agent that dies instead to force the failure path with lots of stderr.
        agents["flood"] = new() { Command = _host, Args = [MockDll, "exit-immediately"] };
        await AcpConnectionProbe.RunAsync("flood", agents, _work, new AcpAgentLauncher(new InMemoryTrustStore()),
            _ => Task.FromResult(true), new StringWriter(), error, logDirectory: logs);

        var shown = error.ToString().Split("Agent stderr (last lines):")[1].Split("Full agent stderr:")[0]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(shown.Length <= 20, $"{shown.Length} lines shown");
    }

    private static async Task<bool> IsGone(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
