using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Input;
using RazorConsole.Core.Rendering;
using Xunit;

namespace MandoCode.Tests;

public class PlanStepEditorTests
{
    [Theory]
    [InlineData(40, 16)]
    [InlineData(100, 24)]
    public async Task LongInstruction_WrapsToTerminalWidth_AndNavigationReachesEnd(int width, int height)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddRazorConsoleServices();
        var viewport = new Viewport(width, height);
        services.AddSingleton<ITerminalViewport>(viewport);
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PlanStepEditor editor = default!;
            var original = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Line {i}: Review the complete instruction and preserve every requested change."));
            var coordinator = new InstructionPromptCoordinator();
            var pending = coordinator.RequestAsync("Edit step", original, multiline: true);
            RenderFragment fragment = b =>
            {
                b.OpenComponent<PlanStepEditor>(0);
                b.AddAttribute(1, "InitialValue", original);
                b.AddAttribute(2, "OnCancel", EventCallback.Factory.Create(this, coordinator.Cancel));
                b.AddAttribute(3, "OnSubmit", EventCallback.Factory.Create<string>(this, coordinator.Submit));
                b.AddComponentReferenceCapture(4, instance => editor = (PlanStepEditor)instance);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["ChildContent"] = fragment }));
            var buffer = (TextSelectionState)typeof(PlanStepEditor).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            int Dimension(string name) => (int)typeof(PlanStepEditor).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            var preview = typeof(PlanStepEditor).GetMethod("PreviewKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Key(string key, bool ctrl = false) => Assert.True(await (Task<bool>)preview.Invoke(editor, new object[] { new KeyboardEventArgs { Key = key, CtrlKey = ctrl } })!);
            Assert.Equal(original, buffer.Text);
            Assert.Equal(width - 4, Dimension("EditorWidth"));
            Assert.InRange(Dimension("EditorHeight"), 3, height - 7);
            Assert.Contains("Line 1", root.ToHtmlString());
            await Key("ArrowDown");
            Assert.True(buffer.Cursor > 0);
            await Key("PageDown");
            Assert.True(buffer.Offset > 0);
            await Key("End", ctrl: true);
            Assert.Equal(original.Length, buffer.Cursor);
            Assert.Contains("Line 30", root.ToHtmlString());
            await Key("Home", ctrl: true);
            Assert.Equal(0, buffer.Cursor);
            Assert.Equal(0, buffer.Offset);
            viewport.Width += 20;
            await Key("ArrowDown");
            Assert.Equal(viewport.Width - 4, Dimension("EditorWidth"));
            buffer.Insert("updated ");
            await Key("Escape");
            Assert.Equal(original, await pending);
            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.Multiline);
        });
    }

    [Fact]
    public async Task MultilineSubmission_PreservesNewlines_AndNextPromptUsesDefaultMode()
    {
        var coordinator = new InstructionPromptCoordinator();
        var pending = coordinator.RequestAsync("Edit", "first\nsecond", multiline: true);
        coordinator.Submit("first\nrevised second\nthird");
        Assert.Equal("first\nrevised second\nthird", await pending);
        pending = coordinator.RequestAsync("New instructions");
        Assert.False(coordinator.Multiline);
        coordinator.Submit("done");
        Assert.Equal("done", await pending);
    }

    private sealed class Viewport(int width, int height) : ITerminalViewport
    {
        public int Width { get; set; } = width;
        public int Height => height;
        public event Action? OnResized { add { } remove { } }
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
    }
}
