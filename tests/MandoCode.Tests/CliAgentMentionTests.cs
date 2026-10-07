using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using System.Reflection;
using MandoCode.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
public sealed class CliAgentMentionTests
{
    private static ServiceProvider Services()
    {
        var registrations = new ServiceCollection().AddLogging();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        return registrations.BuildServiceProvider();
    }

    [Fact]
    public async Task PickerFiltersAcrossWorkspaces_PreservesSuffix_AndDefaultsBackToFiles()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var self = registry.Active.Add();
        var other = registry.Add("Other").Panes[0];
        other.Name = "Agent Two";
        var composer = new PromptComposerState(new InputStateMachine(new Dictionary<string, string>(), null)) { Agents = new(self) };
        composer.SetText("ask @Two about it");
        composer.Buffer.Begin("ask @Two".Length, 1, false); composer.Buffer.End(); composer.Refresh();
        Assert.True(composer.HandleKey("Tab"));
        Assert.True(composer.ViewingAgents);
        Assert.Equal("ask @Two about it", composer.Buffer.Text);
        Assert.True(composer.HandleKey("Tab"));
        Assert.False(composer.ViewingAgents);
        Assert.True(composer.HandleKey("Tab"));
        Assert.Same(other, Assert.Single(composer.AgentSuggestions));
        Assert.True(composer.HandleKey("Enter"));
        Assert.Equal("ask @\"Agent Two\"  about it", composer.Buffer.Text);
        Assert.False(composer.ViewingAgents);
        composer.SetText("@");
        Assert.True(composer.AtPickerOpen);
        Assert.False(composer.ViewingAgents);
        Assert.True(composer.ToggleAgentView());
        Assert.True(composer.HandleKey("Escape"));
        Assert.False(composer.AtPickerOpen);
        Assert.Equal("@", composer.Buffer.Text);
    }

    [Fact]
    public async Task MentionsResolveNames_WithoutTreatingExplicitPathsAsAgents()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add();
        var one = workspace.Add(); one.Name = "Ares";
        var two = workspace.Add(); two.Name = "Artemis";
        var directory = new CliAgentDirectory(self);
        Assert.Null(directory.Resolve("Ar"));
        Assert.Same(one, directory.Resolve("ares"));
        Assert.Same(two, directory.Resolve("Arte"));
        Assert.Contains("Mentioned open agents", directory.ExpandMentions("ask @Ares"));
        Assert.Equal("read @./Ares", directory.ExpandMentions("read @./Ares"));
        Assert.Equal("read @folder/Ares", directory.ExpandMentions("read @folder/Ares"));
        var composer = new PromptComposerState(new InputStateMachine(new Dictionary<string, string>(), null)) { Agents = directory };
        composer.InsertFileReference("Ares");
        Assert.Equal("@./Ares ", composer.Buffer.Text);
    }

    [Fact]
    public async Task ToolsReadWithoutRunningTurn_AndRejectSelfBusyAndLoops()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        peer.ReadConversation = () => Task.FromResult<IReadOnlyList<ChatMsg>>([new() { Role = "assistant", Text = "My findings" }]);
        var tools = new CliAgentTools(self, new());
        Assert.Contains("My findings", await tools.ReadAgentTranscript(peer.Name));
        Assert.Contains("you", await tools.AskAgent(self.Name, "question"));
        peer.AskPeer = request => { request.Accepted.TrySetResult(false); return Task.FromResult(new CliPeerAnswer(false, "busy")); };
        Assert.Contains("busy", await tools.AskAgent(peer.Name, "question"));
        Assert.Contains("busy", await tools.DelegateToAgent(peer.Name, "task"));
        Assert.Contains("Declined", tools.CheckDelegations());
        using (CliAgentCallChain.Enter(peer.PersistKey))
            Assert.Contains("loop", await tools.AskAgent(peer.Name, "question"));
        Assert.Empty(CliAgentCallChain.Capture);
    }

    [Fact]
    public async Task DelegationReturnsWhilePeerWorks_AndQueuesResultOnce()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        var completion = new TaskCompletionSource<CliPeerAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AskPeer = request => { request.Accepted.TrySetResult(true); return completion.Task; };
        var jobs = new CliDelegations(); var tools = new CliAgentTools(self, jobs);
        Assert.Contains("handed", await tools.DelegateToAgent(peer.Name, "Research this"));
        Assert.Contains("Working", tools.CheckDelegations());
        completion.SetResult(new(true, "Research done"));
        await tools.WaitForAgentJob(jobs.For(self.PersistKey)[0].Id);
        Assert.Contains("Completed", tools.CheckDelegations());
        Assert.Contains("Research done", jobs.WithInbox(self.PersistKey, "next"));
        Assert.Equal("next", jobs.WithInbox(self.PersistKey, "next"));
    }

    [Fact]
    public void ChainLimitsAndRestoresNestedCalls()
    {
        using (CliAgentCallChain.Enter("a"))
        {
            using (CliAgentCallChain.Enter("b"))
            {
                Assert.True(CliAgentCallChain.Reject("a"));
                Assert.False(CliAgentCallChain.Reject("c"));
                using (CliAgentCallChain.Enter("c")) Assert.True(CliAgentCallChain.Reject("d"));
            }
            Assert.Equal(new[] { "a" }, CliAgentCallChain.Capture);
        }
        Assert.Empty(CliAgentCallChain.Capture);
    }

    [Fact]
    public async Task QuestionAndReplyShareIndependentCollapsedSection()
    {
        await using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var self = workspace.Add(); var peer = workspace.Add();
        peer.AskPeer = request => Task.FromResult(new CliPeerAnswer(true, "Review complete"));
        var tools = new CliAgentTools(self, new());
        self.Session.BeginToolTurn();
        using (self.Session.CaptureToolOutput(true))
            Assert.Contains("Review complete", await tools.AskAgent(peer.Name, "Review my code"));
        self.Session.CompleteToolTurn();
        var entries = self.Session.Snapshot().Entries;
        Assert.Equal(4, entries.Count);
        Assert.False(entries[0].ToolActivity!.Expanded);
        var exchange = entries[1].AgentActivity!;
        Assert.False(exchange.Expanded);
        Assert.Equal("Completed", exchange.Status);
        Assert.All(entries.Skip(2), entry =>
        {
            Assert.Null(entry.ToolOutput);
            Assert.Same(exchange, entry.AgentOutput);
            Assert.True(entry.SpaceAfter);
        });
        Assert.IsType<Spectre.Console.Panel>(entries[2].Content);
        Assert.IsType<Spectre.Console.Panel>(entries[3].Content);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(100)]
    public void CompletionReportRendersMarkdownInNarrowAndWidePanes(int width)
    {
        using var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(writer),
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors
        });
        console.Profile.Width = width;
        console.Write(CliAgentExchangeRenderer.Result("Headrush → Drift · Completed · d1", "**Location:** Tulare\n\n- Sunny\n- No rain", true));
        var rendered = writer.ToString();
        Assert.Contains("Completed", rendered);
        Assert.Contains("Location:", rendered);
        Assert.Contains("Tulare", rendered);
        Assert.Contains("Sunny", rendered);
        Assert.DoesNotContain("**", rendered);
    }

    [Fact]
    public async Task MountedAgentClaimsOnlyOnePeerRequest_AndClosingReleasesCaller()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        var pane = services.GetRequiredService<AgentWorkspace>().Add();
        App? app = null;
        RenderFragment<AgentPane> body = _ => builder =>
        {
            builder.OpenComponent<AgentWorkspaceTests.MountedTestApp>(0);
            builder.AddComponentReferenceCapture(1, instance => app = (App)instance);
            builder.CloseComponent();
        };
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<AgentWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body })));
        var input = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            typeof(App).GetField("_hasRendered", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, true);
            typeof(App).GetField("_inputTcs", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(app, input);
        });
        var first = new CliPeerRequest("Caller", "First", [], CancellationToken.None);
        var pending = pane.AskPeer!(first);
        Assert.True(await first.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        var second = await pane.AskPeer!(new("Caller", "Second", [], CancellationToken.None));
        Assert.False(second.Answered);
        Assert.Contains("busy", second.Text);
        Assert.Contains("First", await input.Task);
        await renderer.Dispatcher.InvokeAsync(() => pane.Stop!());
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Answered);
        Assert.Null(pane.AskPeer);
    }

    private sealed class Viewport : ITerminalViewport
    {
        public int Width => 120;
        public int Height => 30;
        public event Action? OnResized { add { } remove { } }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
