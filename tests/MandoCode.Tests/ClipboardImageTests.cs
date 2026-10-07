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

public sealed class ClipboardImageTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1];
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [Fact]
    public void ImagesAreBoundedAgentLocalAndReferencesRoundTrip()
    {
        var store = new ClipboardImageStore();
        var original = Png.ToArray(); var reference = store.Add(original); original[8] = 2;
        Assert.Equal(reference, FileReferenceToken.Paths(FileReferenceToken.Format(reference)).Single());
        Assert.True(store.TryGet(reference, out var image)); Assert.Equal(Png, image);
        Assert.False(new ClipboardImageStore().TryGet(reference, out _));
        Assert.Throws<IOException>(() => store.Add([1, 2, 3]));
        Assert.Throws<IOException>(() => store.Add(new byte[AIService.MaxImageInputBytes + 1]));
        for (var i = 0; i < ClipboardImageStore.Capacity; i++) store.Add(Png);
        Assert.False(store.TryGet(reference, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagePasteInsertsAReferenceAndEnterSubmitsIt(bool alt)
    {
        var reader = new Reader(Png);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<IClipboardImageReader>(reader); registrations.AddSingleton<ClipboardImageStore>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!; string? submitted = null;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Capture"] = (Action<PromptInput>)(p => input = p), ["Submit"] = (Action<string>)(s => submitted = s)
            }));
            var handled = await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = "v", AltKey = alt, CtrlKey = !alt }])!;
            Assert.True(handled); Assert.Equal(1, reader.Reads);
            Assert.Contains("Clipboard image attached", rendered.ToHtmlString());
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            var value = composer.Buffer.Text;
            var reference = FileReferenceToken.Paths(value).Single();
            Assert.True(services.GetRequiredService<ClipboardImageStore>().TryGet(reference, out var image)); Assert.Equal(Png, image);
            await (Task)typeof(PromptInput).GetMethod("HandleSubmit", Flags)!.Invoke(input, [value])!;
            Assert.Equal(value, submitted); Assert.Equal("", composer.Buffer.Text);
            Assert.DoesNotContain("Clipboard image attached", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task TextPasteFallsThroughAndDisabledInputDoesNotReadClipboard()
    {
        var reader = new Reader(null);
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        registrations.AddSingleton<IClipboardImageReader>(reader);
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<PromptInput>)(p => input = p) }));
            Task<bool> Key(bool alt) => (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input, [new KeyboardEventArgs { Key = "v", AltKey = alt, CtrlKey = !alt }])!;
            Assert.False(await Key(false)); Assert.True(await Key(true)); Assert.Equal(2, reader.Reads);
            typeof(PromptInput).GetProperty("Disabled")!.SetValue(input, true); Assert.True(await Key(true)); Assert.Equal(2, reader.Reads);
        });
    }
    private sealed class Reader(byte[]? bytes) : IClipboardImageReader {
        public int Reads { get; private set; }
        public Task<byte[]?> ReadPngAsync(CancellationToken cancellationToken) { Reads++; return Task.FromResult(bytes); }
    }
    public sealed class Host : ComponentBase {
        [Parameter] public Action<PromptInput>? Capture { get; set; }
        [Parameter] public Action<string>? Submit { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder) {
            builder.OpenComponent<PromptInput>(0);
            builder.AddAttribute(1, "OnSubmit", EventCallback.Factory.Create<string>(this, s => Submit?.Invoke(s)));
            builder.AddComponentReferenceCapture(2, p => Capture?.Invoke((PromptInput)p)); builder.CloseComponent();
        }
    }
}