using System.Reflection;
using MandoCode.Acp.Protocol;
using MandoCode.Components;
using MandoCode.Services;
using MandoCode.Services.Acp;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MandoCode.Tests.Acp;

[Trait("Category", "Component")]
public class AcpImagePromptTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1];
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, AcpRuntimeStatus.Ready, true, 1)]
    [InlineData(true, AcpRuntimeStatus.Ready, false, 1)]
    [InlineData(false, AcpRuntimeStatus.Ready, true, 0)]
    [InlineData(false, AcpRuntimeStatus.Ready, false, 1)]
    [InlineData(true, AcpRuntimeStatus.Starting, true, 0)]
    public async Task PasteChecksTheConnectedAgentsCapability(bool image, AcpRuntimeStatus status, bool alt, int reads)
    {
        var reader = new Reader(Png);
        await using var services = AcpTestHost.Services(configure: s => { s.AddSingleton<IClipboardImageReader>(reader); s.AddSingleton<IClipboardTextReader>(reader); });
        var pane = services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "test");
        AcpPaneMode.Enter(pane, "test");
        var runtime = new FakeRuntime { Status = status };
        runtime.PromptCapabilities.Image = image;
        pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = runtime;
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PromptInput input = null!;
            var root = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Pane"] = pane, ["Capture"] = (Action<PromptInput>)(p => input = p)
            }));
            Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input,
                [new KeyboardEventArgs { Key = "v", AltKey = alt, CtrlKey = !alt }])!);
            Assert.Equal(reads, reader.Reads);
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            if (image && status == AcpRuntimeStatus.Ready)
            {
                var prompt = AcpPromptBuilder.Build(composer.Buffer.Text, Path.GetTempPath(),
                    pane.Services.GetRequiredService<ClipboardImageStore>(), image);
                Assert.Equal(Png, Convert.FromBase64String(Assert.Single(prompt.OfType<ImageContent>()).Data));
                Assert.Contains("Clipboard image attached", root.ToHtmlString());
            }
            else
            {
                Assert.Empty(composer.Buffer.Text);
                Assert.Contains(status == AcpRuntimeStatus.Ready ? "does not support image prompts" : "Wait for the ACP agent", root.ToHtmlString());
            }
        });
    }

    [Fact]
    public async Task CtrlVTextStillWorksForAnAgentWithoutImageSupport()
    {
        var reader = new Reader(null);
        await using var services = AcpTestHost.Services(configure: s => { s.AddSingleton<IClipboardImageReader>(reader); s.AddSingleton<IClipboardTextReader>(reader); });
        var pane = services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "test");
        AcpPaneMode.Enter(pane, "test");
        pane.Services.GetRequiredService<AcpRuntimeHolder>().Runtime = new FakeRuntime();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PromptInput input = null!;
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Pane"] = pane, ["Capture"] = (Action<PromptInput>)(p => input = p)
            }));
            Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", Flags)!.Invoke(input,
                [new KeyboardEventArgs { Key = "v", CtrlKey = true }])!);
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", Flags)!.GetValue(input)!;
            Assert.Equal("pasted text", composer.Buffer.Text);
        });
    }

    [Fact]
    public void PromptIncludesTextAndPngOnceAndRemovingReferenceRemovesImage()
    {
        var store = new ClipboardImageStore();
        var reference = store.Add(Png);
        var input = $"describe @{reference} @{reference}.";
        var blocks = AcpPromptBuilder.Build(input, Path.GetTempPath(), store, true);
        Assert.Equal(input, Assert.IsType<TextContent>(blocks[0]).Text);
        var image = Assert.Single(blocks.OfType<ImageContent>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Png, Convert.FromBase64String(image.Data));
        Assert.Null(image.Uri);
        Assert.Empty(blocks.OfType<ResourceLink>());
        Assert.Empty(AcpPromptBuilder.Build("describe", Path.GetTempPath(), store, true).OfType<ImageContent>());
    }

    [Fact]
    public void UnsupportedExpiredAndOtherPaneReferencesFailRatherThanSendingTextOnly()
    {
        var store = new ClipboardImageStore();
        var reference = store.Add(Png);
        Assert.Contains("does not support", Assert.Throws<IOException>(() =>
            AcpPromptBuilder.Build("@" + reference, Path.GetTempPath(), store, false)).Message);
        Assert.Contains("no longer available", Assert.Throws<IOException>(() =>
            AcpPromptBuilder.Build("@" + reference, Path.GetTempPath(), new ClipboardImageStore(), true)).Message);
        for (var i = 0; i < ClipboardImageStore.Capacity; i++) store.Add(Png);
        Assert.Contains("no longer available", Assert.Throws<IOException>(() =>
            AcpPromptBuilder.Build("@" + reference, Path.GetTempPath(), store, true)).Message);
    }

    [Fact]
    public void CombinedImagesHaveAPromptBudget()
    {
        var store = new ClipboardImageStore();
        var large = new byte[AIService.MaxImageInputBytes];
        Png.CopyTo(large, 0);
        var references = Enumerable.Range(0, 5).Select(_ => "@" + store.Add(large)).ToArray();
        Assert.Contains("16 MiB", Assert.Throws<IOException>(() =>
            AcpPromptBuilder.Build(string.Join(" ", references), Path.GetTempPath(), store, true)).Message);
    }

    private sealed class Reader(byte[]? png) : IClipboardImageReader, IClipboardTextReader
    {
        public int Reads;
        public Task<byte[]?> ReadPngAsync(CancellationToken cancellationToken) { Reads++; return Task.FromResult(png); }
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("pasted text");
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public AgentPane Pane { get; set; } = default!;
        [Parameter] public Action<PromptInput>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<AgentPane>>(0);
            builder.AddAttribute(1, "Value", Pane);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(b => {
                b.OpenComponent<PromptInput>(0);
                b.AddComponentReferenceCapture(1, p => Capture?.Invoke((PromptInput)p));
                b.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
