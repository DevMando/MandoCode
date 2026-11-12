using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RazorConsole.Core;
using RazorConsole.Core.Focus;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public sealed class PromptStartupRenderingTests
{
    [Fact]
    public async Task FocusAndRefocusKeepOnePromptInTheRenderTree()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        await using var services = registrations.BuildServiceProvider();
        PromptInput input = null!;
        await using var renderer = await WidgetRendererHarness.MountAsync<Host>(services, new()
        {
            [nameof(Host.Capture)] = (Action<PromptInput>)(value => input = value)
        });
        var hook = (string)typeof(PromptInput).GetField("_hook", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(input)!;
        var focus = services.GetRequiredService<FocusManager>();
        // The test renderer has no console host to deliver snapshots to the focus observer.
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            typeof(FocusManager).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(method => method.Name.EndsWith("OnNext")).Invoke(focus, [renderer.Snapshot()]);
            await input.FocusPromptAsync();
        });
        for (var iteration = 0; iteration < 5; iteration++)
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                input.SetValue($"Prompt {iteration}");
                await input.FocusPromptAsync();
            });
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                var nodes = WidgetRendererHarness.Flatten(WidgetRendererHarness.Root(renderer.Snapshot()));
                Assert.Single(nodes, node => node.Attributes.TryGetValue("data-focus-key", out var key) && key == hook);
                Assert.True(focus.IsFocused(hook));
            });
        }
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public Action<PromptInput> Capture { get; set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<PromptInput>(0);
            builder.AddComponentReferenceCapture(1, value => Capture((PromptInput)value));
            builder.CloseComponent();
        }
    }
}
