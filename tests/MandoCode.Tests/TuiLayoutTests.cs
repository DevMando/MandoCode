using HtmlAgilityPack;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Translators;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Abstractions.Rendering;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public class TuiLayoutTests
{
    [Fact]
    public async Task MultipleTurns_KeepResponsesCodeAndToolResultsInOrder_AbovePrompt()
    {
        var session = new TuiSession();
        session.AppendUserPrompt("howdy!");
        session.Append(MarkdownHtmlRenderer.BuildRenderable("Hello, first response.", "."));
        var first = await Frame(session, 100, 40);
        AssertOrdered(first, "> howdy!", "Hello, first response.", "type / for commands");

        session.AppendUserPrompt("show an example");
        session.SetRunning(true, "Reading Program.cs");
        session.SetPreview("Checking the entry point");
        var running = await Frame(session, 100, 40, disabled: true);
        AssertOrdered(running, "Hello, first response.", "> show an example", "Reading Program.cs", "type / for commands");
        Assert.Contains("Checking the entry point", running);

        var tools = new OperationDisplayRenderer(new ProjectRootAccessor("."));
        session.Append(tools.BuildRenderable(new OperationDisplayEvent
        {
            OperationType = "Read", FilePath = "Program.cs", LineCount = 12
        }));
        session.Append(MarkdownHtmlRenderer.BuildRenderable("Here is the example:\n\n```csharp\nConsole.WriteLine(\"hello\");\n```", "."));
        session.Append(new Text("[100 in, 20 out]"));
        session.SetRunning(false);
        var second = await Frame(session, 100, 40);
        AssertOrdered(second, "> howdy!", "Hello, first response.", "> show an example", "Read(", "Here is the example:", "Console.WriteLine", "[100 in, 20 out]", "type / for commands");
        Assert.DoesNotContain("Checking the entry point", second);
        Assert.DoesNotContain((char)27, second);
    }

    [Theory]
    [InlineData(100, 32)]
    [InlineData(50, 16)]
    [InlineData(32, 12)]
    public async Task LongConversation_FollowsLatestOutput_AndReservesPrompt(int width, int height)
    {
        var session = new TuiSession();
        for (var i = 0; i < 40; i++) session.Append(new Text($"turn {i}: some conversation content"));
        session.Append(new Text("LATEST RESPONSE"));
        var frame = await Frame(session, width, height);
        Assert.Contains("LATEST RESPONSE", frame);
        Assert.Contains("type / for commands", frame);
        Assert.Contains("╔", frame);
        Assert.DoesNotContain("turn 0:", frame);
        Assert.True(frame.Split('\n').Length <= height + 1, frame);
    }

    [Fact]
    public async Task PreviewUpdates_DoNotBecomeTranscriptEntries()
    {
        var session = new TuiSession();
        session.Append(new Text("previous response"));
        session.SetRunning(true, "Thinking");
        session.SetPreview("partial");
        session.SetPreview("partial response growing");
        var frame = await Frame(session, 80, 24);
        Assert.Contains("partial response growing", frame);
        Assert.Contains(session.Snapshot().LoadingMessage, frame);
        Assert.Single(session.Snapshot().Entries);
        session.SetRunning(false);
        Assert.DoesNotContain("partial response growing", await Frame(session, 80, 24));
    }

    [Fact]
    public void LegacyStdout_DropsTerminalControls_AndKeepsMessageOrder()
    {
        var session = new TuiSession();
        var writer = new TuiTranscriptWriter(session);
        writer.Write("\u001b[31m> user");
        writer.Write("\u001b[0m\n");
        writer.Write("\u001b[2J\u001b[1;1Hassistant answer\n");
        var contents = session.Snapshot().Entries.Select(e => Render(e.Content, 80)).ToArray();
        Assert.Equal(2, contents.Length);
        Assert.Contains("> user", contents[0]);
        Assert.Contains("assistant answer", contents[1]);
        Assert.All(contents, text => Assert.DoesNotContain((char)27, text));
    }

    [Fact]
    public async Task MountedShell_UpdatesAcrossTurns_WithoutDuplicatingEarlierResponses()
    {
        var session = new TuiSession();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorConsoleServices();
        services.AddSingleton(session);
        services.AddSingleton<AgentWorkspace>();
        services.AddSingleton<WorkspaceRegistry>();
        services.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        services.AddSingleton<ITerminalViewport>(new FixedViewport(80, 24));
        services.Insert(0, ServiceDescriptor.Singleton<ITranslationMiddleware, TranscriptEntryTranslator>());
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        ModelPickerState? activePicker = null;
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment footer = builder =>
            {
                builder.OpenComponent<PromptInput>(0);
                builder.AddAttribute(1, "ModelPicker", activePicker);
                builder.CloseComponent();
            };
            return await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Footer"] = footer }));
        });

        async Task<string> WaitFor(string text)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var frame = await renderer.Dispatcher.InvokeAsync(() =>
                {
                    var document = new HtmlDocument();
                    document.LoadHtml(root.ToHtmlString());
                    var vdom = VNode.CreateRegion();
                    foreach (var child in document.DocumentNode.ChildNodes)
                        if (ToVNode(child) is { } node) vdom.AddChild(node);
                    var widgets = provider.GetRequiredService<WidgetTranslationContext>();
                    return Render(new LayoutEngine().Layout(widgets.Translate(vdom),
                        new BoxConstraints(80, 80, 24, 24)).PaintToRenderable(), 80);
                });
                if (frame.Contains(text, StringComparison.Ordinal)) return frame;
                await Task.Delay(20);
            }
            throw new TimeoutException("Mounted shell did not render: " + text);
        }

        session.AppendUserPrompt("first");
        session.Append(new Text("first response"));
        await WaitFor("first response");
        session.AppendUserPrompt("second");
        session.SetRunning(true, "Thinking about turn two");
        session.SetPreview("second response in progress");
        var running = await WaitFor("second response in progress");
        AssertOrdered(running, "> first", "first response", "> second", "Thinking about turn two", "type / for commands");
        session.Append(new Text("second response complete"));
        session.SetRunning(false);
        var completed = await WaitFor("second response complete");
        AssertOrdered(completed, "first response", "> second", "second response complete", "type / for commands");
        Assert.Equal(1, completed.Split("first response", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("second response in progress", completed);

        activePicker = new(new[] { "model-one (local)", "model-two (cloud)" });
        session.Append(new Text("Select a model:"));
        var picking = await WaitFor("model-one (local)");
        Assert.DoesNotContain("type / for commands", picking);
        activePicker = null;
        session.Append(new Text("Now using model-two"));
        var switched = await WaitFor("Now using model-two");
        AssertComposerAtBottom(switched, 24);
        Assert.Contains("type / for commands", switched);
        Assert.DoesNotContain("model-one (local)", switched);
    }

    [Theory]
    [InlineData(100, 32)]
    [InlineData(50, 16)]
    [InlineData(32, 12)]
    [InlineData(20, 10)]
    public async Task RunningAndCompletedTurns_KeepComposerAtBottom_AndStatusOutsideTranscript(int width, int height)
    {
        var session = new TuiSession();
        for (var turn = 0; turn < 3; turn++)
        {
            session.AppendUserPrompt($"request {turn}");
            session.SetRunning(true, "Working on a very long activity that should occupy just one row");
            session.SetPreview(string.Join("\n", Enumerable.Range(0, 8).Select(i => $"preview {i}: " + new string('x', 200))));
            var running = await Frame(session, width, height, disabled: true);
            AssertComposerAtBottom(running, height);
            Assert.DoesNotContain("preview", string.Join(" ", session.Snapshot().Entries.Select(e => e.UserPrompt)));
            session.SetPreview("short preview");
            AssertComposerAtBottom(await Frame(session, width, height, disabled: true), height);
            session.Append(new Text($"final response {turn}"));
            session.SetRunning(false);
            var finished = await Frame(session, width, height);
            AssertComposerAtBottom(finished, height);
            Assert.Contains($"final response {turn}", finished);
            Assert.DoesNotContain("preview", finished);
            Assert.DoesNotContain("Working on", finished);
        }
        Assert.Equal(6, session.Snapshot().Entries.Count);
        Assert.Equal(3, session.Snapshot().Entries.Count(entry => entry.UserPrompt is not null));
    }

    [Theory]
    [InlineData(32, 12)]
    [InlineData(20, 10)]
    public async Task SuggestionsAndContextMeter_CannotPushComposerOffscreen(int width, int height)
    {
        var session = new TuiSession();
        session.AppendUserPrompt("previous request");
        session.SetRunning(true, "Thinking");
        session.SetPreview("one\ntwo\nthree");
        var frame = await Frame(session, width, height, disabled: true, metadata: true, initialValue: "/h");
        AssertComposerAtBottom(frame, height);
        Assert.Contains("Commands", frame);
        Assert.Contains("/h", frame);
    }

    [Theory]
    [InlineData(40, "first line\nsecond line\nthird line")]
    [InlineData(40, "one two three four five six seven eight nine ten eleven twelve thirteen fourteen")]
    public async Task Prompt_GrowsToShowWrappedDraft_WithoutOverlappingConversation(int width, string draft)
    {
        var session = new TuiSession();
        session.Append(new Text("LATEST RESPONSE"));
        var frame = await Frame(session, width, 30, initialValue: draft);
        var rows = frame.Replace("\r", "").TrimEnd('\n').Split('\n');
        var top = Array.FindLastIndex(rows, row => row.Contains('╔'));
        Assert.True(top < rows.Length - 3, frame);
        Assert.Contains("LATEST RESPONSE", frame);
        Assert.Contains("╚", rows[^1]);
        var content = string.Join(" ", rows.Skip(top + 1).Take(rows.Length - top - 2));
        foreach (var word in draft.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(word, content);
        Assert.Equal(30, rows.Length);
        AssertComposerAtBottom(await Frame(session, width, 30), 30);
    }
    private static void AssertComposerAtBottom(string frame, int height)
    {
        var rows = frame.Replace("\r", "").TrimEnd('\n').Split('\n');
        Assert.Equal(height, rows.Length);
        Assert.Contains("╚", rows[^1]);
        Assert.Contains("╔", rows[^3]);
    }

    private sealed class FixedViewport(int width, int height) : ITerminalViewport
    {
        public int Width => width;
        public int Height => height;
        public event Action? OnResized { add { } remove { } }
    }


    internal static async Task<string> Frame(TuiSession session, int width, int height, bool disabled = false, bool metadata = false, string initialValue = "", ModelPickerState? modelPicker = null, SnapshotStore? snapshots = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorConsoleServices();
        services.AddSingleton(session);
        services.AddSingleton<AgentWorkspace>();
        services.AddSingleton<WorkspaceRegistry>();
        if (snapshots is not null) services.AddSingleton(snapshots);
        services.AddSingleton<ITerminalViewport>(new FixedViewport(width, height));
        services.AddSingleton(new InputStateMachine(new Dictionary<string, string> { ["/help"] = "Help" }, null));
        services.Insert(0, ServiceDescriptor.Singleton<ITranslationMiddleware, TranscriptEntryTranslator>());
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment footer = builder =>
            {
                if (metadata)
                {
                    builder.OpenComponent<RazorConsole.Components.Box>(10);
                    builder.AddAttribute(11, "Height", 1);
                    builder.AddAttribute(12, "FillWidth", true);
                    builder.AddAttribute(13, "ChildContent", (RenderFragment)(b =>
                    {
                        b.OpenComponent<ContextMeterLine>(0);
                        b.AddAttribute(1, "UsedTokens", 8000L);
                        b.AddAttribute(2, "WindowTokens", 32000);
                        b.CloseComponent();
                    }));
                    builder.CloseComponent();
                }
                builder.OpenComponent<PromptInput>(0);
                builder.AddAttribute(1, "Disabled", disabled);
                builder.AddAttribute(2, "InitialValue", initialValue);
                builder.AddAttribute(3, "ModelPicker", modelPicker);
                builder.CloseComponent();
            };
            var parameters = new Dictionary<string, object?> { ["Footer"] = footer };
            if (snapshots is not null)
            {
                parameters["SnapshotsOpen"] = true;
                parameters["SnapshotContext"] = new SnapshotContext();
                parameters["SettingsConfig"] = new MandoCodeConfig { ModelName = "test-model", AllowPersistence = false };
                parameters["OllamaEndpoint"] = "http://127.0.0.1:1";
            }
            var root = await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(parameters));
            var document = new HtmlDocument();
            document.LoadHtml(root.ToHtmlString());
            var vdom = VNode.CreateRegion();
            foreach (var child in document.DocumentNode.ChildNodes)
                if (ToVNode(child) is { } node) vdom.AddChild(node);
            var widgets = provider.GetRequiredService<WidgetTranslationContext>();
            var result = new LayoutEngine().Layout(widgets.Translate(vdom), new BoxConstraints(width, width, height, height));
            return Render(result.PaintToRenderable(), width);
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

    private static string Render(Spectre.Console.Rendering.IRenderable renderable, int width)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors
        });
        console.Profile.Width = width;
        console.Write(renderable);
        return writer.ToString();
    }

    private static void AssertOrdered(string frame, params string[] parts)
    {
        var position = 0;
        foreach (var part in parts)
        {
            var found = frame.IndexOf(part, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"Missing or out of order: {part}\n{frame}");
            position = found + part.Length;
        }
    }
}
