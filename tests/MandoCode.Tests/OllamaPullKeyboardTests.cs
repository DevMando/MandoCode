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

[Trait("Category", "Component")]
public class OllamaPullKeyboardTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [Theory]
    [InlineData("Escape")]
    [InlineData("Enter")]
    public async Task SortButtonsAreKeyboardReachableAndOperationDoesNotBlockCancel(string cancelKey)
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        TestPanel panel = null!;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            void Set(string name, object value) => typeof(OllamaPullPanel).GetField(name, Flags)!.SetValue(panel, value);
            T Get<T>(string name) => (T)typeof(OllamaPullPanel).GetField(name, Flags)!.GetValue(panel)!;
            Task Key(string key, bool shift = false) => (Task)typeof(OllamaPullPanel).GetMethod("Key", Flags)!.Invoke(panel, [new KeyboardEventArgs { Key = key, ShiftKey = shift }])!;
            Assert.Equal("Newest", Get<string>("_sort"));
            Assert.Equal(2, Get<int>("_sortIndex"));
            Set("_newestModels", new OllamaModelLibrary.Model[] { new("test", "", "") });
            await Key("Tab", true); // list -> sort
            Assert.True(Get<bool>("_sortFocused"));
            await Key("ArrowLeft"); await Key("ArrowRight");
            Assert.Equal(2, Get<int>("_sortIndex"));
            await Key("Enter"); Assert.Equal("Newest", Get<string>("_sort"));
            await Key("ArrowRight"); Assert.Equal(3, Get<int>("_sortIndex"));
            await Key("Enter"); Assert.True(Get<bool>("_cloudOnly"));
            await Key("Enter"); Assert.False(Get<bool>("_cloudOnly"));
            await Key("Tab"); Assert.False(Get<bool>("_sortFocused"));
            await Key("Tab"); Assert.True(Get<bool>("_searchFocused"));
            await Key("Tab"); Assert.True(Get<bool>("_sortFocused"));

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> operation = async ct => { try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.TrySetResult(); } };
            var start = (Task)typeof(OllamaPullPanel).GetMethod("Run", Flags)!.Invoke(panel, [operation])!;
            Assert.True(start.IsCompletedSuccessfully); // Input event returns while work continues.
            Assert.True(Get<bool>("_busy"));
            await Key(cancelKey);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        });
        // Drain the cancellation continuation on the renderer dispatcher.
        await renderer.Dispatcher.InvokeAsync(() => { });

    }
    public class TestPanel : OllamaPullPanel
    {
        protected override void OnInitialized() { } // No catalog request in a keyboard regression test.
    }
    public class Host : ComponentBase
    {
        [Parameter] public Action<TestPanel>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TestPanel>(0);
            builder.AddComponentReferenceCapture(1, component => Capture?.Invoke((TestPanel)component));
            builder.CloseComponent();
        }
    }
}
