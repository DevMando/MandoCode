namespace MandoCode.Services;

/// <summary>Two-target keyboard navigation while an agent's explorer is open.</summary>
public sealed class ExplorerFocusScope
{
    private bool _active;
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Changed?.Invoke();
        }
    }
    public bool PromptFocused { get; private set; } = true;
    public Func<Task>? FocusPrompt { get; set; }
    public Func<Task>? FocusExplorer { get; set; }
    public Func<bool, Task>? NavigateTab { get; set; }
    public Func<string, Task>? InsertFileReference { get; set; }
    public event Action? Changed;
    public void SetPromptFocused(bool value)
    {
        if (PromptFocused == value) return;
        PromptFocused = value;
        Changed?.Invoke();
    }
    public async Task ToggleAsync(bool reverse = false)
    {
        if (!Active) return;
        if (NavigateTab is not null) { await NavigateTab(reverse); return; }
        var target = PromptFocused ? FocusExplorer : FocusPrompt;
        if (target is not null) await target();
    }
}
