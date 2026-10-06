using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using MandoCode.Translators;
using RazorConsole.Core.Abstractions.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Vdom;
using HtmlAgilityPack;
using Xunit;

namespace MandoCode.Tests;

public class PromptEmojiPickerTests
{
    [Theory]
    [InlineData(":", 1, "Enter", "😀")]
    [InlineData("hello :cool world", 11, "Enter", "hello 😎 world")]
    [InlineData(":llam", 5, "Enter", "🦙")]
    [InlineData(":cool", 5, "Escape", ":cool")]
    [InlineData(":not_an_emoji", 13, "Escape", ":not_an_emoji")]
    [InlineData(":cool:", 6, "", "😎")]
    [InlineData(":unknown:", 9, "", ":unknown:")]
    [InlineData("https://example.com", 19, "", "https://example.com")]
    [InlineData("C:\\files", 8, "", "C:\\files")]
    public async Task ColonFiltersReplacesAndCloses(string text, int caret, string action, string expected)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new TuiSession());
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<ITerminalViewport>(new FixedViewport(80, 24));
        using var provider = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PromptInput prompt = null!;
            RenderFragment footer = b =>
            {
                b.OpenComponent<PromptInput>(0);
                b.AddComponentReferenceCapture(1, value => prompt = (PromptInput)value);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Footer"] = footer }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", flags)!.GetValue(prompt)!;
            void Change(string value, int position)
            {
                composer.SetText(value);
                composer.Buffer.Begin(position, 1, false); composer.Buffer.End(); composer.Buffer.ClearSelection();
                typeof(PromptInput).GetMethod("HandleChanged", flags)!.Invoke(prompt, null);
            }
            // Typing ':' opens immediately; completing or replacing the token closes it.
            Change(":", 1);
            Assert.Contains("Enter insert", root.ToHtmlString());
            Change(text, caret);
            if (action.Length > 0)
            {
                Assert.Contains("Enter insert", root.ToHtmlString());
                Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", flags)!.Invoke(prompt, [new KeyboardEventArgs { Key = action }])!);
            }
            Assert.Equal(expected, composer.Buffer.Text);
            Assert.DoesNotContain("Enter insert", root.ToHtmlString());
            typeof(PromptInput).GetMethod("HandleChanged", flags)!.Invoke(prompt, null);
            Assert.DoesNotContain("Enter insert", root.ToHtmlString());
            if (action.Length > 0) Assert.Equal(expected.Length - (text.Length - caret), composer.Buffer.Cursor);
        });
    }

    [Theory]
    [InlineData(false, 80)]
    [InlineData(true, 80)]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    public async Task PickerInsertsAtCaretOrCancelsWithoutSubmitting(bool cancel, int width)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new TuiSession());
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<ITerminalViewport>(new FixedViewport(width, 24));
        registrations.Insert(0, ServiceDescriptor.Singleton<ITranslationMiddleware, TranscriptEntryTranslator>());
        using var provider = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PromptInput prompt = null!;
            var submissions = 0;
            RenderFragment footer = b =>
            {
                b.OpenComponent<PromptInput>(0);
                b.AddAttribute(1, "InitialValue", "hello world");
                b.AddAttribute(2, "OnSubmit", EventCallback.Factory.Create<string>(this, _ => submissions++));
                b.AddComponentReferenceCapture(3, value => prompt = (PromptInput)value);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Footer"] = footer }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", flags)!.GetValue(prompt)!;
            composer.Buffer.Begin(6, 1, false);
            composer.Buffer.End();
            composer.Buffer.ClearSelection();
            async Task<bool> Key(string key) => await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", flags)!.Invoke(prompt, [new KeyboardEventArgs { Key = key }])!;
            Assert.True(await Key("Tab"));
            Assert.Contains("Enter insert", root.ToHtmlString());
            var document = new HtmlDocument();
            document.LoadHtml(root.ToHtmlString());
            var vdom = VNode.CreateRegion();
            foreach (var child in document.DocumentNode.ChildNodes)
                if (ToVNode(child) is VNode node) vdom.AddChild(node);
            var renderable = new LayoutEngine().Layout(provider.GetRequiredService<WidgetTranslationContext>().Translate(vdom), new BoxConstraints(width, width, 24, 24)).PaintToRenderable();
            var frame = (string)typeof(TuiLayoutTests).GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [renderable, width])!;
            Assert.True(frame.Contains("😀"), frame);
            Assert.True(frame.TrimEnd('\n').Split('\n').Length <= 24);
            Assert.False(await Key("n"));
            Assert.False(await Key("Backspace"));
            Assert.False(await Key("Delete"));
            // Unhandled keys are edited by TextArea, which then calls HandleChanged.
            PromptEmojiAliases.Insert(composer.Buffer, "new ");
            typeof(PromptInput).GetMethod("HandleChanged", flags)!.Invoke(prompt, null);
            Assert.Contains("Enter insert", root.ToHtmlString());
            Assert.True(await Key("ArrowRight"));
            Assert.True(await Key(cancel ? "Escape" : "Enter"));
            Assert.Equal(cancel ? "hello new world" : "hello new 😄world", composer.Buffer.Text);
            Assert.Equal(cancel ? 10 : 12, composer.Buffer.Cursor);
            Assert.DoesNotContain("Enter insert", root.ToHtmlString());
            Assert.Equal(0, submissions);
        });
    }

    private sealed class FixedViewport(int width, int height) : ITerminalViewport
    {
        public int Width => width;
        public int Height => height;
        public event Action? OnResized { add { } remove { } }
    }

    private static VNode? ToVNode(HtmlNode html)
    {
        if (html.NodeType == HtmlNodeType.Text)
        {
            var text = System.Net.WebUtility.HtmlDecode(html.InnerText);
            return string.IsNullOrWhiteSpace(text) ? null : VNode.CreateText(text);
        }
        if (html.NodeType != HtmlNodeType.Element) return null;
        var node = VNode.CreateElement(html.Name);
        foreach (var attribute in html.Attributes) node.SetAttribute(attribute.Name, System.Net.WebUtility.HtmlDecode(attribute.Value));
        foreach (var child in html.ChildNodes) if (ToVNode(child) is { } nested) node.AddChild(nested);
        return node;
    }
}
