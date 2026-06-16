using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Bridges the imperative <see cref="DiffApprovalHandler"/> approval menus to the VDOM
/// <c>ApprovalSelect</c> component hosted by <c>App.razor</c>. The handler calls
/// <see cref="RequestAsync"/> with its option set and awaits the user's choice; App.razor
/// reads <see cref="IsActive"/>/<see cref="Options"/> to render the menu and calls
/// <see cref="Submit"/> with the chosen option's text when the user presses Enter.
///
/// Mirrors <see cref="InstructionPromptCoordinator"/>, and exists for the same reason: to
/// stop routing approval menus through Spectre's blocking <c>SelectionPrompt</c>. Spectre's
/// <c>Console.ReadKey</c> races RazorConsole's keyboard pump (KeyboardEventManager polls
/// KeyAvailable every 50ms and reads unconditionally), which intermittently swallows the
/// arrow/Enter keys the prompt is waiting for. When that race is lost the prompt — and the
/// whole turn — hangs with the spinner already stopped: a silent freeze. The plan-approval
/// menu was migrated to the VDOM <c>ApprovalSelect</c> for exactly this, but the
/// diff / command / delete / MCP menus were not, which is the freeze that surfaces a few
/// steps into a plan once the model starts writing files.
/// </summary>
public sealed class ApprovalSelectCoordinator
{
    /// <summary>A selectable entry: display text plus its palette color.</summary>
    public sealed record Option(string Text, Color Color);

    private TaskCompletionSource<string>? _tcs;
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public IReadOnlyList<Option> Options { get; private set; } = Array.Empty<Option>();

    /// <summary>
    /// Fires when <see cref="IsActive"/> changes so App.razor can re-render.
    /// </summary>
    public event Action? StateChanged;

    public Task<string> RequestAsync(IReadOnlyList<Option> options)
    {
        TaskCompletionSource<string> tcs;
        lock (_gate)
        {
            // Last writer wins. ApprovalPromptGate already serializes approvals so a
            // second concurrent request shouldn't arrive, but this keeps state consistent
            // (and the abandoned awaiter unblocked) if one ever does.
            _tcs?.TrySetResult(string.Empty);

            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = tcs;
            Options = options;
            IsActive = true;
        }

        StateChanged?.Invoke();
        return tcs.Task;
    }

    public void Submit(string value)
    {
        TaskCompletionSource<string>? tcs;
        lock (_gate)
        {
            tcs = _tcs;
            _tcs = null;
            IsActive = false;
            Options = Array.Empty<Option>();
        }

        StateChanged?.Invoke();
        tcs?.TrySetResult(value);
    }
}
