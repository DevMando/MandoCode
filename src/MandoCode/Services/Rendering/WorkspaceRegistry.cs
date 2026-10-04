using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Application-level workspace tabs. Closed entries retain stable renderer positions.</summary>
public sealed class WorkspaceRegistry : IDisposable, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    internal AgentCallsigns Callsigns { get; } = new();
    private readonly List<AgentWorkspace> _entries = [];
    private int _nextWorkspaceId;
    private int _nextAgentId;
    private bool _changing;
    public IReadOnlyList<AgentWorkspace> Entries => _entries;
    public IEnumerable<AgentWorkspace> Workspaces => _entries.Where(w => !w.IsClosed);
    public AgentWorkspace Active { get; private set; }
    public bool IsWorkspacePickerOpen { get; set; }
    public event Action? Changed;

    public WorkspaceRegistry(IServiceScopeFactory scopes, AgentWorkspace main)
    {
        _scopes = scopes;
        Active = main;
        _nextAgentId = main.Panes.Select(p => p.Id).DefaultIfEmpty().Max();
        Attach(main, "Main");
    }
    private void Attach(AgentWorkspace workspace, string name)
    {
        workspace.Id = ++_nextWorkspaceId;
        workspace.Name = name;
        workspace.Registry = this;
        workspace.AllocateAgentId = () => Interlocked.Increment(ref _nextAgentId);
        _entries.Add(workspace);
    }
    public AgentWorkspace Add(string? name = null)
    {
        var source = Active.SelectedPane;
        var workspace = new AgentWorkspace(_scopes);
        _changing = true;
        try
        {
            Attach(workspace, string.IsNullOrWhiteSpace(name) ? $"Workspace {_nextWorkspaceId + 1}" : name.Trim());
            var pane = workspace.Add();
            if (source is not null)
            {
                var config = source.Services.GetRequiredService<MandoCode.Models.MandoCodeConfig>();
                var target = pane.Services.GetRequiredService<MandoCode.Models.MandoCodeConfig>();
                var copy = System.Text.Json.JsonSerializer.Deserialize<MandoCode.Models.MandoCodeConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
                foreach (var property in typeof(MandoCode.Models.MandoCodeConfig).GetProperties().Where(p => p.CanWrite && p.Name != nameof(MandoCode.Models.MandoCodeConfig.AgentName) && p.Name != nameof(MandoCode.Models.MandoCodeConfig.AllowPersistence)))
                    property.SetValue(target, property.GetValue(copy));
            }
            Switch(workspace);
        }
        finally { _changing = false; Notify(); }
        return workspace;
    }
    public void Switch(AgentWorkspace workspace)
    {
        if (workspace.IsClosed || !_entries.Contains(workspace)) return;
        IsWorkspacePickerOpen = false;
        var previous = Active;
        Active = workspace;
        previous.Refresh();
        if (workspace.SelectedPane is { } pane) workspace.Focus(pane);
        Notify();
    }
    public void Cycle(int direction)
    {
        var items = Workspaces.ToArray();
        var index = Array.IndexOf(items, Active);
        Switch(items[(index + direction + items.Length) % items.Length]);
    }
    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Active.Name = name.Trim();
        Notify();
    }
    public void OpenPicker()
    {
        IsWorkspacePickerOpen = true;
        Active.Refresh();
    }
    public void ClosePicker()
    {
        IsWorkspacePickerOpen = false;
        if (Active.SelectedPane is { } pane) Active.Focus(pane);
        else Notify();
    }
    public void TogglePicker()
    {
        if (IsWorkspacePickerOpen) ClosePicker();
        else OpenPicker();
    }
    public void Close()
    {
        var pane = Active.SelectedPane;
        if (Workspaces.Count() == 1)
        {
            pane?.Session.Append(new Text("Keep one workspace open; use /exit to leave the application."));
            return;
        }
        if (Active.Panes.Any(p => p.IsBusy?.Invoke() == true))
        {
            pane?.Session.Append(new Text("Cancel running requests and finish pending approvals before closing this workspace."));
            return;
        }
        var closing = Active;
        foreach (var item in closing.Panes) if (!item.ArchiveForClose()) return;
        closing.IsClosed = true;
        foreach (var item in closing.Panes) item.Stop?.Invoke();
        Switch(Workspaces.First());
        closing.Refresh();
        Notify();
    }
    public bool Command(AgentPane pane, string command)
    {
        var parts = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var argument = parts.Length > 1 ? parts[1].Trim() : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "/workspace-new": Add(argument); return true;
            case "/workspace-all": OpenPicker(); return true;
            case "/workspace-rename":
                if (argument.Length == 0) pane.Session.Append(new Text("Usage: /workspace-rename <name>"));
                else Rename(argument);
                return true;
            case "/workspace-close": Close(); return true;
            case "/workspace":
                if (argument.Length == 0) Cycle(1);
                else if (Workspaces.FirstOrDefault(w => w.Name.Equals(argument, StringComparison.OrdinalIgnoreCase) || w.Id.ToString() == argument) is { } workspace) Switch(workspace);
                else pane.Session.Append(new Text("Workspace not found. Use its name or tab number."));
                return true;
            default: return false;
        }
    }
    public void Notify() { if (!_changing) Changed?.Invoke(); }
    public AgentPane? Restore(ArchivedAgent archive)
    {
        var existing = Workspaces.SelectMany(w => w.Panes).FirstOrDefault(p => p.PersistKey == archive.Key);
        if (existing is not null) { Switch(existing.Workspace); existing.Workspace.Focus(existing); return existing; }
        var store = Active.SelectedPane!.Services.GetRequiredService<AgentArchiveStore>();
        var claim = store.Claim(archive.Key);
        if (claim is null) { Active.SelectedPane.Session.Append(new Text(store.Error ?? "Conversation unavailable.", new Style(Color.Yellow))); return null; }
        if (Active.Panes.Count < 4) { var restored = Active.Add(archive); restored.ArchiveLease = claim; return restored; }
        var workspace = new AgentWorkspace(_scopes);
        Attach(workspace, "Restored agents");
        var pane = workspace.Add(archive);
        pane.ArchiveLease = claim;
        Switch(workspace);
        return pane;
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        foreach (var workspace in _entries) await workspace.DisposeAsync().ConfigureAwait(false);
    }
}
