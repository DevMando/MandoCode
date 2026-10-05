using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Xunit;

namespace MandoCode.Tests;

public sealed class SetupWizardKeyboardTests
{
    [Fact]
    public async Task TabAndArrowsSelectAndEscapeReturnsBackWithinWizard()
    {
        var registrations = new ServiceCollection().AddLogging(); registrations.AddRazorConsoleServices();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            TestPanel panel = null!;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<TestPanel>)(p => panel = p) }));
            var type = typeof(SetupWizardPanel);
            var choose = (Task<string?>)type.GetMethod("Choose", flags)!.Invoke(panel, ["Choose model", "Select a starter", new[] { "First", "Second", "Back" }])!;
            Task Key(string key) => (Task)type.GetMethod("Key", flags)!.Invoke(panel, [new KeyboardEventArgs { Key = key }])!;
            await Key("Tab"); await Key("Enter");
            Assert.Equal("Second", await choose);
            var next = (Task<string?>)type.GetMethod("Choose", flags)!.Invoke(panel, ["Choose model", "Select a starter", new[] { "First", "Back" }])!;
            await Key("Escape"); Assert.Equal("Back", await next);
            Assert.Contains("Connect", rendered.ToHtmlString());
            var url = (Task<string?>)type.GetMethod("AskUrl", flags)!.Invoke(panel, ["http://localhost:11434"])!;
            type.GetMethod("SubmitUrl", flags)!.Invoke(panel, ["not a URL"]);
            Assert.False(url.IsCompleted);
            type.GetMethod("SubmitUrl", flags)!.Invoke(panel, ["https://remote:11434/"]);
            Assert.Equal("https://remote:11434", await url);
        });
    }
    public sealed class TestPanel : SetupWizardPanel { protected override void OnInitialized() { } }
    public sealed class Host : ComponentBase
    {
        [Parameter] public Action<TestPanel>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TestPanel>(0);
            builder.AddAttribute(1, "Config", new MandoCodeConfig { AllowPersistence = false });
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((TestPanel)value));
            builder.CloseComponent();
        }
    }
}
