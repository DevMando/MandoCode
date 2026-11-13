using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
[Trait("Category", "Component")]
public class WorkspaceRenderingPerformanceTests
{
    [Fact]
    public async Task WorkspaceSwitchPublishesOneCompletedTransition()
    {
        await using var services = Acp.AcpTestHost.Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active;
        first.Add();
        var second = registry.Add();
        var version = first.FocusVersion;
        var notifications = 0;
        registry.Changed += () =>
        {
            notifications++;
            Assert.Same(first, registry.Active);
            Assert.True(first.IsVisible);
            Assert.False(second.IsVisible);
            Assert.Equal(version + 1, first.FocusVersion);
        };
        registry.Switch(first);
        Assert.Equal(1, notifications);
        Assert.True(first.SelectedPane!.Active);
    }

    [Fact]
    public async Task ClosingAnAlreadyClosedPickerDoesNotRefocusOrRedraw()
    {
        await using var services = Acp.AcpTestHost.Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        registry.Active.Add();
        var version = registry.Active.FocusVersion;
        var notifications = 0;
        registry.Changed += () => notifications++;
        registry.ClosePicker();
        Assert.Equal(version, registry.Active.FocusVersion);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task SelectingAnAgentTabHasOneFocusTransition()
    {
        await using var services = Acp.AcpTestHost.Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        registry.Active.Add();
        var navigation = new AgentNavigationBar();
        typeof(AgentNavigationBar).GetProperty(nameof(AgentNavigationBar.Workspace))!.SetValue(navigation, registry.Active);
        typeof(AgentNavigationBar).GetProperty("Registry", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(navigation, registry);
        var version = registry.Active.FocusVersion;
        var notifications = 0;
        registry.Changed += () => notifications++;
        typeof(AgentNavigationBar).GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(navigation, [first]);
        Assert.Same(first, registry.Active.SelectedPane);
        Assert.Equal(version + 1, registry.Active.FocusVersion);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task IdlePollingIsQuietWhileFocusAndStatusChangesStillRender()
    {
        await using var services = Acp.AcpTestHost.Services();
        var registry = services.GetRequiredService<WorkspaceRegistry>();
        var first = registry.Active.Add();
        registry.Active.Add();
        var awaiting = false;
        first.IsAwaitingInput = () => awaiting;
        var renders = 0;
        RenderFragment<AgentPane> body = pane => builder =>
        {
            builder.OpenComponent<CountBody>(0);
            builder.AddAttribute(1, "Count", (Action)(() => renders++));
            builder.CloseComponent();
        };
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var before = 0;
        TerminalWorkspaceView view = null!;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<RootHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["AgentBody"] = body,
                ["Capture"] = (Action<TerminalWorkspaceView>)(value => view = value)
            }));
            before = renders;
        });
        // Observe more than two actual 300ms status-poll intervals, without model/network activity.
        await Task.Delay(750);
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Equal(before, renders);
            registry.Active.Focus(first);
            Assert.Equal(before + 2, renders); // Exactly one repaint per agent, through the root.
            var focused = renders;
            awaiting = true;
            var poll = typeof(TerminalWorkspaceView).GetMethod("PollStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            poll.Invoke(view, null);
            Assert.Equal(focused + 2, renders);
            poll.Invoke(view, null);
            Assert.Equal(focused + 2, renders); // The same status must not repaint again.
        });
    }

    public sealed class RootHost : ComponentBase
    {
        [Parameter] public RenderFragment<AgentPane> AgentBody { get; set; } = default!;
        [Parameter] public Action<TerminalWorkspaceView> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TerminalWorkspaceView>(0);
            builder.AddAttribute(1, "AgentBody", AgentBody);
            builder.AddComponentReferenceCapture(2, value => Capture((TerminalWorkspaceView)value));
            builder.CloseComponent();
        }
    }

    public sealed class CountBody : ComponentBase
    {
        [Parameter] public Action Count { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Count();
            builder.OpenElement(0, "div");
            builder.AddContent(1, "Agent content");
            builder.CloseElement();
        }
    }
}
