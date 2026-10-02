using System.Reflection;
using HtmlAgilityPack;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
public class WorkspaceRegistryTests
{
    [Fact]
    public async Task CallsignsAndNumbering_AreUniqueAndPersistThePreference()
    {
        Assert.True(new MandoCodeConfig().UseAgentNames);
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        var config = first.Services.GetRequiredService<MandoCodeConfig>();
        Assert.True(ConfigKeySetter.TrySet(config, "agentNaming", "names").Ok);
        var named = registry.Active.Add();
        Assert.Contains(named.Name, AgentCallsigns.Pool);
        var other = registry.Add("Other").SelectedPane!;
        Assert.Contains(other.Name, AgentCallsigns.Pool);
        Assert.NotEqual(named.Name, other.Name);
        var otherConfig = other.Services.GetRequiredService<MandoCodeConfig>();
        Assert.Equal(other.Name, otherConfig.AgentName);
        Assert.True(ConfigKeySetter.TrySet(otherConfig, "agentNaming", "numbers").Ok);
        Assert.Equal("Agent 2", registry.Active.Add().Name);
        Assert.Contains(other.Name, AgentCallsigns.Pool);
        Assert.False(ConfigKeySetter.TrySet(otherConfig, "agentNaming", "invalid").Ok);
        var saved = System.Text.Json.JsonSerializer.Serialize(otherConfig);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MandoCodeConfig>(saved)!.UseAgentNames);
        Assert.DoesNotContain("\"AgentName\"", saved);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddRazorConsoleServices();
        services.AddSingleton<ITerminalViewport>(new Viewport());
        services.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(services, new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false }, Path.GetTempPath());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task EightAgents_HaveIndependentScopesAndFourPerWorkspace()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active;
        for (var i = 0; i < 4; i++) main.Add();
        main.SelectedPane!.Services.GetRequiredService<MandoCodeConfig>().ModelName = "inherited-model";
        var tests = registry.Add("Tests");
        for (var i = 1; i < 4; i++) tests.Add();
        Assert.Equal("inherited-model", tests.Panes[0].Model);
        var extra = tests.Add();
        Assert.Equal(4, tests.Panes.Count);
        Assert.Same(tests.SelectedPane, extra);
        var panes = registry.Workspaces.SelectMany(w => w.Panes).ToArray();
        Assert.Equal(8, panes.Length);
        Assert.Equal(8, panes.Select(p => p.Id).Distinct().Count());
        Assert.Equal(8, panes.Select(p => p.Services.GetRequiredService<AIService>()).Distinct().Count());
        Assert.Null(panes[0].Services.GetRequiredService<AgentIdentity>().CheckpointId);
        Assert.All(panes.Skip(1), p => Assert.NotNull(p.Services.GetRequiredService<AgentIdentity>().CheckpointId));
        Assert.DoesNotContain(main.Panes, p => p.Active);
        Assert.Single(tests.Panes, p => p.Active);
        tests.Panes[0].Services.GetRequiredService<MandoCodeConfig>().ModelName = "other-model";
        Assert.Equal("inherited-model", main.SelectedPane.Model);
        registry.Switch(main);
        Assert.True(main.SelectedPane.Active);
        Assert.DoesNotContain(tests.Panes, p => p.Active);
    }

    [Fact]
    public async Task Switching_PreservesPendingApprovalAndRoutesBackgroundOutput()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        var approval = first.Services.GetRequiredService<ApprovalSelectCoordinator>();
        var pending = approval.RequestAsync([new("Approve", Color.Green)]);
        var second = registry.Add("Tests").SelectedPane!;
        using (TuiConsole.Enter(first.Session)) TuiConsole.Write("background main output");
        using (TuiConsole.Enter(second.Session)) TuiConsole.Write("tests output");
        Assert.Single(first.Session.Snapshot().Entries);
        Assert.Single(second.Session.Snapshot().Entries);
        Assert.False(pending.IsCompleted);
        Assert.True(approval.IsActive);
        registry.Switch(first.Workspace);
        Assert.False(pending.IsCompleted);
        approval.Submit("Approve");
        Assert.Equal("Approve", await pending);
    }

    [Fact]
    public async Task CommandsAndShortcuts_RenameSwitchAndProtectWorkspaceClosure()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        Assert.True(first.Workspace.Command(first, "/workspace-new Tests"));
        var second = registry.Active.SelectedPane!;
        Assert.True(second.Workspace.Command(second, "/workspace-rename Unit tests"));
        Assert.Equal("Unit tests", registry.Active.Name);
        second.IsBusy = () => true;
        registry.Close();
        Assert.Equal(2, registry.Workspaces.Count());
        second.IsBusy = () => false;
        Assert.True(second.Workspace.Key(second, new KeyboardEventArgs { Key = "ArrowLeft", AltKey = true, ShiftKey = true }));
        Assert.Same(first.Workspace, registry.Active);
        first.Workspace.Command(first, "/workspace Unit tests");
        Assert.Same(second.Workspace, registry.Active);
        registry.Close();
        Assert.Single(registry.Workspaces);
        Assert.True(second.Workspace.IsClosed);
        Assert.Equal(2, registry.Entries.Count); // Stable positions, no sibling permutations.
        registry.Close();
        Assert.Single(registry.Workspaces);
        first.Workspace.Key(first, new KeyboardEventArgs { Key = "N", AltKey = true, ShiftKey = true });
        Assert.Equal(2, registry.Workspaces.Count());
        Assert.Equal(3, registry.Active.Id);
    }

    [Fact]
    public async Task LiveRenderer_SwitchingAndClosingWorkspace_PreservesMountedAgents()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active;
        for (var i = 0; i < 4; i++) main.Add();
        var tests = registry.Add("Tests");
        for (var i = 1; i < 4; i++) tests.Add();
        var rendererType = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")!;
        var instance = ActivatorUtilities.CreateInstance(services, rendererType,
            new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        await using var renderer = (IAsyncDisposable)instance;
        RenderFragment<AgentPane> body = pane => builder =>
        {
            builder.OpenComponent<AgentWorkspaceTests.MountedTestApp>(0);
            builder.CloseComponent();
        };
        var mount = rendererType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "MountComponentAsync" && m.IsGenericMethodDefinition);
        await (Task)mount.MakeGenericMethod(typeof(TerminalWorkspaceView)).Invoke(instance,
            new object[] { ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }), CancellationToken.None })!;
        var dispatcher = (Dispatcher)rendererType.GetProperty("Dispatcher")!.GetValue(instance)!;
        VNode Snapshot()
        {
            var snapshot = rendererType.GetMethod("RefreshSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null)!;
            return (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
        }
        var root = Snapshot();
        Assert.Equal(8, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        using (TuiConsole.Enter(main.Panes[0].Session)) TuiConsole.Write("BACKGROUND_MAIN_MESSAGE");
        using (TuiConsole.Enter(tests.Panes[0].Session)) TuiConsole.Write("VISIBLE_TESTS_MESSAGE");
        await Task.Delay(150); // ChatShell refreshes its snapshot on an 80ms timer.
        root = Snapshot();
        Assert.Contains("VISIBLE_TESTS_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("BACKGROUND_MAIN_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("Agent 1", Paint(services, root));
        Assert.Contains("Agent 5", Paint(services, root));
        var hidden = Flatten(root).Single(n => n.Attributes.GetValueOrDefault("data-workspace-hidden") == "true");
        Assert.DoesNotContain(Flatten(hidden), n => n.Attributes.GetValueOrDefault("data-focusable") == "true" || n.Events.Count > 0);
        await dispatcher.InvokeAsync(() => registry.Switch(main));
        root = Snapshot();
        Assert.Equal(8, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains("Agent 1", Paint(services, root));
        Assert.DoesNotContain("Agent 5", Paint(services, root));
        Assert.Contains("BACKGROUND_MAIN_MESSAGE", Paint(services, root));
        Assert.DoesNotContain("VISIBLE_TESTS_MESSAGE", Paint(services, root));
        Assert.All(tests.Panes, p => Assert.NotNull(p.Services.GetRequiredService<AIService>()));
        Assert.All(main.Panes, p => p.IsBusy = () => false);
        await dispatcher.InvokeAsync(() => registry.Close());
        root = Snapshot();
        Assert.Equal(4, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains("Agent 5", Paint(services, root));
        await dispatcher.InvokeAsync(() => registry.Add("Docs"));
        root = Snapshot();
        Assert.Equal(5, Flatten(root).Count(n => n.Attributes.ContainsKey("data-agent-marker")));
        Assert.Contains(registry.Active.SelectedPane!.Name, Paint(services, root));
        Assert.DoesNotContain("Agent 5", Paint(services, root));
    }

    [Fact]
    public async Task WorkspaceTabs_PreservePendingInputAndComposerDraftAcrossSwitch()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder =>
            {
                builder.OpenComponent<DraftHost>(0);
                builder.AddAttribute(1, "Pane", pane);
                builder.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            var prompt = (PromptInput)first.PresentationState["test-prompt"];
            prompt.SetValue("unfinished workspace draft");
            var approval = first.Services.GetRequiredService<ApprovalSelectCoordinator>();
            var pending = approval.RequestAsync([new("Approve", Color.Green)]);
            registry.Add("Tests");
            await Task.Yield();
            Assert.Contains("Main", root.ToHtmlString());
            registry.Switch(first.Workspace);
            await Task.Yield();
            Assert.Contains("unfinished workspace draft", root.ToHtmlString());
            Assert.False(pending.IsCompleted);
            approval.Submit("Approve");
        });
    }

    public sealed class DraftHost : ComponentBase
    {
        [Parameter] public AgentPane Pane { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (!Pane.Visible) return;
            builder.OpenComponent<PromptInput>(0);
            builder.AddComponentReferenceCapture(1, prompt => Pane.PresentationState["test-prompt"] = prompt);
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task PlanEditor_PreservesUnsubmittedTextAcrossWorkspaceSwitch()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder =>
            {
                builder.OpenComponent<EditorHost>(0);
                builder.AddAttribute(1, "Pane", pane);
                builder.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            var editor = (PlanStepEditor)first.PresentationState["test-editor"];
            var buffer = (RazorConsole.Core.Input.TextSelectionState)typeof(PlanStepEditor).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
            buffer.SetText("Unsubmitted revised plan instructions");
            registry.Add("Tests");
            await Task.Yield();
            registry.Switch(first.Workspace);
            await Task.Yield();
            Assert.Contains("Unsubmitted revised plan instructions", root.ToHtmlString());
            Assert.NotSame(editor, first.PresentationState["test-editor"]);
        });
    }

    public sealed class EditorHost : ComponentBase
    {
        [Parameter] public AgentPane Pane { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (!Pane.Visible) return;
            builder.OpenComponent<PlanStepEditor>(0);
            builder.AddAttribute(1, "InitialValue", "Original plan instructions");
            builder.AddAttribute(2, "PersistenceKey", "pending-plan-edit");
            builder.AddComponentReferenceCapture(3, editor => Pane.PresentationState["test-editor"] = editor);
            builder.CloseComponent();
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    [InlineData(45)]
    public async Task NineWorkspaceTabs_KeepActionsVisibleAndRevealSelection(int width)
    {
        await using var services = Services();
        ((Viewport)services.GetRequiredService<ITerminalViewport>()).Width = width;
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        for (var i = 2; i <= 9; i++) registry.Add($"Workspace {i} with an extremely long name");
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment<AgentPane> body = pane => builder => { builder.AddContent(0, $"Body {pane.Id}"); };
            var root = await renderer.RenderComponentAsync<TerminalWorkspaceView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["AgentBody"] = body }));
            string Header()
            {
                var document = new HtmlDocument();
                document.LoadHtml(root.ToHtmlString());
                var vnode = VNode.CreateRegion();
                foreach (var child in document.DocumentNode.ChildNodes)
                    if (ConvertNode(child) is { } converted) vnode.AddChild(converted);
                return Paint(services, vnode).Split('\n')[0].TrimEnd('\r');
            }
            var header = Header();
            Assert.Contains("Workspace 9", header);
            Assert.Contains("…", header);
            Assert.DoesNotContain("[white]", header);
            Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(header) <= width, header);
            if (width >= 70)
            {
                Assert.Contains(" All ", header);
                Assert.Contains(" + New ", header);
                Assert.Contains(" × Close ", header);
                Assert.True(header.IndexOf("× Close", StringComparison.Ordinal) >= width - 9, header);
            }
            else
            {
                Assert.Contains(" ≡ ", header);
                Assert.Contains(" + ", header);
                Assert.Contains(" × ", header);
            }
            registry.Switch(registry.Workspaces.First());
            await Task.Yield();
            Assert.Contains("Main", Header());
            Assert.Equal(9, registry.Workspaces.Count());
        });
    }

    [Fact]
    public async Task TabWindow_ScrollsWithoutSwitchingAgents_AndFollowsNewSelection()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        for (var i = 2; i <= 9; i++) registry.Add($"Workspace {i}");
        var items = registry.Workspaces.ToArray();
        var window = new WorkspaceTabWindow();
        window.Update(items, registry.Active, 66);
        Assert.True(window.Includes(8));
        window.Scroll(-1, items.Length);
        window.Update(items, registry.Active, 66);
        Assert.False(window.Includes(8));
        Assert.Same(items[8], registry.Active);
        registry.Switch(items[0]);
        window.Update(items, registry.Active, 66);
        Assert.True(window.Includes(0));
        window.Update(items, registry.Active, 10);
        Assert.True(window.Includes(0));
        Assert.Equal(1, window.Capacity);
        Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(WorkspaceTabWindow.Fit("模型 workspace 🎨", 7)) <= 7);
    }

    private static VNode? ConvertNode(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text) return VNode.CreateText(System.Net.WebUtility.HtmlDecode(node.InnerText));
        if (node.NodeType != HtmlNodeType.Element) return null;
        var converted = VNode.CreateElement(node.Name);
        foreach (var attribute in node.Attributes) converted.SetAttribute(attribute.Name, System.Net.WebUtility.HtmlDecode(attribute.Value));
        foreach (var child in node.ChildNodes) if (ConvertNode(child) is { } nested) converted.AddChild(nested);
        return converted;
    }

    [Fact]
    public async Task AllWorkspacePicker_FiltersSwitchesAndCancelsWithoutChangingWorkspace()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        var docs = registry.Add("Docs");
        var tests = registry.Add("Tests");
        registry.IsWorkspacePickerOpen = true;
        Assert.DoesNotContain(tests.Panes, p => p.Active);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            WorkspacePicker picker = default!;
            var closed = 0;
            RenderFragment content = builder =>
            {
                builder.OpenComponent<WorkspacePicker>(0);
                builder.AddAttribute(1, "OnClose", EventCallback.Factory.Create(this, () => { closed++; registry.IsWorkspacePickerOpen = false; registry.Active.Focus(registry.Active.SelectedPane!); }));
                builder.AddComponentReferenceCapture(2, instance => picker = (WorkspacePicker)instance);
                builder.CloseComponent();
            };
            await renderer.RenderComponentAsync<PickerHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = content }));
            var query = (RazorConsole.Core.Input.TextSelectionState)typeof(WorkspacePicker).GetField("_query", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)!;
            query.SetText("Docs");
            typeof(WorkspacePicker).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(picker, null);
            var key = typeof(WorkspacePicker).GetMethod("Key", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task<bool>)key.Invoke(picker, new object[] { new KeyboardEventArgs { Key = "Enter" } })!;
            Assert.Same(docs, registry.Active);
            Assert.True(docs.SelectedPane!.Active);
            Assert.False(registry.IsWorkspacePickerOpen);
            Assert.Equal(1, closed);
            registry.IsWorkspacePickerOpen = true;
            await (Task<bool>)key.Invoke(picker, new object[] { new KeyboardEventArgs { Key = "Escape" } })!;
            Assert.Same(docs, registry.Active);
            Assert.Equal(2, closed);
        });
    }
    public sealed class PickerHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }

    [Fact]
    public async Task WorkspaceAll_CommandAndToggle_RestoreAgentFocusAndRespectBusyClosure()
    {
        await using var services = Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var main = registry.Active.Add();
        Assert.True(main.Workspace.Command(main, "/workspace-all"));
        Assert.True(registry.IsWorkspacePickerOpen);
        Assert.False(main.Active);
        registry.TogglePicker();
        Assert.False(registry.IsWorkspacePickerOpen);
        Assert.True(main.Active);
        registry.TogglePicker();
        Assert.True(registry.IsWorkspacePickerOpen);
        Assert.True(main.Workspace.Key(main, new KeyboardEventArgs { Key = "A", AltKey = true, ShiftKey = true }));
        Assert.False(registry.IsWorkspacePickerOpen);
        var other = registry.Add("Tests").SelectedPane!;
        other.IsBusy = () => true;
        Assert.True(other.Workspace.Key(other, new KeyboardEventArgs { Key = "W", AltKey = true, ShiftKey = true }));
        Assert.Equal(2, registry.Workspaces.Count());
        other.IsBusy = () => false;
        other.Workspace.Key(other, new KeyboardEventArgs { Key = "W", AltKey = true, ShiftKey = true });
        Assert.Single(registry.Workspaces);
        Assert.Same(main.Workspace, registry.Active);
    }

    [Fact]
    public async Task CommandSuggestions_KeepKeyboardShortcutVisibleInNarrowPane()
    {
        await using var services = Services();
        ((Viewport)services.GetRequiredService<ITerminalViewport>()).Width = 45;
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<CommandSuggestionRow>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Command"] = "/workspace-new", ["Description"] = SlashCommands.All["/workspace-new"]
            }));
            Assert.Contains("Alt+Shift+N", System.Net.WebUtility.HtmlDecode(root.ToHtmlString()));
        });
    }
    private static IEnumerable<VNode> Flatten(VNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    private static string Paint(IServiceProvider services, VNode node)
    {
        var width = services.GetRequiredService<ITerminalViewport>().Width;
        var layout = new LayoutEngine().Layout(services.GetRequiredService<WidgetTranslationContext>().Translate(node), new BoxConstraints(width, width, 30, 30));
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = width;
        console.Write(layout.PaintToRenderable());
        return writer.ToString();
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width { get; set; } = 120;
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
