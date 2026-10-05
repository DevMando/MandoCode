namespace MandoCode.Services;

/// <summary>
/// Provides file listing, filtering, and content reading for @ autocomplete.
/// Supports both file and directory entries for path-based navigation.
/// </summary>
public class FileAutocompleteProvider : IDisposable
{
    private readonly ProjectRootAccessor _projectRootAccessor;
    private readonly HashSet<string> _ignoreDirectories;
    private volatile List<string>? _cachedFiles;
    private volatile List<string>? _cachedDirectories;

    private readonly object _gate = new();
    private CancellationTokenSource? _lookup;
    private FileSystemWatcher? _watcher;
    private string? _lookupQuery, _cacheRoot;
    private List<string> _lookupResults = [];
    private bool _loading, _disposed;
    private string? _workRoot;
    private bool _working, _dirty = true;
    private int _queryVersion;
    private bool _indexComplete;
    private CancellationTokenSource? _filterCancellation;
    internal Action? RootListed { get; set; }
    private List<string>? _rootSuggestions;
    private readonly System.Collections.Concurrent.ConcurrentQueue<FileSystemEventArgs> _changes = new();
    public bool IsIndexing { get { lock (_gate) return _loading && (!_indexComplete || _cacheRoot != ProjectRoot || _dirty); } }
    internal int ScanCount { get; private set; }
    public bool IsLoading { get { lock (_gate) return _loading; } }
    public event Action? SuggestionsChanged;

