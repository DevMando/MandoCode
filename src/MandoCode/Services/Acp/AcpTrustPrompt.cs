using Spectre.Console;

namespace MandoCode.Services.Acp;

/// <summary>
/// The trust question inside the TUI: prints the launch plan (as plain text, never markup) and asks through the
/// panel's <see cref="ApprovalSelectCoordinator"/>, serialized behind the panel's <see cref="ApprovalPromptGate"/>.
/// </summary>
public sealed class AcpTrustPrompt
{
    public const string TrustLabel = "Trust and start";
    public const string CancelLabel = "Cancel";

    private readonly ApprovalPromptGate _gate;
    private readonly ApprovalSelectCoordinator _menu;
    private readonly Func<IDisposable> _suppressKeys;
    private readonly Action<string> _writeLine;

    public AcpTrustPrompt(ApprovalPromptGate gate, ApprovalSelectCoordinator menu, Func<IDisposable> suppressKeys, Action<string> writeLine)
    {
        _gate = gate;
        _menu = menu;
        _suppressKeys = suppressKeys;
        _writeLine = writeLine;
    }

    public async Task<bool> ConfirmAsync(AcpLaunchPlan plan)
    {
        using var hold = await _gate.AcquireAsync();

        _writeLine("");
        foreach (var line in plan.Describe())
            _writeLine(line);
        _writeLine("");

        using (_suppressKeys())
        {
            var choice = await _menu.RequestAsync(new List<ApprovalSelectCoordinator.Option>
            {
                new(TrustLabel, Color.Green),
                new(CancelLabel, new Color(255, 200, 80)),
            });
            return choice == TrustLabel;
        }
    }
}
