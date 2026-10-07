using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Input;
using Xunit;

namespace MandoCode.Tests;
public sealed class PromptCursorNavigationTests
{
    [Fact]
    public void DesiredColumnSurvivesShortLinesAndSelectionUsesTheSameMovement()
    {
        var buffer = new TextSelectionState(); buffer.SetText("abcdef\nx\nabcdef"); buffer.Begin(14, 1, false); buffer.End();
        var navigation = new PromptCursorNavigation();
        Assert.True(navigation.Move(buffer, 80, 2, -1, false)); Assert.Equal(8, buffer.Cursor);
        Assert.True(navigation.Move(buffer, 80, 2, -1, false)); Assert.Equal(5, buffer.Cursor);
        Assert.False(navigation.Move(buffer, 80, 2, -1, false));
        Assert.True(navigation.Move(buffer, 80, 2, 1, true)); Assert.Equal("f\nx", buffer.Selection);
        Assert.True(navigation.Move(buffer, 80, 2, 1, true)); Assert.Equal(14, buffer.Cursor); Assert.Equal(5, buffer.Anchor);
        Assert.Equal(1, buffer.Offset);
    }
    [Fact]
    public void SoftWrappedRowsNavigateWithoutGettingStuckAtASharedBoundary()
    {
        var buffer = new TextSelectionState(); buffer.SetText("abcdefghij"); buffer.Begin(8, 1, false); buffer.End();
        var navigation = new PromptCursorNavigation();
        Assert.True(navigation.Move(buffer, 4, 2, -1, false)); Assert.Equal(4, buffer.Cursor);
        Assert.True(navigation.Move(buffer, 4, 2, -1, false)); Assert.Equal(0, buffer.Cursor);
        Assert.False(navigation.Move(buffer, 4, 2, -1, false));
        buffer.Begin(10, 1, false); buffer.End();
        Assert.True(navigation.Move(buffer, 4, 2, -1, false)); Assert.Equal(6, buffer.Cursor);
    }
    [Fact]
    public void WideCharactersUseDisplayColumns()
    {
        var buffer = new TextSelectionState(); buffer.SetText("界a\n12345"); buffer.Begin(8, 1, false); buffer.End();
        Assert.True(new PromptCursorNavigation().Move(buffer, 80, 3, -1, false)); Assert.Equal(2, buffer.Cursor);
    }
    [Fact]
    public async Task PromptArrowsScrollConversationOnlyAtEditorBoundaries()
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => {
            PromptInput input = null!; var scroll = new List<string>();
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> {
                ["Capture"] = (Action<PromptInput>)(p => input = p), ["Scroll"] = (Action<string>)(s => scroll.Add(s))
            }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var composer = (PromptComposerState)typeof(PromptInput).GetField("_composer", flags)!.GetValue(input)!;
            composer.SetText("first\nsecond\nthird");
            Task<bool> Key(string key, bool shift = false) => (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", flags)!.Invoke(input, [new KeyboardEventArgs { Key = key, ShiftKey = shift }])!;
            Assert.True(await Key("ArrowUp")); Assert.Empty(scroll); Assert.Equal(11, composer.Buffer.Cursor);
            await Key("ArrowUp"); Assert.Empty(scroll); Assert.Equal(5, composer.Buffer.Cursor);
            await Key("ArrowUp"); Assert.Equal("ArrowUp", Assert.Single(scroll));
            await Key("ArrowDown"); await Key("ArrowDown"); Assert.Single(scroll);
            await Key("ArrowDown"); Assert.Equal(new[] { "ArrowUp", "ArrowDown" }, scroll);
            await Key("ArrowDown", true); Assert.Equal(2, scroll.Count);
        });
    }
    public sealed class Host : ComponentBase {
        [Parameter] public Action<PromptInput>? Capture { get; set; }
        [Parameter] public Action<string>? Scroll { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder) {
            builder.OpenComponent<PromptInput>(0);
            builder.AddAttribute(1, "OnScroll", EventCallback.Factory.Create<string>(this, s => Scroll?.Invoke(s)));
            builder.AddComponentReferenceCapture(2, p => Capture?.Invoke((PromptInput)p)); builder.CloseComponent();
        }
    }
}