using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Xunit;

namespace MandoCode.Tests;

public sealed class PromptPasteTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [Theory]
    [InlineData("first\r\n\tsecond\r\n\r\n", "first\n\tsecond\n\n", false)]
    [InlineData("first\r\nsecond\r\n", "first\nsecond\n", false)]
    [InlineData("first\rsecond", "first\nsecond", false)]
    [InlineData("\n", "\n", false)]
    public async Task ClipboardPastePreservesNewlinesIndentationAndSelectionWithoutSubmitting(string pasted, string expected, bool alt)
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<IClipboardImageReader>(new TextReader(pasted));
        registrations.AddSingleton<IClipboardTextReader>(sp => (TextReader)sp.GetRequiredService<IClipboardImageReader>());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!; var submissions = new List<string>();
            await renderer.RenderComponentAsync<ClipboardImageTests.Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Capture"] = (Action<PromptInput>)(p => input = p), ["Submit"] = (Action<string>)(s => submissions.Add(s))
            }));
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            composer.SetText("before REPLACE after"); composer.Buffer.Begin(7, 1, false); composer.Buffer.Extend(14); composer.Buffer.End();
            Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = "v", AltKey = alt, CtrlKey = !alt }])!);
            Assert.Equal("before " + expected + " after", composer.Buffer.Text);
            Assert.Empty(submissions);
            await (Task)typeof(PromptInput).GetMethod("HandleSubmit", Flags)!.Invoke(input, [composer.Buffer.Text])!;
            Assert.Equal("before " + expected + " after", Assert.Single(submissions));
        });
    }
    [Theory]
    [InlineData("first\n\tsecond\n\n")]
    [InlineData("\n\nfirst\nsecond\n")]
    [InlineData("one\ntwo")]
    public async Task QueuedTerminalPasteNeverSubmitsAndFollowingEnterIsNormal(string pasted)
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!; var submissions = new List<string>();
            await renderer.RenderComponentAsync<ClipboardImageTests.Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Capture"] = (Action<PromptInput>)(p => input = p), ["Submit"] = (Action<string>)(s => submissions.Add(s))
            }));
            var pending = default(PasteQueueState);
            typeof(PromptInput).GetField("_pasteGuard", Flags)!.SetValue(input, new TerminalPasteGuard(() => pending));
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            for (var i = 0; i < pasted.Length; i++) {
                var remaining = pasted[(i + 1)..]; pending = new(remaining.Length > 0, remaining.Contains('\n'), remaining.Count(c => !char.IsControl(c)) >= 8);
                var key = pasted[i] == '\n' ? "Enter" : pasted[i] == '\t' ? "Tab" : pasted[i].ToString();
                var handled = await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = key }])!;
                if (!handled) { Assert.NotEqual("Enter", key); composer.Buffer.Insert(key); }
                Assert.Empty(submissions);
            }
            Assert.Equal(pasted, composer.Buffer.Text);
            pending = default;
            Assert.False(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = "Enter" }])!);
            await (Task)typeof(PromptInput).GetMethod("HandleSubmit", Flags)!.Invoke(input, [composer.Buffer.Text])!;
            Assert.Equal(pasted, Assert.Single(submissions));
        });
    }
    [Fact]
    public void TypingAndShiftEnterWithoutQueuedPasteStayWithTheEditor()
    {
        var guard = new TerminalPasteGuard(() => default);
        Assert.False(guard.IsPasteKey(new() { Key = "a" }));
        Assert.False(guard.IsPasteKey(new() { Key = "Enter" }));
        Assert.False(guard.IsPasteKey(new() { Key = "Enter", ShiftKey = true }));
        Assert.False(guard.IsPasteKey(new() { Key = "ArrowLeft", AltKey = true }));
        var enterQueued = true;
        var normalTyping = new TerminalPasteGuard(() => enterQueued ? new(true, true) : default);
        Assert.False(normalTyping.IsPasteKey(new() { Key = "a" }));
        enterQueued = false; Assert.False(normalTyping.IsPasteKey(new() { Key = "Enter" }));
    }
    [Fact]
    public async Task AltVStaysImageOnlyWhenClipboardContainsText()
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<IClipboardImageReader>(new TextReader("must not paste this text"));
        registrations.AddSingleton<IClipboardTextReader>(sp => (TextReader)sp.GetRequiredService<IClipboardImageReader>());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!;
            var rendered = await renderer.RenderComponentAsync<ClipboardImageTests.Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Capture"] = (Action<PromptInput>)(p => input = p)
            }));
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            composer.SetText("keep my draft");
            Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = "v", AltKey = true }])!);
            Assert.Equal("keep my draft", composer.Buffer.Text);
            Assert.Contains("No Image in the clipboard. Copy an image, then press Alt+V", System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString()));
        });
    }    private sealed class TextReader(string text) : IClipboardImageReader, IClipboardTextReader {
        public Task<byte[]?> ReadPngAsync(CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(text);
    }
}