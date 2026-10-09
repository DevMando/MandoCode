using System.Reflection;
using MandoCode.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Spectre.Console;
using Xunit;

// This harness deliberately inspects renderer frames and dispatches real UI events.
#pragma warning disable BL0006

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public class PlanChoiceTests
{
    [Fact]
    public async Task RecoveryAndRevisedPlanChoices_RenderOneMenu_AndRemoveItAfterSelectionOrCancellation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorConsoleServices();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var app = new TestApp();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rootId = renderer.Attach(app);
            await renderer.Render(rootId);
            var recovery = new[]
            {
                new ApprovalSelect.Option("Retry this step", Color.Green),
                new ApprovalSelect.Option("Revise the remaining plan", Color.DeepSkyBlue1),
                new ApprovalSelect.Option("Skip this step and continue", Color.Yellow),
                new ApprovalSelect.Option("Cancel the plan", Color.Red)
            };
            var pending = app.Choose("How would you like to proceed?", recovery, CancellationToken.None);
            Assert.Equal(1, renderer.MenuCount(rootId));
            var keyHandler = renderer.KeyHandler(rootId);
            await renderer.DispatchEventAsync(keyHandler, null, new KeyboardEventArgs { Key = "ArrowDown" });
            await renderer.DispatchEventAsync(keyHandler, null, new KeyboardEventArgs { Key = "Enter" });
            Assert.Equal("Revise the remaining plan", await pending);
            Assert.Equal(0, renderer.MenuCount(rootId));

            var revised = new[]
            {
                new ApprovalSelect.Option("Use revised plan", Color.Green),
                new ApprovalSelect.Option("Edit a step", Color.DeepSkyBlue1)
            };
            pending = app.Choose("Review the revised remaining plan", revised, CancellationToken.None);
            Assert.Equal(1, renderer.MenuCount(rootId));
            keyHandler = renderer.KeyHandler(rootId);
            // A replacement menu starts at the first option, rather than retaining
            // the previous recovery selection or leaving that old menu on screen.
            await renderer.DispatchEventAsync(keyHandler, null, new KeyboardEventArgs { Key = "Enter" });
            Assert.Equal("Use revised plan", await pending);
            Assert.Equal(0, renderer.MenuCount(rootId));

            using var cancellation = new CancellationTokenSource();
            pending = app.Choose("How would you like to proceed?", recovery, cancellation.Token);
            Assert.Equal(1, renderer.MenuCount(rootId));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, renderer.MenuCount(rootId));
        });
    }

    // Mount the actual App choice lifecycle without initializing AI/network services.
    private sealed class TestApp : App, IDisposable
    {
        // AI services are intentionally absent in this render-only harness.
        void IDisposable.Dispose() { }
        private static readonly FieldInfo Active = typeof(App).GetField("_planSelectActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo Options = typeof(App).GetField("_planSelectOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo ChooseMethod = typeof(App).GetMethod("PromptPlanChoiceAsync", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(IReadOnlyList<ApprovalSelect.Option>), typeof(CancellationToken) }, null)!;
        private static readonly MethodInfo Submit = typeof(App).GetMethod("HandlePlanSelectSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        public Task<string> Choose(string title, IReadOnlyList<ApprovalSelect.Option> options, CancellationToken ct) =>
            (Task<string>)ChooseMethod.Invoke(this, new object[] { title, options, ct })!;
        protected override void OnInitialized() { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!(bool)Active.GetValue(this)!) return;
            builder.OpenComponent<ApprovalSelect>(0);
            builder.AddAttribute(1, "Options", Options.GetValue(this));
            builder.AddAttribute(2, "OnSubmit", EventCallback.Factory.Create<string>(this, value => Submit.Invoke(this, new object[] { value })));
            builder.CloseComponent();
        }
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public Task Render(int id) => RenderRootComponentAsync(id);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Test render failed", exception);
        public int MenuCount(int id) => GetCurrentRenderTreeFrames(id).Array
            .Take(GetCurrentRenderTreeFrames(id).Count).Count(f => f.FrameType == RenderTreeFrameType.Component && f.ComponentType == typeof(ApprovalSelect));
        public ulong KeyHandler(int rootId)
        {
            var root = GetCurrentRenderTreeFrames(rootId);
            var menuId = root.Array.Take(root.Count).Single(f => f.FrameType == RenderTreeFrameType.Component).ComponentId;
            var menu = GetCurrentRenderTreeFrames(menuId);
            return menu.Array.Take(menu.Count).Single(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onkeydown").AttributeEventHandlerId;
        }
    }
}
