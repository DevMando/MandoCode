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

public sealed class AgentHistoryKeyboardTests
{
    [Fact]
    public async Task ProjectsGroupByFullPathAndLongSearchKeepsTypedEndVisible()
    {
        using var folder = new AgentArchiveTests.ArchiveFolder();
        var recent = AgentArchiveTests.Sample(name: "Jetik") with { ProjectRoot = Path.Combine(Path.GetTempPath(), "one", "project") };
        var older = AgentArchiveTests.Sample(name: "Voxel") with { ProjectRoot = recent.ProjectRoot, ClosedAt = recent.ClosedAt!.Value.AddHours(-2) };
        var other = AgentArchiveTests.Sample(name: "Kernel") with { ProjectRoot = Path.Combine(Path.GetTempPath(), "two", "project"), ClosedAt = recent.ClosedAt!.Value.AddHours(-1) };
        folder.Store.Save(recent); folder.Store.Save(older); folder.Store.Save(other);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(folder.Store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AgentHistoryBrowser browser = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(Host.Capture)] = (Action<AgentHistoryBrowser>)(b => browser = b) }));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Value<T>(string name) => (T)typeof(AgentHistoryBrowser).GetField(name, flags)!.GetValue(browser)!;
            async Task Key(string key) => await (Task)typeof(AgentHistoryBrowser).GetMethod("Key", flags)!
                .Invoke(browser, [new KeyboardEventArgs { Key = key }])!;
            Assert.Equal(new[] { recent.Key, older.Key, other.Key }, Value<IReadOnlyList<ArchivedAgent>>("_items").Select(a => a.Key));
            Assert.Equal(2, Value<List<(int Index, string? Project)>>("_rows").Count(r => r.Project is not null));
            await Key("ArrowDown"); Assert.Equal(1, Value<int>("_selected"));
            await Key("End"); Assert.Equal(2, Value<int>("_selected"));
            var query = new string('x', 80) + "TAIL123";
            foreach (var letter in query) await Key(letter.ToString());
            var caption = (string)typeof(AgentHistoryBrowser).GetProperty("SearchCaption", flags)!.GetValue(browser)!;
            var width = (int)typeof(AgentHistoryBrowser).GetProperty("SearchWidth", flags)!.GetValue(browser)!;
            Assert.Equal(query, Value<string>("_query"));
            Assert.Contains("TAIL123", caption); Assert.Contains("…", caption);
            Assert.Equal(width, RazorConsole.Core.Input.TextSelectionState.CellWidth(caption));
            await Key("Backspace"); Assert.Equal(query[..^1], Value<string>("_query"));
        });
    }
    [Fact]
    public async Task ConversationLoadsFullArchiveAndResetsToBottomWhenAgentChanges()
    {
        using var folder = new AgentArchiveTests.ArchiveFolder();
        var archive = AgentArchiveTests.Sample() with { Transcript = Enumerable.Range(0, 40)
            .Select(i => new ArchivedBlock([new ArchivedSpan("Transcript line " + i, "green")], null, true)).ToList() };
        archive.Messages[0].Text = new string('x', 1000);
        folder.Store.Save(archive);
        var other = AgentArchiveTests.Sample(name: "Voxel"); folder.Store.Save(other);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(folder.Store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ConversationHost host = null!;
            await renderer.RenderComponentAsync<ConversationHost>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(ConversationHost.Capture)] = (Action<ConversationHost>)(h => host = h) }));
            host.Select(folder.Store.Closed().Single(a => a.Key == archive.Key));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Value<T>(string name) => (T)typeof(AgentHistoryConversation).GetField(name, flags)!.GetValue(host.Conversation)!;
            Assert.Equal(40, Value<IReadOnlyList<TuiEntry>>("_blocks").Count);
            Assert.Equal(1000, Value<ArchivedAgent>("_archive").Messages[0].Text.Length);
            Assert.True(Value<bool>("_following"));
            typeof(AgentHistoryConversation).GetField("_following", flags)!.SetValue(host.Conversation, false);
            typeof(AgentHistoryConversation).GetField("_offset", flags)!.SetValue(host.Conversation, 12);
            host.Select(other);
            Assert.True(Value<bool>("_following")); Assert.Equal(0, Value<int>("_offset"));
            Assert.Equal(other.Messages.Count, Value<IReadOnlyList<TuiEntry>>("_blocks").Count);
        });
    }
    public sealed class ConversationHost : ComponentBase
    {
        [Parameter] public Action<ConversationHost>? Capture { get; set; }
        public AgentHistoryConversation Conversation { get; private set; } = null!;
        private ArchivedAgent? _agent;
        public void Select(ArchivedAgent agent) { _agent = agent; StateHasChanged(); }
        protected override void OnInitialized() => Capture?.Invoke(this);
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AgentHistoryConversation>(0);
            builder.AddAttribute(1, "Agent", _agent);
            builder.AddComponentReferenceCapture(2, c => Conversation = (AgentHistoryConversation)c);
            builder.CloseComponent();
        }
    }
    [Fact]
    public async Task OpenHistoryUpdatesWhenAnotherAgentCloses()
    {
        using var folder = new AgentArchiveTests.ArchiveFolder();
        folder.Store.Save(AgentArchiveTests.Sample(name: "Jetik"));
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(folder.Store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AgentHistoryBrowser browser = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(Host.Capture)] = (Action<AgentHistoryBrowser>)(b => browser = b) }));
            folder.Store.Save(AgentArchiveTests.Sample(name: "Voxel"));
            var items = (IReadOnlyList<ArchivedAgent>)typeof(AgentHistoryBrowser).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
            Assert.Equal(2, items.Count);
            Assert.Contains(items, a => a.Name == "Voxel");
        });
    }
    [Fact]
    public async Task SearchRestoreAndDeleteConfirmationAreKeyboardAccessible()
    {
        using var folder = new AgentArchiveTests.ArchiveFolder();
        var first = AgentArchiveTests.Sample(name: "Jetik");
        var second = AgentArchiveTests.Sample(name: "Voxel") with { ClosedAt = first.ClosedAt!.Value.AddMinutes(-1) };
        folder.Store.Save(first); folder.Store.Save(second);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(folder.Store); registrations.AddSingleton<TuiSession>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AgentHistoryBrowser browser = null!; ArchivedAgent? restored = null; var closed = false;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                [nameof(Host.Capture)] = (Action<AgentHistoryBrowser>)(b => browser = b),
                [nameof(Host.Restore)] = (Action<ArchivedAgent>)(a => restored = a),
                [nameof(Host.Close)] = (Action)(() => closed = true) }));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            async Task Key(string key, bool shift = false, bool ctrl = false) => await (Task)typeof(AgentHistoryBrowser).GetMethod("Key", flags)!
                .Invoke(browser, [new KeyboardEventArgs { Key = key, ShiftKey = shift, CtrlKey = ctrl }])!;
            T Value<T>(string name) => (T)typeof(AgentHistoryBrowser).GetField(name, flags)!.GetValue(browser)!;
            await Key("ArrowDown"); Assert.Equal(1, Value<int>("_selected"));
            await Key("Enter"); Assert.Equal(second.Key, restored!.Key);
            foreach (var letter in "Jetik") await Key(letter.ToString());
            Assert.Single(Value<IReadOnlyList<ArchivedAgent>>("_items"));
            Assert.Equal(first.Key, Value<IReadOnlyList<ArchivedAgent>>("_items")[0].Key);
            await Key("Tab"); Assert.True(Value<bool>("_conversationFocused"));
            await Key("ArrowUp"); Assert.Single(Value<IReadOnlyList<ArchivedAgent>>("_items"));
            await Key("Tab", shift: true); Assert.False(Value<bool>("_conversationFocused"));
            await Key("ArrowRight"); Assert.Equal(1, Value<int>("_action"));
            await Key("Enter"); Assert.True(Value<bool>("_confirm"));
            await Key("Enter"); // Cancel is the default.
            Assert.NotNull(folder.Store.Load(first.Key));
            await Key("ArrowRight"); Assert.Equal(1, Value<int>("_action"));
            await Key("Enter"); await Key("ArrowRight"); await Key("Enter");
            Assert.Null(folder.Store.Load(first.Key));
            await Key("u", ctrl: true); Assert.Single(Value<IReadOnlyList<ArchivedAgent>>("_items"));
            await Key("ArrowLeft"); Assert.Equal(2, Value<int>("_action"));
            await Key("Enter"); Assert.True(closed);
        });
    }
    public sealed class Host : ComponentBase
    {
        [Parameter] public Action<AgentHistoryBrowser>? Capture { get; set; }
        [Parameter] public Action<ArchivedAgent>? Restore { get; set; }
        [Parameter] public Action? Close { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AgentHistoryBrowser>(0);
            builder.AddAttribute(1, "Width", 60); builder.AddAttribute(2, "Height", 20);
            builder.AddAttribute(3, "OnRestore", EventCallback.Factory.Create<ArchivedAgent>(this, a => Restore?.Invoke(a)));
            builder.AddAttribute(4, "OnClose", EventCallback.Factory.Create(this, () => Close?.Invoke()));
            builder.AddComponentReferenceCapture(5, b => Capture?.Invoke((AgentHistoryBrowser)b)); builder.CloseComponent();
        }
    }
}
