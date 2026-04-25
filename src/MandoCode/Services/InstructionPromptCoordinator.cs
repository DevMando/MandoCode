namespace MandoCode.Services;

/// <summary>
/// Bridges the imperative <see cref="DiffApprovalHandler"/> "Provide new instructions"
/// path to a VDOM <c>TextInput</c> hosted by <c>App.razor</c>. The handler calls
/// <see cref="RequestAsync"/> and awaits the user's input; App.razor reads
/// <see cref="IsActive"/>/<see cref="Prompt"/> to render the input and calls
/// <see cref="Submit"/> when the user presses Enter.
///
/// Using a Razor TextInput instead of Spectre's TextPrompt sidesteps the
/// Console.ReadKey race with the App-level Escape listener — the TextInput
/// reads keys via Blazor events, not Console.ReadKey.
/// </summary>
public sealed class InstructionPromptCoordinator
{
    private TaskCompletionSource<string>? _tcs;
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>
    /// Fires when <see cref="IsActive"/> changes so App.razor can re-render.
    /// </summary>
    public event Action? StateChanged;

    public Task<string> RequestAsync(string prompt)
    {
        TaskCompletionSource<string> tcs;
        lock (_gate)
        {
            // Cancel any in-flight request — last writer wins. Shouldn't happen
            // in normal flow but keeps state consistent if it does.
            _tcs?.TrySetResult(string.Empty);

            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcs = tcs;
            Prompt = prompt;
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
            Prompt = string.Empty;
        }

        StateChanged?.Invoke();
        tcs?.TrySetResult(value);
    }
}
