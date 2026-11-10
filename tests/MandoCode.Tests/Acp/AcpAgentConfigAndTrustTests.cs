using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Transport;
using MandoCode.Models;
using MandoCode.Services.Acp;
using Xunit;

namespace MandoCode.Tests.Acp;

public sealed class InMemoryTrustStore : ITrustStore
{
    public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GetHash(string agentName) => Hashes.GetValueOrDefault(agentName);
    public void Set(string agentName, string hash) => Hashes[agentName] = hash;
    public void Remove(string agentName) => Hashes.Remove(agentName);
}

public class AcpAgentConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mandocode-acp-config-" + Guid.NewGuid().ToString("N"));

    public AcpAgentConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Json = """
        {
          "acpAgents": {
            "Claude": { "command": "node", "args": ["C:/a/index.js"], "env": { "K": "V" }, "disabled": true,
                        "passAnthropicApiKey": true, "startupTimeoutSeconds": 45, "authHint": "claude auth login" },
            "claude": { "command": "other" },
            "tiny": { "command": "x", "startupTimeoutSeconds": 0 }
          }
        }
        """;

    private static MandoCodeConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, ConfigJsonOptions.ReadOptions)!;
        config.ValidateAndClamp();
        return config;
    }

    [Fact]
    public void Entries_Deserialize_WithAllFields()
    {
        var config = Parse(Json);

        var claude = config.AcpAgents["CLAUDE"];
        Assert.Equal("node", claude.Command);
        Assert.Equal(["C:/a/index.js"], claude.Args);
        Assert.Equal("V", claude.Env["K"]);
        Assert.True(claude.Disabled);
        Assert.True(claude.PassAnthropicApiKey);
        Assert.Equal(45, claude.StartupTimeoutSeconds);
        Assert.Equal("claude auth login", claude.AuthHint);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var entry = new AcpAgentConfig();

        Assert.False(entry.PassAnthropicApiKey);
        Assert.False(entry.Disabled);
        Assert.Equal(30, entry.StartupTimeoutSeconds);
    }

    [Fact]
    public void Names_AreCaseInsensitive_AndCollisionsKeepTheFirst()
    {
        var config = Parse(Json);

        Assert.Equal(StringComparer.OrdinalIgnoreCase, config.AcpAgents.Comparer);
        Assert.Equal(2, config.AcpAgents.Count);
        Assert.Equal("node", config.AcpAgents["claude"].Command);
    }

    [Fact]
    public void StartupTimeout_IsClamped()
    {
        Assert.Equal(5, Parse(Json).AcpAgents["tiny"].StartupTimeoutSeconds);
    }

    [Fact]
    public void SurvivesASerializeParseRoundTrip()
    {
        var config = Parse(Json);

        var json = JsonSerializer.Serialize(config, ConfigJsonOptions.WriteOptions);

        Assert.Contains("\"acpAgents\"", json);
        Assert.Equal("claude auth login", Parse(json).AcpAgents["claude"].AuthHint);
    }

    [Fact]
    public void AcpAgents_FromAnyOtherConfigPath_AreIgnored_WithAWarning()
    {
        var path = Path.Combine(_dir, "project-config.json");
        File.WriteAllText(path, Json);
        var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);
        MandoCodeConfig config;
        try
        {
            config = MandoCodeConfig.Load(path);
        }
        finally
        {
            Console.SetOut(previous);
        }

        Assert.Empty(config.AcpAgents);
        Assert.Contains("ignoring 'acpAgents'", captured.ToString());
        Assert.Contains(MandoCodeConfig.GetDefaultConfigPath(), captured.ToString());
    }

    [Fact]
    public void OnlyTheDefaultPath_CountsAsTheUserConfig()
    {
        Assert.True(MandoCodeConfig.IsUserConfigPath(MandoCodeConfig.GetDefaultConfigPath()));
        Assert.True(MandoCodeConfig.IsUserConfigPath(
            Path.Combine(Path.GetDirectoryName(MandoCodeConfig.GetDefaultConfigPath())!, ".", "config.json")));
        Assert.False(MandoCodeConfig.IsUserConfigPath(Path.Combine(_dir, "config.json")));
        Assert.False(MandoCodeConfig.IsUserConfigPath(Path.Combine(Environment.CurrentDirectory, ".mandocode", "config.json")));
    }
}

