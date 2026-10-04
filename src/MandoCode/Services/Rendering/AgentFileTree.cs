namespace MandoCode.Services;

/// <summary>A lazy, per-agent directory tree: only expanded folders are enumerated.</summary>
public sealed class AgentFileTree(string root)
{
    public sealed record Entry(string Path, bool IsDirectory, int Depth);
    public string Root { get; } = root;
    private readonly Dictionary<string, Entry[]> _children = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public bool IsExpanded(string path) => _expanded.Contains(path);
    public IReadOnlyList<Entry> Entries
    {
        get
        {
            var rows = new List<Entry>();
            void Visit(Entry item)
            {
                rows.Add(item);
                if (_expanded.Contains(item.Path) && _children.TryGetValue(item.Path, out var children))
                    foreach (var child in children) Visit(child with { Depth = item.Depth + 1 });
            }
            Visit(new(Root, true, 0));
            return rows;
        }
    }
    public async Task ToggleAsync(string path)
    {
        if (_expanded.Remove(path)) return;
        if (!_children.ContainsKey(path))
            _children[path] = await Task.Run(() => new DirectoryInfo(path).EnumerateFileSystemInfos()
                .OrderByDescending(item => (item.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new Entry(item.FullName, (item.Attributes & FileAttributes.Directory) != 0, 0)).ToArray());
        _expanded.Add(path);
    }
    public async Task RefreshAsync()
    {
        var expanded = _expanded.ToArray();
        _children.Clear();
        _expanded.Clear();
        foreach (var path in expanded.Where(Directory.Exists)) await ToggleAsync(path);
        if (!_expanded.Contains(Root)) await ToggleAsync(Root);
    }
}
