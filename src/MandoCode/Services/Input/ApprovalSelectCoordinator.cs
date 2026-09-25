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

    /// <summary>One line of the scrollable preview shown above the menu: plain text (never parsed
    /// as markup) with its color and decoration.</summary>
    public sealed record PreviewLine(string Text, Color Color, Decoration Decoration = Decoration.None);

    private TaskCompletionSource<string>? _tcs;
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public IReadOnlyList<Option> Options { get; private set; } = Array.Empty<Option>();

    /// <summary>What the menu is deciding on, when it's too long to print above the menu — a long
    /// diff. Empty when the details were printed to scrollback instead.</summary>
    public IReadOnlyList<PreviewLine> Preview { get; private set; } = Array.Empty<PreviewLine>();

    /// <summary>Title for <see cref="Preview"/>.</summary>
    public string? PreviewTitle { get; private set; }

    /// <summary>
    /// Fires when <see cref="IsActive"/> changes so App.razor can re-render.
    /// </summary>
    public event Action? StateChanged;

    public Task<string> RequestAsync(
        IReadOnlyList<Option> options,
        IReadOnlyList<PreviewLine>? preview = null,
        string? previewTitle = null)
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
            Preview = preview ?? Array.Empty<PreviewLine>();
            PreviewTitle = previewTitle;
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
            Preview = Array.Empty<PreviewLine>();
            PreviewTitle = null;
        }

        StateChanged?.Invoke();
        tcs?.TrySetResult(value);
    }
}