    /// <summary>Shares one index scan across all keystrokes; filters use the latest query.</summary>
    public List<string> GetSuggestions(string query)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            var root = ProjectRoot;
            if (_working && _workRoot != root) CancelPending();
            if (!_dirty && (_indexComplete || _working) && _cacheRoot == root && _changes.IsEmpty) {
                if (query.Length == 0 && _rootSuggestions is not null) return new(_rootSuggestions);
                if (_lookupQuery == query) return new(_lookupResults);
            }
            var queryChanged = _lookupQuery != query;
            if (queryChanged) { _lookupQuery = query; _lookupResults = []; _queryVersion++; }
            _loading = true;
            if (_working) {
                if (queryChanged && _cacheRoot == root) FilterPartial(query, _queryVersion);
                return _cacheRoot == root ? new(_lookupResults) : [];
            }
            _lookup?.Dispose(); _lookup = new();
            _working = true; _workRoot = root;
            var token = _lookup.Token;
            _ = Task.Run(() => RunLookup(root, token));
            return _cacheRoot == root ? new(_lookupResults) : [];
        }
    }
    private async Task RunLookup(string root, CancellationToken ct)
    {
        try
        {
            bool scan;
            lock (_gate) { scan = _dirty || !_indexComplete || _cachedFiles is null || _cachedDirectories is null || _cacheRoot != root; if (scan) { _dirty = false; _indexComplete = false; ScanCount++; } }
            if (scan)
            {
                var files = new List<string>(); var directories = new List<string>();
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            ct.ThrowIfCancellationRequested();
                            var attrs = File.GetAttributes(entry);
                            if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                            if ((attrs & FileAttributes.Directory) != 0)
                            {
                                if (_ignoreDirectories.Contains(Path.GetFileName(entry))) continue;
                                directories.Add(relative); pending.Push(entry);
                            }
                            else files.Add(relative);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    if (directory == root)
                    {
                        // Publish the current directory before visiting its descendants.
                        lock (_gate) {
                            ct.ThrowIfCancellationRequested();
                            _cachedFiles = files.Order(StringComparer.OrdinalIgnoreCase).ToList();
                            _cachedDirectories = directories.Order(StringComparer.OrdinalIgnoreCase).ToList();
                            _cacheRoot = root;
                            _rootSuggestions = FilterIndex("", _cachedFiles, _cachedDirectories);
                            _lookupResults = FilterIndex(_lookupQuery ?? "", _cachedFiles, _cachedDirectories);
                        }
                        SuggestionsChanged?.Invoke();
                        RootListed?.Invoke();
                    }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase); directories.Sort(StringComparer.OrdinalIgnoreCase);
                ct.ThrowIfCancellationRequested(); Watch(root);
                lock (_gate) { ct.ThrowIfCancellationRequested(); _cachedFiles = files; _cachedDirectories = directories; _cacheRoot = root; _indexComplete = true; }
            }
            ApplyChanges(root, ct);
            while (true)
            {
                string query; int version;
                lock (_gate) { ct.ThrowIfCancellationRequested(); query = _lookupQuery ?? ""; version = _queryVersion; }
                if (query.Length > 0) await Task.Delay(120, ct);
                var results = FilterIndex(query, _cachedFiles ?? [], _cachedDirectories ?? []);
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (version != _queryVersion) continue;
                    _lookupResults = results; if (query.Length == 0) _rootSuggestions = results; _loading = false; _working = false;
                    break;
                }
            }
            SuggestionsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) { if (!ct.IsCancellationRequested) { _loading = false; _working = false; } } SuggestionsChanged?.Invoke(); }
    }
    public void CancelPending()
    {
        lock (_gate) { _lookup?.Cancel(); _filterCancellation?.Cancel(); _working = false; _loading = false; _lookupQuery = null; _lookupResults = []; }
    }
    private void FilterPartial(string query, int version)
    {
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _filterCancellation = new();
        var token = _filterCancellation.Token;
        var files = _cachedFiles ?? []; var directories = _cachedDirectories ?? [];
        _ = Task.Run(async () => {
            try {
                await Task.Delay(120, token);
                var results = FilterIndex(query, files, directories);
                lock (_gate) { token.ThrowIfCancellationRequested(); if (!_working || version != _queryVersion || !ReferenceEquals(files, _cachedFiles)) return; _lookupResults = results; }
                SuggestionsChanged?.Invoke();
            } catch (OperationCanceledException) { }
        });
    }
    private void ApplyChanges(string root, CancellationToken ct)
    {
        if (_changes.IsEmpty) return;
        var files = new HashSet<string>(_cachedFiles ?? [], StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(_cachedDirectories ?? [], StringComparer.OrdinalIgnoreCase);
        void Remove(string full) {
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            files.RemoveWhere(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase) || p.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase));
            directories.RemoveWhere(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase) || p.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase));
        }
        while (_changes.TryDequeue(out var change))
        {
            ct.ThrowIfCancellationRequested();
            if (change is RenamedEventArgs renamed) Remove(renamed.OldFullPath);
            Remove(change.FullPath);
            var pending = new Stack<string>(); pending.Push(change.FullPath);
            while (pending.TryPop(out var path))
            {
                ct.ThrowIfCancellationRequested();
                try {
                    if (!Path.Exists(path)) continue;
                    var attrs = File.GetAttributes(path);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") continue;
                    if (relative.Split('/').Any(part => _ignoreDirectories.Contains(part))) continue;
                    if ((attrs & FileAttributes.Directory) != 0) {
                        if (_ignoreDirectories.Contains(Path.GetFileName(path))) continue;
                        directories.Add(relative);
                        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
                    } else files.Add(relative);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        lock (_gate) { ct.ThrowIfCancellationRequested(); _cachedFiles = files.Order(StringComparer.OrdinalIgnoreCase).ToList(); _cachedDirectories = directories.Order(StringComparer.OrdinalIgnoreCase).ToList(); }
    }    private void Watch(string root)
    {
        if (_watcher?.Path == root) return;
        _watcher?.Dispose(); _watcher = null;
        try
        {
            _watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            _watcher.Created += Changed; _watcher.Deleted += Changed; _watcher.Renamed += Changed;
            _watcher.Error += (_, _) => RefreshCache();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }
    private void Changed(object sender, FileSystemEventArgs args)
    {
        var relative = Path.GetRelativePath(ProjectRoot, args.FullPath);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => _ignoreDirectories.Contains(part)) && args is not RenamedEventArgs) return;
        _changes.Enqueue(args);
        lock (_gate) { _rootSuggestions = null; }
        SuggestionsChanged?.Invoke();
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _lookup?.Cancel(); _filterCancellation?.Cancel(); _watcher?.Dispose(); } }
    private string ProjectRoot => _projectRootAccessor.ProjectRoot;

    public FileAutocompleteProvider(ProjectRootAccessor projectRootAccessor, HashSet<string> ignoreDirectories)
    {
        _projectRootAccessor = projectRootAccessor;
        _ignoreDirectories = new(ignoreDirectories, StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildDirectoryScopeInstruction(string relativePath) =>
        $"Scope instruction: The user explicitly referenced '{relativePath}/'. Keep file operations " +
        $"inside that directory by prefixing relative paths with '{relativePath}/'. To inspect it " +
        $"recursively, call list_all_project_files with relativeDirectory='{relativePath}'. Do not " +
        "list the entire project root for this request.";

    /// <summary>
    /// Gets all project file paths (relative, forward-slash normalized). Lazy-loads and caches.
    /// </summary>
    public List<string> GetFiles()
    {
        if (_cachedFiles != null)
            return _cachedFiles;

        var root = ProjectRoot;
        var files = GetAllFilesRecursive(root);
        _cachedFiles = files
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        return _cachedFiles;
    }

    /// <summary>
    /// Gets all project directory paths (relative, forward-slash normalized). Lazy-loads and caches.
    /// </summary>
    public List<string> GetDirectories()
    {
        if (_cachedDirectories != null)
            return _cachedDirectories;

        var root = ProjectRoot;
        var dirs = GetAllDirectoriesRecursive(root);
        _cachedDirectories = dirs
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
            .OrderBy(d => d)
            .ToList();

        return _cachedDirectories;
    }

    /// <summary>
    /// Filters entries (files and directories) matching a fragment. Returns all matches; the picker controls the visible window.
    /// Supports path navigation: "Games/" shows contents of Games directory.
    /// Directory entries are returned with a trailing "/" suffix.
    /// </summary>
    public List<string> FilterFiles(string fragment)
        => FilterIndex(fragment, GetFiles(), GetDirectories());

    private static List<string> FilterIndex(string fragment, List<string> allFiles, List<string> allDirs)
    {
        var query = (fragment ?? "").Replace('\\', '/');

        // Determine the directory prefix and name filter
        string dirPrefix = "";
        string nameFilter = query;

        var lastSlash = query.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            dirPrefix = query.Substring(0, lastSlash + 1);
            nameFilter = query.Substring(lastSlash + 1);
        }

        // Get immediate subdirectories within dirPrefix
        var subDirs = allDirs
            .Where(d =>
            {
                if (dirPrefix.Length > 0)
                {
                    if (!d.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var remainder = d.Substring(dirPrefix.Length);
                    if (remainder.Contains('/'))
                        return false; // not an immediate child
                    if (!string.IsNullOrEmpty(nameFilter))
                        return remainder.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
                    return true;
                }
                else
                {
                    // Top-level directories only
                    if (d.Contains('/'))
                        return false;
                    if (!string.IsNullOrEmpty(nameFilter))
                        return d.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            })
            .Select(d => d + "/")
            .ToList();

        // Get immediate files within dirPrefix
        var subFiles = allFiles
            .Where(f =>
            {
                if (dirPrefix.Length > 0)
                {
                    if (!f.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var remainder = f.Substring(dirPrefix.Length);
                    if (remainder.Contains('/'))
                        return false; // not an immediate child
                    if (!string.IsNullOrEmpty(nameFilter))
                        return Path.GetFileName(f).Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
                    return true;
                }
                else
                {
                    // Top-level files only
                    if (f.Contains('/'))
                    {
                        // Not a top-level file, but may match by name (deep search)
                        if (!string.IsNullOrEmpty(nameFilter))
                            return false; // handled below in deep search
                        return false;
                    }
                    if (!string.IsNullOrEmpty(nameFilter))
                        return Path.GetFileName(f).Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            })
            .ToList();

        // When at top level with a name filter, also include deep matches
        if (string.IsNullOrEmpty(dirPrefix) && !string.IsNullOrEmpty(nameFilter))
        {
            var immediate = subFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deepFilenameMatches = allFiles
                .Where(f => f.Contains('/')
                            && Path.GetFileName(f).Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                            && !immediate.Contains(f))
                .ToList();

            var filenames = deepFilenameMatches.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var deepPathMatches = allFiles
                .Where(f => f.Contains('/')
                            && f.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                            && !immediate.Contains(f)
                            && !filenames.Contains(f))
                .ToList();

            return subDirs
                .Concat(subFiles)
                .Concat(deepFilenameMatches)
                .Concat(deepPathMatches)
                .ToList();
        }

        return subDirs.Concat(subFiles).ToList();
    }

    /// <summary>
    /// Reads the content of a file by relative path.
    /// Returns null if the file doesn't exist or is outside project root.
    /// </summary>
    public string? ReadFileContent(string relativePath)
    {
        try
        {
            var root = ProjectRoot;
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

            // Security: ensure path stays within project root with separator boundary
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!File.Exists(fullPath))
                return null;

            return File.ReadAllText(fullPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a directory listing for a relative directory path.
    /// Returns null if the path is not a directory or is outside project root.
    /// </summary>
    public string? GetDirectoryListing(string relativePath)
    {
        try
        {
            var root = ProjectRoot;
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

            // Security: ensure path stays within project root with separator boundary
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Directory.Exists(fullPath))
                return null;

            var entries = new List<string>();

            foreach (var dir in Directory.GetDirectories(fullPath))
            {
                var name = Path.GetFileName(dir);
                if (!_ignoreDirectories.Contains(name))
                    entries.Add($"  {name}/");
            }

            foreach (var file in Directory.GetFiles(fullPath))
            {
                entries.Add($"  {Path.GetFileName(file)}");
            }

            return entries.Count > 0
                ? string.Join("\n", entries)
                : "(empty directory)";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Invalidates the file and directory cache so next call rescans.
    /// </summary>
    public void RefreshCache()
    {
        lock (_gate) { _dirty = true; _rootSuggestions = null; if (!_working) _lookupQuery = null; }
    }

    private List<string> GetAllFilesRecursive(string directory)
    {
        var files = new List<string>();

        try
        {
            files.AddRange(Directory.GetFiles(directory));

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!_ignoreDirectories.Contains(dirName))
                {
                    files.AddRange(GetAllFilesRecursive(subDir));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we don't have access to
        }

        return files;
    }

    private List<string> GetAllDirectoriesRecursive(string directory)
    {
        var dirs = new List<string>();

        try
        {
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!_ignoreDirectories.Contains(dirName))
                {
                    dirs.Add(subDir);
                    dirs.AddRange(GetAllDirectoriesRecursive(subDir));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we don't have access to
        }

        return dirs;
    }
}
