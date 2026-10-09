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
[Trait("Category", "Component")]
public class SetupPromptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupModelSelection_UsesHostPicker_AndReturnsSelectionOrCancellation(bool cancel)
    {
        string[]? offered = null;
        var flow = new OnboardingFlow(_ => { }, pickModelVdom: models =>
        {
            offered = models;
            return Task.FromResult(cancel ? null : "alpha:latest");
        });
        var pick = typeof(OnboardingFlow).GetMethod("PickModelAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<string?>)pick.Invoke(flow,
            new object[] { "http://unused", new List<string> { "zeta:latest", "alpha:latest" }, CancellationToken.None })!;
        Assert.Equal(new[] { "alpha:latest", "zeta:latest" }, offered);
        Assert.Equal(cancel ? null : "alpha:latest", result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task WizardText_RestoresPromptVisibility_AfterSubmissionOrFailure(bool visible, bool fail)
    {
        var services = new ServiceCollection().AddLogging();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var app = new TestApp();
            var root = renderer.Attach(app);
            await renderer.Render(root);
            app.Set("_showPrompt", visible);
            var pending = app.Ask(fail ? _ => throw new InvalidOperationException("validation failed") : null);
            Assert.Equal("wizard", renderer.Text(root));
            Assert.False(app.Get<bool>("_showPrompt"));
            app.Submit("http://localhost:11434");
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            else Assert.Equal("http://localhost:11434", await pending);
            Assert.Equal(visible, app.Get<bool>("_showPrompt"));
            Assert.False(app.Get<bool>("_showWizardInput"));
            Assert.Equal(visible ? "prompt" : "hidden", renderer.Text(root));
        });
    }

    [Fact]
    public async Task WizardSelection_RemovesMenu_AndRestoresPrompt()
    {
        var services = new ServiceCollection().AddLogging();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var app = new TestApp();
            var root = renderer.Attach(app);
            await renderer.Render(root);
            app.Set("_showPrompt", true);
            var pending = app.Choose();
            Assert.Equal("menu", renderer.Text(root));
            app.SubmitChoice("View current configuration");
            Assert.Equal("View current configuration", await pending);
            Assert.False(app.Get<bool>("_showWizardSelect"));
            Assert.True(app.Get<bool>("_showPrompt"));
            Assert.Equal("prompt", renderer.Text(root));
        });
    }

    [Fact]
    public async Task SecretWizardInput_ClearsMaskState_WhenDismissed()
    {
        var services = new ServiceCollection().AddLogging();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var app = new TestApp();
            var root = renderer.Attach(app);
            await renderer.Render(root);
            var pending = app.AskSecret();
            Assert.True(app.Get<bool>("_wizardInputSecret"));
            app.Submit("");
            Assert.Equal("", await pending);
            Assert.False(app.Get<bool>("_wizardInputSecret"));
            Assert.False(app.Get<bool>("_showWizardInput"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Learn_ReturnsToPrompt_WhenOfflineOrEducatorDeclined(bool connected)
    {
        var services = new ServiceCollection().AddLogging();
        using var provider = services.BuildServiceProvider();
        using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var app = new TestApp();
            var root = renderer.Attach(app);
            await renderer.Render(root);
            app.Set("_showPrompt", true);
            app.SetConnection(connected);
            await renderer.Render(root);
            var pending = app.Learn();
            if (connected)
            {
                Assert.False(pending.IsCompleted);
                Assert.Equal("menu", renderer.Text(root));
                app.SubmitChoice("No");
            }
            await pending;
            Assert.True(app.Get<bool>("_showPrompt"));
            Assert.False(app.Get<bool>("_showWizardSelect"));
            Assert.Equal("prompt", renderer.Text(root));
        });
    }

    public sealed class TestApp : App, IDisposable
    {
        void IDisposable.Dispose() { }
        public void SetConnection(bool connected) => _isConnected = connected;
        public void Set(string name, object value) => typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        public T Get<T>(string name) => (T)typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public Task<string> Ask(Func<string, string?>? validate) => (Task<string>)typeof(App)
            .GetMethod("WizardPromptTextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this, new object?[] { "Ollama URL", "", validate, null })!;
        public void Submit(string value) => typeof(App).GetMethod("HandleWizardInputSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object[] { value });
        public Task<string> Choose() => (Task<string>)typeof(App).GetMethod("WizardPromptSelectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this, new object[] { "Configuration options", new[] { "Run configuration wizard", "View current configuration", "Cancel" } })!;
        public void SubmitChoice(string value) => typeof(App).GetMethod("HandleWizardSelectSubmit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, new object[] { value });
        public Task<string> AskSecret() => (Task<string>)typeof(App).GetMethod("WizardPromptTextCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(this, new object?[] { "API key", "", null, null, true })!;
        public Task Learn() => (Task)typeof(App).GetMethod("HandleLearnCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, null)!;
        protected override void OnInitialized() { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, Get<bool>("_showWizardInput") ? "wizard" : Get<bool>("_showWizardSelect") ? "menu" : Get<bool>("_showPrompt") ? "prompt" : "hidden");
    }

    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory logs) : Renderer(services, logs)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public Task Render(int id) => RenderRootComponentAsync(id);
        public string Text(int id) => GetCurrentRenderTreeFrames(id).Array[0].TextContent;
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Test render failed", exception);
    }
}
