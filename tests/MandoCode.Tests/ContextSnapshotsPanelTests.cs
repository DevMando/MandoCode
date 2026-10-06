using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Xunit;

namespace MandoCode.Tests;

public sealed class ContextSnapshotsPanelTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type PanelType = typeof(ContextSnapshotsPanel);

    [Theory]
    [InlineData(70, 24)]
    [InlineData(110, 40)]
    public async Task TerminalLayoutShowsControlsAndReadableSummary(int width, int height)
    {
        var store = new SnapshotStore(null);
        for (var i = 0; i < 4; i++) store.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-i), "Saved work " + i, "old", "new", "A readable [bold] summary", 4, "project" + i));
        var frame = await TuiLayoutTests.Frame(new TuiSession(), width, height, snapshots: store);
        Assert.Contains("Saved work 0", frame);
        Assert.Contains("Create Snapshot", frame); Assert.Contains("Import", frame); Assert.Contains("Delete", frame); Assert.Contains("Close", frame);
        Assert.True(frame.Contains("A readable [bold] summary"), frame); Assert.DoesNotContain("[[bold]]", frame);
        if (width >= 100) Assert.Contains(frame.Split('\n'), line => line.Contains("Snapshots") && line.Contains("Summary"));
    }

    [Fact]
    public async Task KeyboardSelectsModelsAndDeleteRequiresConfirmation()
    {
        var store = new SnapshotStore(null);
        var item = new ContextSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, "Saved work", "old", "new", "A readable [bold] summary", 4, "project");
        store.Add(item);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices(); registrations.AddSingleton(store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            Task Key(string key) => Invoke(panel, "Key", new KeyboardEventArgs { Key = key });
            Assert.Contains("data-transcript-entry", rendered.ToHtmlString());
            await Key("Tab");

            await Key("ArrowRight"); await Key("ArrowRight"); await Key("Enter");
            Assert.Single(store.Items);
            await Key("Escape"); Assert.Single(store.Items);
            await Key("ArrowRight"); await Key("ArrowRight"); await Key("Enter");
            await Key("ArrowRight"); await Key("Enter"); Assert.Empty(store.Items);
            Set(panel, "_models", new[] { "first", "second", "third" }); Set(panel, "_model", "first");
            await Invoke(panel, "ShowModels"); await Key("ArrowDown"); await Key("Enter");
            Assert.Equal("second", Get(panel, "_model")); Assert.False((bool)Get(panel, "_choosingModel")!);
        });
    }

    [Fact]
    public async Task ImportQueuesSummaryAndClosesWithoutCallingTheAgent()
    {
        var store = new SnapshotStore(null);
        store.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Work", "old", "new", "Keep these decisions", 4, "project"));
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices(); registrations.AddSingleton(store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            var closed = false;
            PanelType.GetProperty("OnClose")!.SetValue(panel, EventCallback.Factory.Create(panel, () => closed = true));
            await Invoke(panel, "Key", new KeyboardEventArgs { Key = "Tab" });
            await Invoke(panel, "Key", new KeyboardEventArgs { Key = "ArrowRight" });
            await Invoke(panel, "Key", new KeyboardEventArgs { Key = "Enter" });
            Assert.True(closed); Assert.Equal(1, panel.Context.QueuedCount);
            Assert.Contains("Keep these decisions", panel.Context.WithImports("Next task"));
        });
    }
    private static Task Invoke(object panel, string method, params object[] args) => (Task)PanelType.GetMethod(method, Flags)!.Invoke(panel, args)!;
    [Fact]
    public async Task ListAndButtonsHaveSeparateFocusAndCloseDoesNotImport()
    {
        var store = new SnapshotStore(null);
        store.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Work", "old", "new", "Summary", 4, "project"));
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices(); registrations.AddSingleton(store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            var closed = false;
            PanelType.GetProperty("OnClose")!.SetValue(panel, EventCallback.Factory.Create(panel, () => closed = true));
            Task Key(string key) => Invoke(panel, "Key", new KeyboardEventArgs { Key = key });
            bool ListFocused() => (bool)PanelType.GetProperty("ListFocused", Flags)!.GetValue(panel)!;
            Assert.True(ListFocused());
            await Key("Tab"); Assert.False(ListFocused());
            await Key("ArrowDown"); Assert.Equal(1, Get(panel, "_action"));
            await Key("ArrowDown"); await Key("ArrowDown");
            Assert.Equal(3, Get(panel, "_action")); Assert.False(ListFocused());
            await Key("Enter"); Assert.True(closed); Assert.Equal(0, panel.Context.QueuedCount);
            closed = false;
            await Invoke(panel, "Select", 0); Assert.True(ListFocused());
            await Key("Enter"); Assert.True(closed); Assert.Equal(1, panel.Context.QueuedCount);
        });
    }
    private static void Set(object panel, string field, object value) => PanelType.GetField(field, Flags)!.SetValue(panel, value);
    [Fact]
    public async Task ProjectGroupsCollapseAndPreviewSupportsKeyboardAndMouseScrolling()
    {
        var store = new SnapshotStore(null);
        store.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Work", "old", "new", string.Join('\n', Enumerable.Range(0, 100).Select(i => "Summary line " + i)), 4, "project"));
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices(); registrations.AddSingleton(store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            Task Key(string key) => Invoke(panel, "Key", new KeyboardEventArgs { Key = key });
            await Key("ArrowUp"); await Key("Enter");
            Assert.Contains("project", (HashSet<string>)Get(panel, "_collapsed")!);
            await Key("Enter");
            Assert.Empty((HashSet<string>)Get(panel, "_collapsed")!);
            await Key("ArrowDown"); await Key("ArrowRight");
            Assert.True((bool)Get(panel, "_previewFocused")!);
            await Key("ArrowDown"); Assert.Equal(1, Get(panel, "_previewOffset"));
            await Key("PageDown"); Assert.True((int)Get(panel, "_previewOffset")! > 1);
            await Key("Home"); Assert.Equal(0, Get(panel, "_previewOffset"));
            await Invoke(panel, "PreviewWheel", new WheelEventArgs { DeltaY = 120 }); Assert.Equal(3, Get(panel, "_previewOffset"));
            await Key("ArrowLeft"); Assert.False((bool)Get(panel, "_previewFocused")!);
            Assert.Equal(0, panel.Context.QueuedCount);
        });
    }
    private static object? Get(object panel, string field) => PanelType.GetField(field, Flags)!.GetValue(panel);
    [Fact]
    public async Task CreateFormStartsInEditableNameFieldAndEnterDoesNotLeaveIt()
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices(); registrations.AddSingleton(new SnapshotStore(null)); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p), ["CreateNameMode"] = true }));
            Assert.True((bool)Get(panel, "_inputFocused")!);
            Assert.Contains("Snapshot name (optional)", rendered.ToHtmlString());
            var buffer = (RazorConsole.Core.Input.TextSelectionState)Get(panel, "_input")!;
            buffer.SetText("My context snapshot");
            var handled = await (Task<bool>)PanelType.GetMethod("InputKey", Flags)!.Invoke(panel, [new KeyboardEventArgs { Key = "Enter" }])!;
            Assert.True(handled); Assert.True((bool)Get(panel, "_inputFocused")!);
            Assert.Equal("My context snapshot", buffer.Text);
            await (Task<bool>)PanelType.GetMethod("InputKey", Flags)!.Invoke(panel, [new KeyboardEventArgs { Key = "Tab" }])!;
            Assert.False((bool)Get(panel, "_inputFocused")!); Assert.Equal(0, Get(panel, "_action"));
        });
    }
    public sealed class TestPanel : ContextSnapshotsPanel { protected override void OnInitialized() { if (StartCreating) base.OnInitialized(); } }
    public sealed class Host : ComponentBase
    {
        [Parameter] public Action<TestPanel>? Capture { get; set; }
        [Parameter] public bool CreateNameMode { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TestPanel>(0);
            builder.AddAttribute(1, "Context", new SnapshotContext());
            builder.AddAttribute(3, "StartCreating", CreateNameMode);
            builder.AddAttribute(4, "Endpoint", "http://127.0.0.1:1");
            builder.AddAttribute(5, "CurrentModel", "test-model");
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((TestPanel)value));
            builder.CloseComponent();
        }
    }
}
