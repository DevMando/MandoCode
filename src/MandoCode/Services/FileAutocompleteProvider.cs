namespace MandoCode.Services;

/// <summary>
/// Provides file listing, filtering, and content reading for @ autocomplete.
/// Supports both file and directory entries for path-based navigation.
/// </summary>
public class FileAutocompleteProvider
{
    private readonly ProjectRootAccessor _projectRootAccessor;
    private readonly HashSet<string> _ignoreDirectories;
    private List<string>? _cachedFiles;
    private List<string>? _cachedDirectories;

    private string ProjectRoot => _projectRootAccessor.ProjectRoot;

    public FileAutocompleteProvider(ProjectRootAccessor projectRootAccessor, HashSet<string> ignoreDirectories)
    {
        _projectRootAccessor = projectRootAccessor;
        _ignoreDirectories = ignoreDirectories;
    }

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
    /// Filters entries (files and directories) matching a fragment. Returns max 15 matches.
    /// Supports path navigation: "Games/" shows contents of Games directory.
    /// Directory entries are returned with a trailing "/" suffix.
    /// </summary>
    public List<string> FilterFiles(string fragment)
    {
        var query = (fragment ?? "").Replace('\\', '/');
        var allFiles = GetFiles();
        var allDirs = GetDirectories();

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
            var deepFilenameMatches = allFiles
                .Where(f => f.Contains('/')
                            && Path.GetFileName(f).Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                            && !subFiles.Contains(f))
                .ToList();

            var deepPathMatches = allFiles
                .Where(f => f.Contains('/')
                            && f.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                            && !subFiles.Contains(f)
                            && !deepFilenameMatches.Contains(f))
                .ToList();

            return subDirs
                .Concat(subFiles)
                .Concat(deepFilenameMatches)
                .Concat(deepPathMatches)
                .Take(15)
                .ToList();
        }

        return subDirs.Concat(subFiles).Take(15).ToList();
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

            // Security: ensure path stays within project root
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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

            // Security: ensure path stays within project root
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
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
        _cachedFiles = null;
        _cachedDirectories = null;
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
