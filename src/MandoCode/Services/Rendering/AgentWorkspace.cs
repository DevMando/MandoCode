using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>Owns independent agent lifetimes while sharing one terminal renderer.</summary>
public sealed class AgentWorkspace(IServiceScopeFactory scopes) : IDisposable, IAsyncDisposable
{
    private readonly List<AgentPane> _panes = [];
    private AgentPane[] _snapshot = [];
    private int _nextId;
    private readonly AgentCallsigns _callsigns = new();
    internal Func<int>? AllocateAgentId { get; set; }
    public WorkspaceRegistry? Registry { get; internal set; }
    public int Id { get; internal set; }
    public string Name { get; internal set; } = "Main";
    public bool IsClosed { get; internal set; }
    public bool IsVisible => !IsClosed && (Registry is null || Registry.Active == this);
    public AgentPane? SelectedPane => _panes.FirstOrDefault(p => p.Selected);
    public IReadOnlyList<AgentPane> Panes => Volatile.Read(ref _snapshot);
    public event Action? Changed;
    public long FocusVersion { get; private set; }
    public long Revision { get; private set; }
    // Stable column membership keeps live agent components mounted as the layout changes.
    public static int Column(int slot) => slot is 0 or 3 ? 0 : 1;
    public void Refresh() { Revision++; Changed?.Invoke(); Registry?.Notify(); }
    public AgentPane Add()
    {
        if (_panes.Count >= 4)
        {
            _panes.First(p => p.Selected).Session.Append(new Text("Four agent panes are already open."));
            return _panes.First(p => p.Selected);
        }
        var pane = new AgentPane(AllocateAgentId?.Invoke() ?? ++_nextId, scopes.CreateAsyncScope(), this);
        pane.Slot = Enumerable.Range(0, 4).First(slot => _panes.All(p => p.Slot != slot));
        var identity = pane.Services.GetService<AgentIdentity>();
        if (identity is not null && pane.Id == 1) identity.CheckpointId = null;
        var sourceConfig = (_panes.FirstOrDefault(p => p.Selected) ?? Registry?.Active.SelectedPane)?.Services.GetService<MandoCode.Models.MandoCodeConfig>();
        if (sourceConfig is not null && pane.Services.GetService<MandoCode.Models.MandoCodeConfig>() is { } targetConfig)
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<MandoCode.Models.MandoCodeConfig>(System.Text.Json.JsonSerializer.Serialize(sourceConfig))!;
            foreach (var property in typeof(MandoCode.Models.MandoCodeConfig).GetProperties().Where(p => p.CanWrite && p.Name != nameof(MandoCode.Models.MandoCodeConfig.AllowPersistence)))
                property.SetValue(targetConfig, property.GetValue(copy));
        }
        var config = pane.Services.GetRequiredService<MandoCode.Models.MandoCodeConfig>();
        var existing = (Registry?.Workspaces.SelectMany(w => w.Panes) ?? Panes).Select(p => p.Name);
        pane.Name = config.UseAgentNames ? (Registry?.Callsigns ?? _callsigns).Next(existing) : AgentNaming.NextFreeName(existing);
        config.AgentName = pane.Name;
        _panes.Add(pane);
        Volatile.Write(ref _snapshot, _panes.ToArray());
        Focus(pane);
        return pane;
    }
    public void Focus(AgentPane pane)
    {
        if (!_panes.Contains(pane)) return;
        foreach (var item in _panes) item.Selected = item == pane;
        FocusVersion++;
        if (IsVisible) TuiConsole.SetActive(pane.Session);
        Refresh();
    }
    public void Move(int direction)
    {
        if (_panes.Count == 0) return;
        var index = _panes.FindIndex(p => p.Selected);
        if (index < 0) return;
        Focus(_panes[(index + direction + _panes.Count) % _panes.Count]);
    }
    public void Close(AgentPane pane)
    {
        if (!_panes.Contains(pane)) return;
        if (_panes.Count == 1)
        {
            pane.Session.Append(new Text("Use /exit to close the last agent."));
            return;
        }
        if (pane.IsBusy?.Invoke() == true)
        {
            pane.Session.Append(new Text("Wait for startup or cancel the running request with Escape, then use /agent-close."));
            return;
        }
        _panes.Remove(pane);
        pane.Selected = false;
        Volatile.Write(ref _snapshot, _panes.ToArray());
        pane.Stop?.Invoke();
        Focus(_panes[0]);
        // The component releases subscriptions before its service scope is disposed.
    }
    public bool Rename(AgentPane pane, string name)
    {
        name = name.Trim();
        if (!_panes.Contains(pane)) return false;
        if (name.Length == 0 || name.Length > 60 || name.Any(char.IsControl))
        {
            pane.Session.Append(new Text("Usage: /agent-rename <name> (1–60 characters, one line)"));
            return false;
        }
        if ((Registry?.Workspaces.SelectMany(w => w.Panes) ?? Panes).Any(p => p != pane && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            pane.Session.Append(new Text("That agent name is already in use. Choose a different name."));
            return false;
        }
        pane.Name = name;
        pane.Services.GetRequiredService<MandoCode.Models.MandoCodeConfig>().AgentName = name;
        Refresh();
        return true;
    }
    public bool Command(AgentPane pane, string command)
    {
        if (Registry?.Command(pane, command) == true) return true;
        switch (command.Trim().ToLowerInvariant())
        {
            case "/agent-new": Add(); return true;
            case "/agent-focus": Focus(_panes[(_panes.IndexOf(pane) + 1) % _panes.Count]); return true;
            case "/agent-focus left": Move(-1); return true;
            case "/agent-focus right": Move(1); return true;


            case "/agent-close": Close(pane); return true;
            default: return false;
        }
    }
    public bool Key(AgentPane pane, KeyboardEventArgs key)
    {
        if (key.Key == "Escape" && pane.Active && pane.IsFileExplorerOpen?.Invoke() == true && pane.ToggleFileExplorer is not null)
        {
            _ = pane.ToggleFileExplorer();
            return true;
        }
        if (key.Key == "Tab" && pane.Active && pane.ExplorerFocus is { Active: true } scope)
        {
            _ = scope.ToggleAsync(key.ShiftKey);
            return true;
        }
        // Alt is the default; Meta variants also work when forwarded by a terminal.
        if (!key.MetaKey && !key.AltKey) return false;
        if (Registry is not null)
        {
            if (key.ShiftKey && key.Key.Equals("n", StringComparison.OrdinalIgnoreCase)) { Registry.Add(); return true; }
            if (key.ShiftKey && key.Key.Equals("a", StringComparison.OrdinalIgnoreCase)) { Registry.TogglePicker(); return true; }
            if (key.ShiftKey && key.Key.Equals("w", StringComparison.OrdinalIgnoreCase)) { Registry.Close(); return true; }
            if (key.ShiftKey && (key.Key.Equals("ArrowLeft", StringComparison.OrdinalIgnoreCase) || key.Key.Equals("LeftArrow", StringComparison.OrdinalIgnoreCase))) { Registry.Cycle(-1); return true; }
            if (key.ShiftKey && (key.Key.Equals("ArrowRight", StringComparison.OrdinalIgnoreCase) || key.Key.Equals("RightArrow", StringComparison.OrdinalIgnoreCase))) { Registry.Cycle(1); return true; }
        }
        switch (key.Key.ToLowerInvariant())
        {
            case "g":
                if (!key.ShiftKey && pane.Active && pane.ToggleGitChanges is not null) _ = pane.ToggleGitChanges();
                return true;
            case "e":
                if (!key.ShiftKey && pane.Active && pane.ToggleFileExplorer is not null) _ = pane.ToggleFileExplorer();
                return true;
            case "d":
                if (!key.ShiftKey && pane.Active && pane.IsBusy?.Invoke() != true && pane.IsAwaitingInput?.Invoke() != true && pane.SubmitCommand is not null)
                    _ = pane.SubmitCommand("/change-directory");
                return true;
            case "n": Add(); return true;
            case "w": Close(pane); return true;
            case "arrowleft": case "leftarrow": Move(-1); return true;
            case "arrowright": case "rightarrow": Move(1); return true;


            default: return false;
        }
    }
    public IRenderable? Find(long id) => Panes.Select(p => p.Session.Find(id)).FirstOrDefault(value => value is not null);
    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        foreach (var pane in _panes.ToArray()) { pane.Stop?.Invoke(); await pane.DisposeAsync().ConfigureAwait(false); }
        _panes.Clear();
        Volatile.Write(ref _snapshot, []);
    }
}

