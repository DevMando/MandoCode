using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RazorConsole.Core;

namespace MandoCode.Tests.Acp;

/// <summary>The real pane services (workspace, scoped per-pane objects) without a terminal, for tests that need an <see cref="AgentPane"/>.</summary>
internal static class AcpTestHost
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    public static ServiceProvider Services(string? projectRoot = null)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<IHostApplicationLifetime, Lifetime>();
        Program.RegisterAgentServices(registrations,
            new MandoCodeConfig { EnableThemeCustomization = false, UseAgentNames = false, AllowPersistence = false },
            projectRoot ?? Path.GetTempPath());
        return registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
