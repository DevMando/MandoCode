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

public class ModelPickerTests
{
    [Fact]
    public void FilteringAndNavigation_HandleEmptyResultsAndWraparound()
    {
        var picker = new ModelPickerState(new[] { "alpha (local)", "beta (cloud)", "gamma (local)" });
        picker.Move(-1);
        Assert.Equal("gamma (local)", picker.Selected);
        picker.Move(1);
        Assert.Equal("alpha (local)", picker.Selected);
        picker.Filter("CLOUD");
        Assert.Equal("beta (cloud)", picker.Selected);
        picker.Filter("missing");
        picker.Move(1);
        Assert.Null(picker.Selected);
        picker.Filter("");
        Assert.Equal(3, picker.Matches.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptKeyboard_HandlesModelSelectionAndCancel_EvenWhileChatIsProcessing(bool cancel)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorConsoleServices();
        services.AddSingleton(new TuiSession());
        services.AddSingleton(new InputStateMachine(new Dictionary<string, string>(), null));
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var picker = new ModelPickerState(new[] { "alpha (local)", "beta (cloud)" });
        PromptInput prompt = default!;
        string? selected = null;
        var cancelled = false;
        var chatSubmissions = 0;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment footer = b =>
            {
                b.OpenComponent<PromptInput>(0);
                b.AddAttribute(1, "Disabled", true);
                b.AddAttribute(2, "ModelPicker", picker);
                b.AddAttribute(3, "OnModelSelected", EventCallback.Factory.Create<string>(this, value => selected = value));
                b.AddAttribute(4, "OnModelPickerCancel", EventCallback.Factory.Create(this, () => cancelled = true));
                b.AddAttribute(5, "OnSubmit", EventCallback.Factory.Create<string>(this, _ => chatSubmissions++));
                b.AddComponentReferenceCapture(6, instance => prompt = (PromptInput)instance);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Footer"] = footer }));
            var preview = typeof(PromptInput).GetMethod("PreviewKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task<bool> Key(string key) => await (Task<bool>)preview.Invoke(prompt, new object[] { new KeyboardEventArgs { Key = key } })!;
            Assert.True(await Key("ArrowDown"));
            Assert.Equal("beta (cloud)", picker.Selected);
            Assert.Contains("Models", root.ToHtmlString());
            Assert.True(await Key(cancel ? "Escape" : "Enter"));
            Assert.Equal(cancel, cancelled);
            Assert.Equal(cancel ? null : "beta (cloud)", selected);
            Assert.Equal(0, chatSubmissions);
            if (!cancel)
            {
                selected = null;
                picker.Filter("no match");
                Assert.True(await Key("Enter"));
                Assert.Null(selected);
            }
        });
    }

    [Theory]
    [InlineData(32, 12)]
    [InlineData(20, 10)]
    public async Task ModelPopup_ReservesComposerAtBottom(int width, int height)
    {
        var picker = new ModelPickerState(Enumerable.Range(0, 30).Select(i => $"model-{i} (local)").ToArray());
        var frame = await TuiLayoutTests.Frame(new TuiSession(), width, height, disabled: true, metadata: true, modelPicker: picker);
        Assert.Contains("Models", frame);
        Assert.Contains("model-0", frame);
        Assert.Contains("╚", frame.TrimEnd().Split('\n')[^1]);
        Assert.True(frame.TrimEnd('\n').Split('\n').Length <= height);
    }
}