public sealed class AgentPane(int id, AsyncServiceScope scope, AgentWorkspace workspace) : IDisposable, IAsyncDisposable
{
    public int Id { get; } = id;
    public string Name { get; internal set; } = $"Agent {id}";
    public int Slot { get; internal set; }
    public IServiceProvider Services => scope.ServiceProvider;
    public TuiSession Session { get; } = scope.ServiceProvider.GetRequiredService<TuiSession>();
    public string Model => Services.GetRequiredService<MandoCode.Models.MandoCodeConfig>().GetEffectiveModelName();
    public AgentWorkspace Workspace { get; } = workspace;
    internal bool Selected { get; set; }
    public bool Active => Selected && Workspace.IsVisible && Workspace.Registry?.IsWorkspacePickerOpen != true;
    public bool Visible => Workspace.IsVisible;
    public Dictionary<string, object> PresentationState { get; } = [];
    public int Width { get; set; } = 80;
    public int Height { get; set; } = 24;
    public string? FocusKey { get; set; }
    public Func<bool>? IsBusy { get; set; }
    public Func<bool>? IsAwaitingInput { get; set; }
    public Action? Stop { get; set; }
    public Func<string, Task>? SubmitCommand { get; set; }
    public Func<Task>? ToggleFileExplorer { get; set; }
    public Func<Task>? ToggleGitChanges { get; set; }
    public Func<bool>? IsGitChangesOpen { get; set; }
    public Func<bool>? IsGitDiffOpen { get; set; }
    public Func<bool>? IsFileExplorerOpen { get; set; }
    public ExplorerFocusScope? ExplorerFocus { get; set; }
    private bool _disposed;
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await scope.DisposeAsync().ConfigureAwait(false); }
}

public sealed class AgentIdentity
{
    public string? CheckpointId { get; internal set; } = "cli-agent-" + Guid.NewGuid().ToString("N");
}