public class AcpTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mandocode-acp-trust-" + Guid.NewGuid().ToString("N"));
    private readonly string _exe = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                                   ?? AcpCommandResolver.Resolve("dotnet", []).ExecutablePath;

    public AcpTrustTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private AcpAgentConfig Entry(params string[] args) => new() { Command = _exe, Args = [.. args] };

    private static ResolvedCommand Resolved(string path, params string[] args) => new(path, args);

    [Fact]
    public void Hash_IsStable_ForTheSameLaunch()
    {
        var a = AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x", "y"), new AcpAgentConfig { Env = { ["K"] = "V", ["A"] = "B" } });
        var b = AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x", "y"), new AcpAgentConfig { Env = { ["A"] = "B", ["K"] = "V" } });

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Hash_ChangesWith_PathArgsEnvKeyEnvValueAndApiKeyFlag()
    {
        var baseline = AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x"), new AcpAgentConfig { Env = { ["K"] = "V" } });

        string[] variants =
        [
            AcpAgentLauncher.ComputeHash(Resolved("/bin/b", "x"), new AcpAgentConfig { Env = { ["K"] = "V" } }),
            AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x", "z"), new AcpAgentConfig { Env = { ["K"] = "V" } }),
            AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x"), new AcpAgentConfig { Env = { ["K2"] = "V" } }),
            AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x"), new AcpAgentConfig { Env = { ["K"] = "V2" } }),
            AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x"), new AcpAgentConfig { Env = { ["K"] = "V" }, PassAnthropicApiKey = true }),
            AcpAgentLauncher.ComputeHash(Resolved("/bin/a", "x"), new AcpAgentConfig()),
        ];

        Assert.All(variants, v => Assert.NotEqual(baseline, v));
        Assert.Equal(variants.Length, variants.Distinct().Count());
    }

    [Fact]
    public void Hash_IgnoresTheWorkingDirectory_AndTheDisabledFlag()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());

        var a = launcher.Prepare("x", Entry(), _dir);
        var b = launcher.Prepare("x", new AcpAgentConfig { Command = _exe, Disabled = true }, Path.GetTempPath());

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void ArgumentBoundaries_AreNotAmbiguous()
    {
        Assert.NotEqual(
            AcpAgentLauncher.ComputeHash(Resolved("/a", "x y"), new AcpAgentConfig()),
            AcpAgentLauncher.ComputeHash(Resolved("/a", "x", "y"), new AcpAgentConfig()));
    }

    [Fact]
    public async Task FirstLaunch_AsksAndRemembers_ThenDoesNotAskAgain()
    {
        var store = new InMemoryTrustStore();
        var launcher = new AcpAgentLauncher(store);
        var asked = 0;
        Task<bool> Yes(AcpLaunchPlan _) { asked++; return Task.FromResult(true); }

        var plan = launcher.Prepare("agent", Entry("a"), _dir);

        Assert.False(launcher.IsTrusted(plan));
        Assert.True(await launcher.EnsureTrustedAsync(plan, Yes));
        Assert.True(await launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry("a"), _dir), Yes));
        Assert.Equal(1, asked);
        Assert.Equal(plan.Hash, store.GetHash("agent"));
    }

    [Fact]
    public async Task Declining_StartsNothing_AndRemembersNothing()
    {
        var store = new InMemoryTrustStore();
        var launcher = new AcpAgentLauncher(store);

        Assert.False(await launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry(), _dir), _ => Task.FromResult(false)));
        Assert.Empty(store.Hashes);
    }

    [Fact]
    public async Task ChangingTheEntry_ResetsTrust()
    {
        var store = new InMemoryTrustStore();
        var launcher = new AcpAgentLauncher(store);
        await launcher.EnsureTrustedAsync(launcher.Prepare("agent", Entry("a"), _dir), _ => Task.FromResult(true));

        Assert.False(launcher.IsTrusted(launcher.Prepare("agent", Entry("a", "--more"), _dir)));
        Assert.False(launcher.IsTrusted(launcher.Prepare("agent", new AcpAgentConfig { Command = _exe, Args = ["a"], Env = { ["X"] = "1" } }, _dir)));
        Assert.False(launcher.IsTrusted(launcher.Prepare("agent", new AcpAgentConfig { Command = _exe, Args = ["a"], PassAnthropicApiKey = true }, _dir)));
        Assert.True(launcher.IsTrusted(launcher.Prepare("agent", Entry("a"), _dir)));
    }

    [Fact]
    public async Task TrustIsPerAgentName()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        await launcher.EnsureTrustedAsync(launcher.Prepare("one", Entry(), _dir), _ => Task.FromResult(true));

        Assert.False(launcher.IsTrusted(launcher.Prepare("two", Entry(), _dir)));
    }

    [Fact]
    public void Prepare_RefusesUnsafeCommands_BeforeAnyQuestion()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());

        Assert.Throws<AcpLaunchException>(() => launcher.Prepare("a", new AcpAgentConfig { Command = "npx", Args = ["-y", "pkg"] }, _dir));
        Assert.Throws<AcpLaunchException>(() => launcher.Prepare("a", new AcpAgentConfig { Command = "cmd", Args = ["/c", "x"] }, _dir));
        Assert.Throws<AcpLaunchException>(() => launcher.Prepare("a", new AcpAgentConfig { Command = "" }, _dir));
    }

    [Fact]
    public void Description_ShowsPathEveryArgumentFolderAndEnvNamesButNoValues()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var plan = launcher.Prepare("agent",
            new AcpAgentConfig { Command = _exe, Args = ["one", "two words"], Env = { ["SECRET_NAME"] = "secret-value" } }, _dir);

        var text = string.Join("\n", plan.Describe());

        Assert.Contains("\"" + plan.Resolved.ExecutablePath + "\"", text);
        Assert.Contains("argument 1:" + Environment.NewLine + "    \"one\"", string.Join(Environment.NewLine, plan.Describe()));
        Assert.Contains("argument 2:" + Environment.NewLine + "    \"two words\"", string.Join(Environment.NewLine, plan.Describe()));
        Assert.Contains("\"" + _dir + "\"", text);
        Assert.Contains("SECRET_NAME", text);
        Assert.DoesNotContain("secret-value", text);
        Assert.Contains("ANTHROPIC_API_KEY: removed", text);
    }

    [Fact]
    public void Description_SaysSoWhenTheApiKeyIsPassed()
    {
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var plan = launcher.Prepare("agent", new AcpAgentConfig { Command = _exe, PassAnthropicApiKey = true }, _dir);

        Assert.Contains("PASSED", string.Join("\n", plan.Describe()));
    }

    [Fact]
    public void FileTrustStore_PersistsAcrossInstances_AndNeverStoresLaunchDetails()
    {
        var path = Path.Combine(_dir, "sub", "acp-trust.json");
        new FileTrustStore(path).Set("Agent", "abc123");

        var reloaded = new FileTrustStore(path);
        Assert.Equal("abc123", reloaded.GetHash("agent"));
        var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        Assert.Equal(new Dictionary<string, string> { ["Agent"] = "abc123" }, stored); // name -> hash only

        reloaded.Remove("AGENT");
        Assert.Null(new FileTrustStore(path).GetHash("agent"));
    }

    [Fact]
    public void FileTrustStore_TreatsAnUnreadableFileAsNoTrust()
    {
        var path = Path.Combine(_dir, "acp-trust.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(new FileTrustStore(path).GetHash("agent"));
    }
}
