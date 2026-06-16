using ArdinCode.Models;
using Spectre.Console;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArdinCode.Services;

/// <summary>
/// First-run onboarding orchestrator.
/// </summary>
public sealed class OnboardingFlow
{
    private readonly Action<string> _setStatus;
    private readonly Func<string, string, Func<string, string?>?, string?, Task<string>>? _promptTextVdom;

    public OnboardingFlow(
        Action<string> setStatus,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        _setStatus = setStatus;
        _promptTextVdom = promptTextVdom;
    }

    public sealed record FlowResult(bool Connected, bool Skipped, string? FinalModel);

    /// <summary>
    /// Run the onboarding wizard.
    /// </summary>
    public async Task<FlowResult> RunAsync(ArdinCodeConfig config, bool forceInteractive = false, CancellationToken ct = default)
    {
        _setStatus("Running configuration setup...");
        
        // Always run the wizard to set up API Endpoint and Key
        var updatedConfig = await ConfigurationWizard.RunAsync(config, _promptTextVdom);
        
        _setStatus("");
        
        var probe = await ApiProviderSetupHelper.ProbeAsync(updatedConfig.ApiEndpoint, updatedConfig.ApiKey, ct);
        
        if (probe.Ok)
        {
            updatedConfig.HasCompletedOnboarding = true;
            updatedConfig.Save();
            return new FlowResult(Connected: true, Skipped: false, FinalModel: updatedConfig.GetEffectiveModelName());
        }
        else
        {
            // Even if connection failed, save so they don't have to keep retyping, but don't mark onboarding as complete
            updatedConfig.Save();
            return new FlowResult(Connected: false, Skipped: true, FinalModel: null);
        }
    }
}
