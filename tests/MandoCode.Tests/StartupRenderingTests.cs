using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
[Trait("Category", "Component")]
public sealed class StartupRenderingTests
{
    [Fact]
    public async Task ConnectionStatusRender_DoesNotStartInputBeforeStartupFinishes()
    {
        await using var services = Services();
        StartupApp app = null!;
        await using var renderer = await Mount(services, value => app = value);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Set(app, "_connectionChecked", true);
            await app.AfterRender();
            Assert.False(Get<bool>(app, "_hasRendered"));
            Assert.Null(Get<object?>(app, "_inputTcs"));
            Assert.False(Get<bool>(app, "_showPrompt"));
        });
    }

    [Fact]
    public async Task ClosingDuringStartupDelay_DoesNotLaunchInputOnDisposedAgent()
    {
        await using var services = Services();
        StartupApp app = null!;
        await using var renderer = await Mount(services, value => app = value);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Set(app, "_connectionChecked", true);
            Set(app, "_startupComplete", true);
            var startup = app.AfterRender();
            app.Dispose();
            await startup;
            Assert.Null(Get<object?>(app, "_inputTcs"));
        });
    }

    [Fact]
    public async Task StartupAndRefreshes_KeepOnePromptAndInputOwner()
    {
        await using var services = Services();
        StartupApp app = null!;
        await using var renderer = await Mount(services, value => app = value);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Set(app, "_connectionChecked", true);
            Set(app, "_startupComplete", true);
            await app.AfterRender();
        });
        await renderer.WaitUntilAsync(() => Get<object?>(app, "_inputTcs") is not null);
        var input = Get<object>(app, "_inputTcs");
        for (var i = 0; i < 12; i++)
        {
            await renderer.Dispatcher.InvokeAsync(app.AfterRender);
            var snapshot = await renderer.Dispatcher.InvokeAsync(renderer.Snapshot);
            var nodes = WidgetRendererHarness.Flatten(WidgetRendererHarness.Root(snapshot));
            Assert.Single(nodes, node => node.Attributes.TryGetValue("data-focus-key", out var focusKey)
                && focusKey?.StartsWith("prompt-", StringComparison.Ordinal) == true);
            Assert.Same(input, Get<object>(app, "_inputTcs"));
        }
    }

    private static ServiceProvider Services()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations, new MandoCodeConfig
        {
            EnableThemeCustomization = false, AllowPersistence = false, UseAgentNames = false
        }, Path.GetTempPath());
        return registrations.BuildServiceProvider();
    }

    private static Task<WidgetRendererHarness> Mount(ServiceProvider services, Action<StartupApp> ready)
    {
        services.GetRequiredService<AgentWorkspace>().Add();
        RenderFragment<AgentPane> body = _ => builder =>
        {
            builder.OpenComponent<StartupApp>(0);
            builder.AddAttribute(1, nameof(StartupApp.Ready), ready);
            builder.CloseComponent();
        };
        return WidgetRendererHarness.MountAsync<AgentWorkspaceView>(services, new() { ["AgentBody"] = body });
    }

    private static T Get<T>(App app, string name) => (T)Field(name).GetValue(app)!;
    private static void Set(App app, string name, object value) => Field(name).SetValue(app, value);
    private static FieldInfo Field(string name) => typeof(App).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    public sealed class StartupApp : App
    {
        [Parameter] public Action<StartupApp> Ready { get; set; } = default!;
        protected override void OnInitialized() { base.OnInitialized(); Ready(this); }
        // Drive startup render reentry deterministically without probing a live model server.
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task AfterRender() => base.OnAfterRenderAsync(false);
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width => 120;
        public int Height => 40;
        public event Action? OnResized { add { } remove { } }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
