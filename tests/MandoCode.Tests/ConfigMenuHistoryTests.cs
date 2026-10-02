using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MandoCode.Tests;

#pragma warning disable BL0006
[Collection("TUI console routing")]
public class ConfigMenuHistoryTests
{
    [Theory]
    [InlineData("Cancel", true, 0)]
    [InlineData("View current configuration", true, 2)]
    [InlineData("Cancel", false, 2)]
    public async Task CancelledConfig_AddsNoMenuHistory_WhileActionsAndOrdinaryMenusStillRecordChoices(string choice, bool temporary, int addedEntries)
    {
        var services = new ServiceCollection().AddLogging();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var session = new TuiSession();
        session.AppendUserPrompt("/config");
        using var output = TuiConsole.Begin(session);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var app = new TestApp();
            var root = renderer.Attach(app);
            await renderer.Render(root);
            Task pending = temporary && choice == "Cancel" ? app.Config() : app.Choose(temporary);
            Assert.Equal(temporary ? 1 : 2, session.Snapshot().Entries.Count);
            Assert.True(app.Active);
            if (temporary) Assert.Equal("Configuration Options:", app.Title);
            app.Submit(choice);
            await pending;
            Assert.Equal(1 + addedEntries, session.Snapshot().Entries.Count);
            Assert.Equal("/config", session.Snapshot().Entries[0].UserPrompt);
            Assert.False(app.Active);
            Assert.Null(app.Title);
            Assert.True(app.PromptVisible);
        });
    }

    private sealed class TestApp : App, IDisposable
    {
        void IDisposable.Dispose() { }
        public bool Active => Field<bool>("_showWizardSelect");
        public bool PromptVisible => Field<bool>("_showPrompt");
        public string? Title => Field<string?>("_wizardSelectTitle");
        private T Field<T>(string name) => (T)typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public Task Config() => (Task)typeof(App).GetMethod("HandleConfigCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
        public Task<string> Choose(bool temporary) => (Task<string>)typeof(App).GetMethod("WizardPromptSelectCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this, new object?[] { "Configuration Options:", new[] { "View current configuration", "Cancel" }, temporary ? "Cancel" : null })!;
        public void Submit(string choice) => typeof(App).GetMethod("HandleWizardSelectSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object[] { choice });
        protected override void OnInitialized() => typeof(App).GetField("_showPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, true);
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Active ? Title ?? "menu" : "prompt");
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public Task Render(int id) => RenderRootComponentAsync(id);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Test render failed", exception);
    }
}
