namespace MandoCode.Services;

/// <summary>Allows one model picker and confirmation flow per agent pane.</summary>
internal sealed class AgentModelSelectionGate
{
    private int _active;
    public bool TryEnter() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;
    public void Exit() => Volatile.Write(ref _active, 0);
}
