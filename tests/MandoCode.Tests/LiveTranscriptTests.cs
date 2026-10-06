using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Xunit;

namespace MandoCode.Tests;

public sealed class LiveTranscriptTests
{
    [Fact]
    public async Task RepeatedExpansionAndScrollDoesNotDuplicateTranscriptNodes()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { AllowPersistence = false }, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        var session = services.GetRequiredService<AgentWorkspace>().Add().Session;
        for (var line = 0; line < 60; line++) session.Append(new Spectre.Console.Text($"Earlier transcript line {line}"));
        session.AppendAgentExchange("Raycast to Decoy\nUnique weather request", Spectre.Console.Color.Purple, "q1", "Completed");
        session.AppendAgentDetail("q1", "Reply", new Spectre.Console.Text("Unique weather reply"));
        session.AppendAgentExchange("Decoy · Reply sent\nResponse delivered to the requesting agent.", Spectre.Console.Color.Purple, "q1", "Completed");
        session.AppendSpaced(new Spectre.Console.Text("Final user answer"));
        var group = Assert.Single(session.Snapshot().Entries, e => e.AgentActivity is not null).AgentActivity!;
        var type = typeof(RazorConsole.Core.Focus.FocusManager).Assembly.GetType("RazorConsole.Core.Rendering.ConsoleRenderer")!;
        var instance = ActivatorUtilities.CreateInstance(services, type, new ConsoleAppOptions { RenderingPipeline = RazorConsoleRenderingPipeline.WidgetLayout });
        await using var renderer = (IAsyncDisposable)instance;
        Host host = null!;
        var mount = type.GetMethods().Single(m => m.Name == "MountComponentAsync" && m.IsGenericMethodDefinition);
        await (Task)mount.MakeGenericMethod(typeof(Host)).Invoke(instance, [ParameterView.FromDictionary(new Dictionary<string, object?> { ["Session"] = session, ["Ready"] = (Action<Host>)(value => host = value) }), CancellationToken.None])!;
        var dispatcher = (Dispatcher)type.GetProperty("Dispatcher")!.GetValue(instance)!;
        var refresh = type.GetMethod("RefreshSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var i = 0; i < 12; i++)
        {
            if (i == 3)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    session.AppendAgentDetail("q1", "Delivered", new Spectre.Console.Text("Late delivery acknowledgement"), "Completed");
                    host.Refresh();
                });
            }
            await dispatcher.InvokeAsync(() => host.Toggle(group));
            await dispatcher.InvokeAsync(() => host.View.Scroll(i % 2 == 0 ? "PageUp" : "ArrowDown"));
            var snapshot = refresh.Invoke(instance, null)!;
            var root = (VNode)snapshot.GetType().GetProperty("Root")!.GetValue(snapshot)!;
            var ids = Flatten(root).Where(n => n.Attributes.ContainsKey("data-transcript-entry")).Select(n => n.Attributes["data-transcript-entry"]).ToArray();
            Assert.Equal(group.Expanded ? (i >= 3 ? 65 : 64) : 61, ids.Length);
            Assert.Equal(ids.Length, ids.Distinct().Count());
            var renderable = (Spectre.Console.Rendering.IRenderable)snapshot.GetType().GetProperty("Renderable")!.GetValue(snapshot)!;
            using var writer = new StringWriter();
            var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings { Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No });
            console.Profile.Width = 100;
            console.Profile.Height = 24;
            console.Write(renderable);
            var frame = writer.ToString();
            Assert.True(frame.Split("Unique weather request", StringSplitOptions.None).Length <= 2, frame);
            Assert.True(frame.Split("Response delivered to the requesting agent.", StringSplitOptions.None).Length <= 2, frame);
            Assert.True(frame.Split("Late delivery acknowledgement", StringSplitOptions.None).Length <= 2, frame);
            await dispatcher.InvokeAsync(() => host.View.Scroll("End"));
            snapshot = refresh.Invoke(instance, null)!;
            renderable = (Spectre.Console.Rendering.IRenderable)snapshot.GetType().GetProperty("Renderable")!.GetValue(snapshot)!;
            writer.GetStringBuilder().Clear();
            console.Write(renderable);
            if (group.Expanded) Assert.Contains("Unique weather request", writer.ToString());
        }
    }
    public sealed class Host : ComponentBase
    {
        [Parameter] public TuiSession Session { get; set; } = default!;
        [Parameter] public Action<Host> Ready { get; set; } = default!;
        public ConversationView View { get; private set; } = default!;
        protected override void OnInitialized() => Ready(this);
        public void Refresh() => StateHasChanged();
        public void Toggle(AgentExchange group) { group.Expanded = !group.Expanded; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ConversationView>(0);
            builder.AddAttribute(1, "Entries", Session.Snapshot().Entries);
            builder.AddComponentReferenceCapture(2, value => View = (ConversationView)value);
            builder.CloseComponent();
        }
    }
    private static IEnumerable<VNode> Flatten(VNode node)
    {
        yield return node;
        foreach (var child in node.Children) foreach (var nested in Flatten(child)) yield return nested;
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width => 100;
        public int Height => 24;
        public event Action? OnResized { add { } remove { } }
    }
}
