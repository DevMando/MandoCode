using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MandoCode.Components;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Vdom;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Components;
using System.Reflection;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
public class AgentWorkspaceTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LiveRenderer_CloseFirstAgent_KeepsEverySurvivor(int count)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new FixedViewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var workspace = services.GetRequiredService<AgentWorkspace>();
        for (var i = 0; i < count; i++) workspace.Add();
        var survivors = workspace.Panes.Skip(1).ToArray();
        var rendererType = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")!;
        var instance = ActivatorUtilities.CreateInstance(services, rendererType,
            new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        await using var renderer = (IAsyncDisposable)instance;
        RenderFragment<AgentPane> body = pane => builder =>
        {
            builder.OpenComponent<MountedTestApp>(0);
            builder.CloseComponent();
        };
        var mount = rendererType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "MountComponentAsync" && m.IsGenericMethodDefinition);
        await (Task)mount.MakeGenericMethod(typeof(AgentWorkspaceView)).Invoke(instance,
            new object[] { ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }), CancellationToken.None })!;
        var dispatcher = (Dispatcher)rendererType.GetProperty("Dispatcher")!.GetValue(instance)!;
        await dispatcher.InvokeAsync(() =>
        {
            workspace.Panes[0].IsBusy = () => false;
            workspace.Close(workspace.Panes[0]);
        });
        var refresh = rendererType.GetMethod("RefreshSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var snapshot = refresh.Invoke(instance, null)!;
        var root = (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        var markers = Flatten(root).Where(n => n.Attributes.ContainsKey("data-agent-marker"))
            .Select(n => n.Attributes["data-agent-marker"]).ToArray();
        Assert.Equal(survivors.Select(p => p.Id.ToString()).Order(), markers.Order());
        Assert.Equal(count - 1, workspace.Panes.Count);
        foreach (var pane in survivors)
        {
            Assert.NotNull(pane.Services.GetRequiredService<AIService>());
            Assert.NotNull(pane.Stop);
        }
        await dispatcher.InvokeAsync(() => workspace.Add());
        snapshot = refresh.Invoke(instance, null)!;
        root = (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        markers = Flatten(root).Where(n => n.Attributes.ContainsKey("data-agent-marker"))
            .Select(n => n.Attributes["data-agent-marker"]).ToArray();
        Assert.Equal(count, markers.Length);
        foreach (var pane in survivors) Assert.Contains(pane.Id.ToString(), markers);
    }

    public sealed class MountedTestApp : App
    {
        // Run the real App initialization/disposal, but avoid network startup in this test.
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            base.BuildRenderTree(builder);
            builder.OpenElement(9990, "div");
            builder.AddAttribute(9991, "data-agent-marker", Pane!.Id.ToString());
            builder.AddContent(9992, $"survivor-{Pane.Id}");
            builder.CloseElement();
        }
    }

    private static IEnumerable<VNode> Flatten(VNode node)
    {
        yield return node;
        foreach (var child in node.Children) foreach (var descendant in Flatten(child)) yield return descendant;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AgentComponents_RenderWithScopedServices_AndReleaseAsyncResources(int count)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new FixedViewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        var second = count > 1 ? workspace.Add() : first;
        for (var i = 2; i < count; i++) workspace.Add();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<AgentWorkspaceView>();
            var html = System.Net.WebUtility.HtmlDecode(root.ToHtmlString());
            if (count > 1) Assert.Contains("Agent 1", html);
            if (count > 1) Assert.Contains("Agent 2", html);
            Assert.DoesNotContain("+ New", html);
            Assert.Equal(count > 1 ? 58 : 120, first.Width);
            Assert.Equal(count > 1 ? 58 : 120, second.Width);
            Assert.Equal(count == 4 ? 12 : count == 1 ? 29 : 27, first.Height);
            Assert.Equal(count > 2 ? 12 : count == 1 ? 29 : 27, second.Height);
            Assert.Equal(count, workspace.Panes.Count);
            foreach (var pane in workspace.Panes.Where(_ => count > 1)) Assert.Contains($"Agent {pane.Id}", html);
            var document = new HtmlDocument();
            document.LoadHtml(root.ToHtmlString());
            var vdom = VNode.CreateRegion();
            foreach (var child in document.DocumentNode.ChildNodes)
                if (ToVNode(child) is { } node) vdom.AddChild(node);
            var widgets = services.GetRequiredService<WidgetTranslationContext>();
            var layout = new LayoutEngine().Layout(widgets.Translate(vdom), new BoxConstraints(120, 120, 30, 30));
            var writer = new StringWriter();
            var console = Spectre.Console.AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
            console.Profile.Width = 120;
            console.Write(layout.PaintToRenderable());
            var lines = writer.ToString().Split('\n');
            var top = Array.FindIndex(lines, line => line.Contains("Agent 1"));
            if (count > 1) Assert.True(top >= 0);
            if (count > 1) Assert.Contains("Agent 2", lines[top]);
            if (count > 2)
            {
                var bottom = Array.FindIndex(lines, line => line.Contains("Agent 3"));
                Assert.True(bottom > top, writer.ToString());
                if (count == 4) Assert.Contains("Agent 4", lines[bottom]);
                if (count == 3) Assert.DoesNotContain("Agent 1", lines[bottom]);
            }
            if (count == 2)
            {
                var originalConfig = first.Services.GetRequiredService<MandoCodeConfig>();
                first.Session.AppendUserPrompt("preserved conversation");
                workspace.Add();
                var fourth = workspace.Add();
                await Task.Yield();
                Assert.Same(originalConfig, first.Services.GetRequiredService<MandoCodeConfig>());
                Assert.Single(first.Session.Snapshot().Entries);
                Assert.Contains("Agent 4", System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
                fourth.IsBusy = () => false;
                workspace.Close(fourth);
                await Task.Yield();
                var replacement = workspace.Add();
                await Task.Yield();
                Assert.Equal(3, replacement.Slot);
                Assert.Same(originalConfig, first.Services.GetRequiredService<MandoCodeConfig>());
            }
            if (count == 3 || count == 4)
            {
                var survivor = workspace.Panes.Last();
                var survivorConfig = survivor.Services.GetRequiredService<MandoCodeConfig>();
                survivor.Session.AppendUserPrompt("surviving conversation");
                foreach (var removed in workspace.Panes.Where(p => p != second && p != survivor).ToArray())
                {
                    removed.IsBusy = () => false;
                    workspace.Close(removed);
                }
                await Task.Yield();
                Assert.Equal(27, survivor.Height);
                Assert.Same(survivorConfig, survivor.Services.GetRequiredService<MandoCodeConfig>());
                Assert.Single(survivor.Session.Snapshot().Entries);
                document.LoadHtml(root.ToHtmlString());
                // Collapsed layouts must remove empty slots rather than relying on
                // zero-width widgets, which can retain space in live reconciliation.
                Assert.DoesNotContain("data-width=\"0\"", root.ToHtmlString());
                Assert.DoesNotContain("data-height=\"0\"", root.ToHtmlString());
                vdom = VNode.CreateRegion();
                foreach (var child in document.DocumentNode.ChildNodes)
                    if (ToVNode(child) is { } node) vdom.AddChild(node);
                layout = new LayoutEngine().Layout(widgets.Translate(vdom), new BoxConstraints(120, 120, 30, 30));
                writer.GetStringBuilder().Clear();
                console.Write(layout.PaintToRenderable());
                lines = writer.ToString().Split('\n');
                var survivorRow = Array.FindIndex(lines, line => line.Contains($"Agent {survivor.Id}"));
                Assert.True(survivorRow >= 0);
                Assert.Contains("Agent 2", lines[survivorRow]);
                workspace.Focus(second);
                workspace.Move(1);
                Assert.True(survivor.Active);
            }
        });
    }

    private static VNode? ToVNode(HtmlNode html)
    {
        if (html.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(html.InnerText);
            return string.IsNullOrWhiteSpace(text) ? null : VNode.CreateText(text);
        }
        if (html.NodeType != HtmlNodeType.Element) return null;
        var node = VNode.CreateElement(html.Name);
        foreach (var attribute in html.Attributes) node.SetAttribute(attribute.Name, HtmlEntity.DeEntitize(attribute.Value));
        foreach (var child in html.ChildNodes) if (ToVNode(child) is { } nested) node.AddChild(nested);
        return node;
    }

    private sealed class FixedViewport : ITerminalViewport
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
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        Program.RegisterAgentServices(services, new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false }, Path.GetTempPath());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void AgentServicesAndSettings_AreIndependent_AndInheritCurrentModel()
    {
        using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        first.Services.GetRequiredService<MandoCodeConfig>().ModelName = "first-model";
        var second = workspace.Add();
        Assert.Equal("first-model", second.Model);
        second.Services.GetRequiredService<MandoCodeConfig>().ModelName = "second-model";
        Assert.Equal("first-model", first.Model);
        foreach (var type in new[] { typeof(AIService), typeof(TokenTrackingService), typeof(PlanHandoff), typeof(ApprovalSelectCoordinator), typeof(InstructionPromptCoordinator), typeof(InputStateMachine), typeof(SpinnerService), typeof(McpApprovalGate) })
            Assert.NotSame(first.Services.GetRequiredService(type), second.Services.GetRequiredService(type));
        Assert.Null(first.Services.GetRequiredService<AgentIdentity>().CheckpointId);
        Assert.NotNull(second.Services.GetRequiredService<AgentIdentity>().CheckpointId);
        Assert.True(second.Active);
        Assert.False(first.Active);
    }

    [Fact]
    public async Task ConcurrentOutput_StaysInItsOwnPane_IncludingStdoutAndSpinner()
    {
        using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        var second = workspace.Add();
        var fallback = new TuiSession();
        using (TuiConsole.Begin(fallback))
        {
            await Task.WhenAll(Write(first, "alpha"), Write(second, "beta"));
            Assert.Empty(fallback.Snapshot().Entries);
        }
        Assert.Equal(2, first.Session.Snapshot().Entries.Count);
        Assert.Equal(2, second.Session.Snapshot().Entries.Count);
        Assert.Equal("alpha", first.Session.Snapshot().Activity);
        Assert.Equal("beta", second.Session.Snapshot().Activity);
        Assert.DoesNotContain(first.Session.Snapshot().Entries, e => second.Session.Find(e.Id) is not null);
        Assert.Same(first.Session.Snapshot().Entries[0].Content, workspace.Find(first.Session.Snapshot().Entries[0].Id));
        static async Task Write(AgentPane pane, string name)
        {
            using var routing = TuiConsole.Enter(pane.Session);
            await Task.Yield();
            TuiConsole.Write(new Text(name));
            Console.WriteLine(name + " stdout");
            pane.Services.GetRequiredService<SpinnerService>().Start(name);
        }
    }

    [Fact]
    public async Task ApprovalsAndInstructionRequests_DoNotResolveEachOther()
    {
        using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        var second = workspace.Add();
        var firstApproval = first.Services.GetRequiredService<ApprovalSelectCoordinator>();
        var secondApproval = second.Services.GetRequiredService<ApprovalSelectCoordinator>();
        var options = new[] { new ApprovalSelectCoordinator.Option("Approve", Color.Green) };
        var one = firstApproval.RequestAsync(options);
        var two = secondApproval.RequestAsync(options);
        firstApproval.Submit("Approve");
        Assert.Equal("Approve", await one);
        Assert.False(two.IsCompleted);
        Assert.True(secondApproval.IsActive);
        secondApproval.Submit(string.Empty);
        Assert.Equal(string.Empty, await two);
        var firstInstructions = first.Services.GetRequiredService<InstructionPromptCoordinator>();
        var secondInstructions = second.Services.GetRequiredService<InstructionPromptCoordinator>();
        one = firstInstructions.RequestAsync("First", "original one");
        two = secondInstructions.RequestAsync("Second", "original two");
        firstInstructions.Submit("updated one");
        Assert.Equal("updated one", await one);
        Assert.False(two.IsCompleted);
        secondInstructions.Cancel();
        Assert.Equal("original two", await two);
    }

    [Fact]
    public void CommandsAndShortcuts_EnforceLimitAndProtectRunningAgent()
    {
        using var services = Services();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        var first = workspace.Add();
        Assert.True(workspace.Command(first, "/agent-new"));
        var second = workspace.Panes[1];
        workspace.Command(second, "/agent-new");
        workspace.Command(second, "/agent-new");
        workspace.Command(second, "/agent-new");
        Assert.Equal(4, workspace.Panes.Count);
        workspace.Focus(second);
        Assert.True(workspace.Key(second, new KeyboardEventArgs { Key = "ArrowLeft", MetaKey = true }));
        Assert.True(first.Active);
        Assert.True(workspace.Key(first, new KeyboardEventArgs { Key = "ArrowRight", AltKey = true }));
        Assert.True(second.Active);
        Assert.False(workspace.Key(second, new KeyboardEventArgs { Key = "ArrowDown", AltKey = true }));
        Assert.False(workspace.Key(second, new KeyboardEventArgs { Key = "ArrowUp", AltKey = true }));
        Assert.True(second.Active);
        workspace.Key(second, new KeyboardEventArgs { Key = "ArrowRight", AltKey = true });
        Assert.True(workspace.Panes[2].Active);
        workspace.Key(workspace.Panes[2], new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true });
        Assert.True(second.Active);
        workspace.Focus(workspace.Panes[3]);
        workspace.Key(workspace.Panes[3], new KeyboardEventArgs { Key = "ArrowRight", AltKey = true });
        Assert.True(first.Active);
        workspace.Key(first, new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true });
        Assert.True(workspace.Panes[3].Active);
        workspace.Focus(second);
        second.IsBusy = () => true;
        workspace.Command(second, "/agent-close");
        Assert.Equal(4, workspace.Panes.Count);
        var stopped = false;
        second.IsBusy = () => false;
        second.Stop = () => stopped = true;
        workspace.Command(second, "/agent-close");
        Assert.Equal(3, workspace.Panes.Count);
        Assert.True(stopped);
        Assert.True(first.Active);
        second.Dispose();
        foreach (var pane in workspace.Panes.Where(p => p != first).ToArray()) { workspace.Close(pane); pane.Dispose(); }
        workspace.Command(first, "/agent-close");
        Assert.Single(workspace.Panes);
    }
}
