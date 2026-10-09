namespace MandoCode.Services;

[Flags]
public enum AgentPanel
{
    None = 0,
    DirectoryBrowser = 1,
    FileExplorer = 2,
    GitChanges = 4,
    Settings = 8,
    Snapshots = 16,
    ModelDownloads = 32,
    Integrations = 64,
    Setup = 128
}

/// <summary>Per-component presentation state, separate from agent execution and input requests.
/// Explorer/Git regions may coexist with a chat prompt; modal panels suppress it.
/// Panels can remain mounted while a nested picker owns input.</summary>
public sealed class AgentPresentationState
{
    private AgentPanel _open;
    private const AgentPanel ModalPanels = AgentPanel.Settings | AgentPanel.Snapshots |
        AgentPanel.ModelDownloads | AgentPanel.Integrations | AgentPanel.Setup;

    public bool HidesChatPrompt => (_open & (ModalPanels | AgentPanel.DirectoryBrowser)) != 0;
    public bool BlocksRequests => (_open & ModalPanels) != 0;
    public bool IsOpen(AgentPanel panel) => (_open & panel) != 0;

    public void SetOpen(AgentPanel panel, bool open)
    {
        if (open) _open |= panel;
        else _open &= ~panel;
    }
}
