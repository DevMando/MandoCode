using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
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
public class AgentSettingsKeyboardTests
{
    [Fact]
    public async Task ModelListUsesSettingsHeight_AndKeyboardSelectsOrCancels()
    {
        var config = new MandoCodeConfig { ModelName = "test:cloud", AllowPersistence = false };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        Program.RegisterAgentServices(registrations, config, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AgentSettingsPanel panel = null!;
            var picker = new ModelPickerState(Enumerable.Range(0, 15).Select(i => $"model-{i}").ToArray());
            string? selected = null;
            var cancelled = false;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Config"] = config, ["Scope"] = new ExplorerFocusScope { Active = true },
                ["Capture"] = (Action<AgentSettingsPanel>)(value => panel = value),
                ["Picker"] = picker,
                ["SelectModel"] = (Action<string>)(value => selected = value),
                ["CancelModel"] = (Action)(() => cancelled = true)
            }));
            Assert.Contains("model-14", rendered.ToHtmlString());
            Assert.DoesNotContain("Save to Global Defaults", rendered.ToHtmlString());
            async Task Key(string key) => await (Task)typeof(AgentSettingsPanel).GetMethod("Key", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(panel, new object[] { new KeyboardEventArgs { Key = key } })!;
            await Key("ArrowDown");
            Assert.Equal(1, picker.SelectedIndex);
            await Key("Enter");
            Assert.Equal("model-1", selected);
            await Key("Escape");
            Assert.True(cancelled);
        });
    }

    [Fact]
    public async Task TabStaysInSettings_AndPresetChoicesUseTheSameFocusScope()
    {
        var config = new MandoCodeConfig { ModelName = "test:cloud", AllowPersistence = false };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        Program.RegisterAgentServices(registrations, config, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var scope = new ExplorerFocusScope { Active = true };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            AgentSettingsPanel panel = null!;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Config"] = config, ["Scope"] = scope, ["Capture"] = (Action<AgentSettingsPanel>)(value => panel = value) }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            T Value<T>(string name) => (T)typeof(AgentSettingsPanel).GetField(name, flags)!.GetValue(panel)!;
            async Task Key(string key) => await (Task)typeof(AgentSettingsPanel).GetMethod("Key", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = key } })!;
            Assert.NotNull(scope.NavigateTab);
            var dirty = typeof(AgentSettingsPanel).GetProperty("HasChanges", flags)!;
            Assert.False((bool)dirty.GetValue(panel)!);
            var draft = Value<AgentSettingsDraft>("_draft");
            var original = draft.Config.EnableDiffApprovals;
            draft.Config.EnableDiffApprovals = !original;
            Assert.True((bool)dirty.GetValue(panel)!);
            await (Task)typeof(AgentSettingsPanel).GetMethod("RequestClose", flags)!.Invoke(panel, null)!;
            Assert.True(Value<bool>("_confirmDiscard"));
            await Key("Escape");
            Assert.False(Value<bool>("_confirmDiscard"));
            draft.Config.EnableDiffApprovals = original;
            Assert.False((bool)dirty.GetValue(panel)!);
            Assert.Contains("Apply Global Defaults", rendered.ToHtmlString());
            Assert.Contains("Save to Global Defaults", rendered.ToHtmlString());
            await scope.ToggleAsync(); Assert.Equal(1, Value<int>("_selected"));
            await scope.ToggleAsync(true); Assert.Equal(0, Value<int>("_selected"));
            await Key("Tab"); await Key("Enter");
            Assert.Equal("temperature", Value<AgentSettingsDraft.Field>("_editing").Key);
            var choices = (string[])typeof(AgentSettingsPanel).GetProperty("Presets", flags)!.GetValue(panel)!;
            var current = Array.IndexOf(choices, config.Temperature.ToString());
            Assert.Equal(current, Value<int>("_selected"));
            Assert.Contains(0.7.ToString(), choices);
            await scope.ToggleAsync(); Assert.Equal(current + 1, Value<int>("_selected"));
            await scope.ToggleAsync(true); Assert.Equal(current, Value<int>("_selected"));
            await Key("Enter"); Assert.Null(Value<AgentSettingsDraft.Field?>("_editing"));
            Assert.Equal(1, Value<int>("_selected"));
            await Key("Enter");
            var presets = (string[])typeof(AgentSettingsPanel).GetProperty("Presets", flags)!.GetValue(panel)!;
            await (Task)typeof(AgentSettingsPanel).GetMethod("Activate", flags)!.Invoke(panel, new object[] { presets.Length })!;
            Assert.True(Value<bool>("_custom"));
            typeof(AgentSettingsPanel).GetMethod("SubmitValue", flags)!.Invoke(panel, new object[] { "2.0" });
            Assert.NotEmpty(Value<string>("_status"));
            Assert.NotNull(Value<AgentSettingsDraft.Field?>("_editing"));
            await scope.ToggleAsync();
            Assert.False(Value<bool>("_custom"));
            Assert.NotNull(Value<AgentSettingsDraft.Field?>("_editing"));
        });
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public MandoCodeConfig Config { get; set; } = default!;
        [Parameter] public ExplorerFocusScope Scope { get; set; } = default!;
        [Parameter] public Action<AgentSettingsPanel>? Capture { get; set; }
        [Parameter] public ModelPickerState? Picker { get; set; }
        [Parameter] public Action<string>? SelectModel { get; set; }
        [Parameter] public Action? CancelModel { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<ExplorerFocusScope>>(0);
            builder.AddAttribute(1, "Value", Scope);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(content =>
            {
                content.OpenComponent<AgentSettingsPanel>(0);
                content.AddAttribute(1, "Config", Config);
                content.AddAttribute(3, "ModelPicker", Picker);
                content.AddAttribute(4, "OnModelSelected", EventCallback.Factory.Create<string>(this, value => SelectModel?.Invoke(value)));
                content.AddAttribute(5, "OnModelCancel", EventCallback.Factory.Create(this, () => CancelModel?.Invoke()));
                content.AddComponentReferenceCapture(2, value => Capture?.Invoke((AgentSettingsPanel)value));
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
