using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public class MultiAgentExplorerTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task ExplorerOpensAndOwnsFocusInSelectedNarrowPane(int count, bool gitChanges)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        var workspace = services.GetRequiredService<AgentWorkspace>();
        for (var i = 0; i < count; i++) workspace.Add();
        var selected = workspace.SelectedPane!;
        RenderFragment<AgentPane> body = _ => builder => { builder.OpenComponent<TestApp>(0); builder.CloseComponent(); };
        await using var renderer = await WidgetRendererHarness.MountAsync<AgentWorkspaceView>(services,
            new() { ["AgentBody"] = body });
        var dispatcher = renderer.Dispatcher;
        await dispatcher.InvokeAsync(async () => await (gitChanges ? selected.ToggleGitChanges!() : selected.ToggleFileExplorer!()));
        await renderer.WaitUntilAsync(() => WidgetRendererHarness.Flatten(WidgetRendererHarness.Root(renderer.Snapshot()))
            .Any(node => node.Key?.StartsWith(gitChanges ? "agent-changes-" : "agent-files-") == true));
        var snapshot = await dispatcher.InvokeAsync(renderer.Snapshot);
        var root = WidgetRendererHarness.Root(snapshot);
        var explorer = Assert.Single(Flatten(root), node => node.Key?.StartsWith(gitChanges ? "agent-changes-" : "agent-files-") == true);
        Assert.Equal(explorer.Key, selected.FocusKey);
        var focus = services.GetRequiredService<RazorConsole.Core.Focus.FocusManager>();
        typeof(RazorConsole.Core.Focus.FocusManager).GetMethod("UpdateFocusTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(focus, new[] { snapshot });
        // The terminal render loop restores the selected pane's requested focus
        // after registering the new snapshot's targets.
        await focus.FocusAsync(selected.FocusKey!);
        Assert.True(focus.IsFocused(explorer.Key!));
        if (!gitChanges) Assert.True(selected.IsFileExplorerOpen!());
        Assert.All(workspace.Panes.Where(pane => pane != selected), pane => Assert.False(pane.IsFileExplorerOpen!()));
        Assert.False(selected.ExplorerFocus!.PromptFocused);
        if (!gitChanges)
        {
            // Escape must close the explorer even while the prompt owns focus.
            selected.ExplorerFocus.SetPromptFocused(true);
            await dispatcher.InvokeAsync(() => Assert.True(workspace.Key(selected, new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" })));
            await renderer.WaitUntilAsync(() => !selected.IsFileExplorerOpen!());
            Assert.False(selected.IsFileExplorerOpen!());
        }
    }
    [Fact]
    public async Task AltWArchivesOriginalAndSpawnedAgentsIndependently()
    {
        using var folder = new AgentArchiveTests.ArchiveFolder();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { EnableThemeCustomization = false, AllowPersistence = true }, Path.GetTempPath());
        registrations.AddSingleton(folder.Store);
        await using var services = registrations.BuildServiceProvider();
        var workspace = services.GetRequiredService<WorkspaceRegistry>().Active;
        var original = workspace.Add();
        RenderFragment<AgentPane> body = _ => builder => { builder.OpenComponent<ArchiveApp>(0); builder.CloseComponent(); };
        await using var renderer = await WidgetRendererHarness.MountAsync<AgentWorkspaceView>(services,
            new() { ["AgentBody"] = body });
        var dispatcher = renderer.Dispatcher;
        AgentPane spawned = null!;
        await dispatcher.InvokeAsync(() => { spawned = workspace.Add(); workspace.Add(); });
        await renderer.WaitUntilAsync(() => spawned.SaveHistory is not null);
        await dispatcher.InvokeAsync(() => { workspace.Focus(original); workspace.Key(original, new() { Key = "w", AltKey = true }); });
        await renderer.WaitUntilAsync(() => !workspace.Panes.Contains(original));
        await dispatcher.InvokeAsync(() => { workspace.Focus(spawned); workspace.Key(spawned, new() { Key = "w", AltKey = true }); });
        await renderer.WaitUntilAsync(() => !workspace.Panes.Contains(spawned));
        Assert.Single(workspace.Panes);
        Assert.Contains(folder.Store.Closed(), a => a.Key == original.PersistKey);
        Assert.Contains(folder.Store.Closed(), a => a.Key == spawned.PersistKey);
    }
    public sealed class ArchiveApp : App
    {
        protected override void OnInitialized()
        {
            base.OnInitialized();
            typeof(App).GetField("_hasRendered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
            typeof(App).GetField("_isProcessing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, false);
            var messages = (List<ChatMsg>)typeof(App).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
            messages.Add(new() { Role = "user", Text = "hello" });
            messages.Add(new() { Role = "assistant", Text = "hello back" });
        }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }
    private static IEnumerable<VNode> Flatten(VNode node)
    {
        yield return node;
        foreach (var child in node.Children) foreach (var nested in Flatten(child)) yield return nested;
    }
    public sealed class TestApp : App
    {
        protected override void OnInitialized()
        {
            base.OnInitialized();
            typeof(App).GetField("_showPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
            typeof(App).GetField("_isProcessing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, false);
        }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width => 120;
        public int Height => 40;
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
