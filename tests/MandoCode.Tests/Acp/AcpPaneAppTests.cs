using System.Reflection;
using System.Text.Json;
using MandoCode.Acp.Protocol;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>The real <see cref="App"/> component in an ACP pane with a fake agent runtime: /clear, /model, /compact, folder changes, --resume.</summary>
[Collection("TUI console routing")]
[Trait("Category", "Component")]
public class AcpPaneAppTests
{
    private static void SetField(App app, string name, object? value) =>
        typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);

    private static T Field<T>(App app, string name) =>
        (T)typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private sealed class Harness : IAsyncDisposable
    {
        public ServiceProvider Services = default!;
        public AgentPane Pane = default!;
        public App App = default!;
        public FakeRuntime Runtime = new();
        public string Folder = default!;
        private IDisposable _scope = default!;

        public static Harness Create()
        {
            var h = new Harness { Folder = Path.Combine(Path.GetTempPath(), "mandocode-acp-app-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(h.Folder);
            h.Services = AcpTestHost.Services(h.Folder);
            h.Pane = h.Services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "claude");
            h.App = new App { Pane = h.Pane };
            foreach (var name in new[] { "ApprovalMenu", "PromptGate", "KeyCoordinator", "Config", "ProjectRoot", "Shell", "Spinner" })
            {
                var property = typeof(App).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
                property.SetValue(h.App, h.Pane.Services.GetRequiredService(property.PropertyType));
            }

            h.Pane.Services.GetRequiredService<MandoCodeConfig>().AllowPersistence = true;   // --resume needs the remembered-session store
            SetField(h.App, "_acp", h.Runtime);
            h._scope = TuiConsole.Enter(h.Pane.Session, new StringWriter());
            return h;
        }

        public Task Run(string input) =>
            (Task)typeof(App).GetMethod("ExecuteSlashCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(App, [input])!;

        public Task Shell(string line) =>
            (Task)typeof(App).GetMethod("RunShellLineAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(App, [line])!;

        public string Output => string.Join("\n", Pane.Session.Snapshot().Entries.Select(e => Plain.Of(e.Content)));
        public ApprovalSelectCoordinator Menu => Pane.Services.GetRequiredService<ApprovalSelectCoordinator>();
        public ProjectRootAccessor Root => Pane.Services.GetRequiredService<ProjectRootAccessor>();
        public List<ChatMsg> AcpMessages => Field<List<ChatMsg>>(App, "_acpMessages");
        public List<ChatMsg> NativeMessages => Field<List<ChatMsg>>(App, "_messages");

        public async Task AnswerMenuAsync(string answer)
        {
            for (var i = 0; i < 400 && !Menu.IsActive; i++)
                await Task.Delay(10);
            Assert.True(Menu.IsActive, "The question never appeared.");
            Menu.Submit(answer);
        }

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await Services.DisposeAsync();
            try { Directory.Delete(Folder, true); } catch (IOException) { }
        }
    }

    private static SessionConfigOption ModelOption(string current = "model-a") =>
        JsonSerializer.Deserialize($$"""
            {"id":"model","name":"Model","category":"model","type":"select","currentValue":"{{current}}",
             "options":[{"value":"model-a","name":"Model A"},{"value":"model-b","name":"Model B"}]}
            """, AcpJsonContext.Default.SessionConfigOption)!;

    // ─────────── /transcript-save ───────────

    [Fact]
    public async Task AgentModeCommandChangesTheAdvertisedSettingInTheSameSession()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [JsonSerializer.Deserialize("""
            {"id":"mode","name":"Mode","category":"mode","type":"select","currentValue":"agent",
             "options":[{"value":"read-only","name":"Read-only"},{"value":"agent","name":"Auto review"}]}
            """, AcpJsonContext.Default.SessionConfigOption)!];
        await h.Run("/agent-acp mode read-only");
        Assert.Equal(("mode", "read-only"), Assert.Single(h.Runtime.ConfigChanges));
        Assert.Equal(0, h.Runtime.NewConversations);
        Assert.Contains("Agent mode set", h.Output);
    }

    [Fact]
    public async Task AgentModeCommandIsRefusedDuringActiveWork()
    {
        await using var h = Harness.Create();
        SetField(h.App, "_requestCts", new CancellationTokenSource());
        await h.Run("/agent-acp mode read-only");
        Assert.Empty(h.Runtime.ConfigChanges);
        Assert.Contains("current request to finish", h.Output);
    }

    [Fact]
    public async Task TranscriptSave_NamesTheAcpAgent_NotTheOllamaModel()
    {
        await using var h = Harness.Create();
        var ollamaModel = h.Pane.Services.GetRequiredService<MandoCodeConfig>().GetEffectiveModelName();
        var file = Path.Combine(h.Folder, "t.html");
        h.Pane.Session.AppendUserPrompt("hello from the ACP pane");

        await h.Run($"/transcript-save \"{file}\"");

        Assert.True(File.Exists(file), h.Output);
        var html = await File.ReadAllTextAsync(file);
        Assert.Contains("ACP · fake-adapter 1.2.3", html.Replace("&#183;", "·").Replace("&middot;", "·"));
        Assert.DoesNotContain(ollamaModel, html);
    }

    // ─────────── /clear ───────────

    [Fact]
    public async Task Clear_StartsANewSession_AndLeavesTheNativeConversationAlone()
    {
        await using var h = Harness.Create();
        h.Runtime.Support = new AcpSessionSupport(false, false, false, true);
        h.AcpMessages.Add(new ChatMsg { Role = "user", Text = "old acp" });
        h.NativeMessages.Add(new ChatMsg { Role = "user", Text = "native" });

        await h.Run("/clear");

        Assert.Equal(1, h.Runtime.NewConversations);
        Assert.Empty(h.AcpMessages);
        Assert.Equal("native", Assert.Single(h.NativeMessages).Text);
        Assert.Contains("ACP conversation cleared", h.Output);
        Assert.Contains("native conversation of this pane was not touched", h.Output);
    }

    [Fact]
    public async Task Clear_WhileARequestRuns_IsRefusedWithAnExplanation()
    {
        await using var h = Harness.Create();
        SetField(h.App, "_requestCts", new CancellationTokenSource());
        h.AcpMessages.Add(new ChatMsg { Role = "user", Text = "old acp" });

        await h.Run("/clear");

        Assert.Equal(0, h.Runtime.NewConversations);
        Assert.Single(h.AcpMessages);
        Assert.Contains("request is running", h.Output);
    }

    [Fact]
    public async Task Clear_WhenTheAgentIsNotRunning_SaysSo()
    {
        await using var h = Harness.Create();
        h.Runtime.Status = AcpRuntimeStatus.Failed;

        await h.Run("/clear");

        Assert.Equal(0, h.Runtime.NewConversations);
        Assert.Contains("not running", h.Output);
    }

    [Fact]
    public async Task Clear_WhenTheAgentRefusesANewSession_KeepsTheConversation()
    {
        await using var h = Harness.Create();
        h.Runtime.NewConversationFails = new InvalidOperationException("boom");
        h.AcpMessages.Add(new ChatMsg { Role = "user", Text = "old acp" });

        await h.Run("/clear");

        Assert.Single(h.AcpMessages);
        Assert.Contains("boom", h.Output);
    }

    // ─────────── /model and /compact ───────────

    [Fact]
    public async Task Model_WithAValueArgument_SetsTheAgentsModelOption()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [ModelOption()];

        await h.Run("/model model-b");

        Assert.Equal([("model", "model-b")], h.Runtime.ConfigChanges);
        Assert.Contains("Model set to Model B", h.Output);
    }

    [Fact]
    public async Task Model_WithoutAnArgument_AsksWhichOne_AndAppliesTheChoice()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [ModelOption()];

        var run = h.Run("/model");
        await h.AnswerMenuAsync("Model B (model-b)");
        await run;

        Assert.Equal([("model", "model-b")], h.Runtime.ConfigChanges);
    }

    [Fact]
    public async Task Model_MenuStartsOnTheCurrentModel()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [ModelOption(current: "model-b")];

        var run = h.Run("/model");
        for (var i = 0; i < 400 && !h.Menu.IsActive; i++)
            await Task.Delay(10);
        var start = h.Menu.InitialIndex;
        h.Menu.Submit(AcpPaneCommands.CancelLabel);
        await run;

        Assert.Equal(1, start);
    }

    [Fact]
    public async Task Model_CancelledInTheMenu_ChangesNothing()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [ModelOption()];

        var run = h.Run("/model");
        await h.AnswerMenuAsync(AcpPaneCommands.CancelLabel);
        await run;

        Assert.Empty(h.Runtime.ConfigChanges);
    }

    [Fact]
    public async Task Model_WhenTheAgentOffersNoModelOption_SaysSo()
    {
        await using var h = Harness.Create();

        await h.Run("/model");

        Assert.Contains("does not expose a model setting", h.Output);
        Assert.Empty(h.Runtime.ConfigChanges);
    }

    [Fact]
    public async Task Model_WithAnUnknownValue_ListsTheAvailableOnes()
    {
        await using var h = Harness.Create();
        h.Runtime.ConfigOptions = [ModelOption()];

        await h.Run("/model nope");

        Assert.Contains("model-a, model-b", h.Output);
        Assert.Empty(h.Runtime.ConfigChanges);
    }

    [Fact]
    public async Task Compact_WhenTheAgentHasNoCompactCommand_ExplainsAndSendsNothing()
    {
        await using var h = Harness.Create();
        h.Runtime.AvailableCommands = [new AvailableCommand { Name = "review", Description = "x" }];

        await h.Run("/compact");

        Assert.Empty(h.Runtime.Prompts);
        Assert.Contains("does not announce a /compact command", h.Output);
    }

    [Fact]
    public async Task OtherAgentCommands_AreNotForwarded()
    {
        await using var h = Harness.Create();
        h.Runtime.AvailableCommands = [new AvailableCommand { Name = "review", Description = "x" }];

        await h.Run("/review");

        Assert.Empty(h.Runtime.Prompts);
        Assert.Contains("Unknown command", h.Output);
    }

    [Theory]
    [InlineData("/mcp", "/mcp is not available in an ACP agent pane")]
    [InlineData("/skills", "/skills is not available in an ACP agent pane")]
    [InlineData("/history", "/history is not available in an ACP agent pane")]
    [InlineData("/plan fix", "/plan is not available in an ACP agent pane")]
    public async Task BlockedCommands_AnswerWithTheSharedExplanation(string input, string expected)
    {
        await using var h = Harness.Create();

        await h.Run(input);

        Assert.Contains(expected, h.Output);
    }

    // ─────────── folder changes ───────────

    [Fact]
    public async Task ChangeDirectory_Confirmed_ChangesTheFolder_RestartsTheAgent_AndDropsTheAcpConversation()
    {
        await using var h = Harness.Create();
        var target = Path.Combine(h.Folder, "next");
        Directory.CreateDirectory(target);
        h.AcpMessages.Add(new ChatMsg { Role = "user", Text = "old acp" });

        var run = h.Run("/change-directory " + target);
        await h.AnswerMenuAsync("Change folder and restart the agent");
        await run;

        Assert.True(AcpPaths.SameFolder(target, h.Root.ProjectRoot));
        Assert.Empty(h.AcpMessages);
        Assert.True(h.Runtime.Disposed);   // the old agent process ended; a new one starts in the new folder
        Assert.Contains("restarts there with a new conversation", h.Output);
    }

    [Fact]
    public async Task ChangeDirectory_Declined_KeepsTheFolderAndTheAgent()
    {
        await using var h = Harness.Create();
        var target = Path.Combine(h.Folder, "next");
        Directory.CreateDirectory(target);
        h.AcpMessages.Add(new ChatMsg { Role = "user", Text = "old acp" });

        var run = h.Run("/change-directory " + target);
        await h.AnswerMenuAsync("Cancel");
        await run;

        Assert.True(AcpPaths.SameFolder(h.Folder, h.Root.ProjectRoot));
        Assert.Single(h.AcpMessages);
        Assert.False(h.Runtime.Disposed);
    }

    [Fact]
    public async Task ChangeDirectory_ToTheSameFolder_AsksNothing()
    {
        await using var h = Harness.Create();

        await h.Run("/change-directory " + h.Folder);

        Assert.False(h.Menu.IsActive);
        Assert.Contains("already works in that folder", h.Output);
    }

    [Theory]
    [InlineData("cd next")]
    [InlineData("cd  next")]
    public async Task ShellCd_InAnAcpPane_GoesThroughTheSameFolderFlow(string line)
    {
        await using var h = Harness.Create();
        var target = Path.Combine(h.Folder, "next");
        Directory.CreateDirectory(target);

        var run = h.Shell(line);
        await h.AnswerMenuAsync("Change folder and restart the agent");
        await run;

        Assert.True(AcpPaths.SameFolder(target, h.Root.ProjectRoot));
        Assert.True(h.Runtime.Disposed);
    }

    [Theory]
    [InlineData("cd", true)]
    [InlineData("cd ..", true)]
    [InlineData("cd src", true)]
    [InlineData("chdir src", false)]
    [InlineData("pushd src", false)]
    [InlineData("Set-Location src", false)]
    [InlineData("sl src", false)]
    [InlineData("cdx", false)]
    [InlineData("echo cd", false)]
    public void OnlyCd_IsAFolderChangeInTheShellLine(string line, bool isCd) =>
        Assert.Equal(isCd, ShellCommandHandler.TryParseCd(line, out _));

    [Fact]
    public async Task TheTrustHash_DoesNotDependOnTheFolder()
    {
        await using var h = Harness.Create();
        var config = new AcpAgentConfig { Command = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" };
        var launcher = new AcpAgentLauncher(new InMemoryTrustStore());
        var a = Path.Combine(h.Folder, "a");
        var b = Path.Combine(h.Folder, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        Assert.Equal(launcher.Prepare("x", config, a).Hash, launcher.Prepare("x", config, b).Hash);
    }

    // ─────────── --resume ───────────

    private static AcpResumeRequest? Take(Harness h) =>
        (AcpResumeRequest?)typeof(App).GetMethod("TakeResumeRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(h.App, null);

    [Fact]
    public async Task Resume_UsesTheRememberedSessionOfThisFolder_Once()
    {
        await using var h = Harness.Create();
        var store = new AcpSessionStore(Path.Combine(h.Folder, "sessions.json"));
        store.Set("claude", h.Folder, "remembered-1");
        SetField(h.App, "_acpSessionStore", store);
        h.Pane.AcpResume = true;

        Assert.Equal("remembered-1", Take(h)?.SessionId);
        Assert.Null(Take(h));   // a later restart starts fresh
    }

    [Fact]
    public async Task Resume_WithNothingRemembered_StartsNewAndSaysSo()
    {
        await using var h = Harness.Create();
        SetField(h.App, "_acpSessionStore", new AcpSessionStore(Path.Combine(h.Folder, "sessions.json")));
        h.Pane.AcpResume = true;

        Assert.Null(Take(h));
        Assert.Contains("No earlier session", h.Output);
    }

    [Fact]
    public async Task Resume_IgnoresASessionRememberedForAnotherFolder()
    {
        await using var h = Harness.Create();
        var store = new AcpSessionStore(Path.Combine(h.Folder, "sessions.json"));
        store.Set("claude", Path.Combine(h.Folder, "elsewhere"), "other-folder");
        SetField(h.App, "_acpSessionStore", store);
        h.Pane.AcpResume = true;

        Assert.Null(Take(h));
    }

    [Fact]
    public void HistoryCollector_MergesChunksPerSpeaker_AndSkipsTheRest()
    {
        var collector = new AcpHistoryCollector();
        collector.Add(new UserMessageChunk { Content = new TextContent("hel") });
        collector.Add(new UserMessageChunk { Content = new TextContent("lo") });
        collector.Add(new AgentMessageChunk { Content = new TextContent("hi ") });
        collector.Add(new AgentThoughtChunk { Content = new TextContent("thinking") });
        collector.Add(new AgentMessageChunk { Content = new TextContent("there") });
        collector.Add(new UserMessageChunk { Content = new TextContent("bye") });

        Assert.Collection(collector.Messages(),
            m => { Assert.Equal("user", m.Role); Assert.Equal("hello", m.Text); },
            m => { Assert.Equal("assistant", m.Role); Assert.Equal("hi there", m.Text); },
            m => { Assert.Equal("user", m.Role); Assert.Equal("bye", m.Text); });
    }
}
