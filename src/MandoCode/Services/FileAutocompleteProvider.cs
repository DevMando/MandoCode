namespace MandoCode.Services;

/// <summary>
/// Provides file listing, filtering, and content reading for @ autocomplete.
/// </summary>
public class FileAutocompleteProvider
{
    private readonly string _projectRoot;
    private readonly HashSet<string> _ignoreDirectories;
    private List<string>? _cachedFiles;

    public FileAutocompleteProvider(string projectRoot, HashSet<string> ignoreDirectories)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _ignoreDirectories = ignoreDirectories;
    }

    /// <summary>
    /// Gets all project file paths (relative, forward-slash normalized). Lazy-loads and caches.
    /// </summary>
    public List<string> GetFiles()
    {
        if (_cachedFiles != null)
            return _cachedFiles;

        var files = GetAllFilesRecursive(_projectRoot);
        _cachedFiles = files
            .Select(f => Path.GetRelativePath(_projectRoot, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        return _cachedFiles;
    }

    /// <summary>
    /// Filters files matching a fragment. Returns max 15 matches.
    /// Prioritizes filename matches over path matches.
    /// </summary>
    public List<string> FilterFiles(string fragment)
    {
        if (string.IsNullOrEmpty(fragment))
            return GetFiles().Take(15).ToList();

        var query = fragment.Replace('\\', '/');
        var allFiles = GetFiles();

        // Filename matches (higher priority)
        var filenameMatches = allFiles
            .Where(f => Path.GetFileName(f).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Path matches (lower priority)
        var pathMatches = allFiles
            .Where(f => f.Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !filenameMatches.Contains(f))
            .ToList();

        return filenameMatches
            .Concat(pathMatches)
            .Take(15)
            .ToList();
    }

    /// <summary>
    /// Reads the content of a file by relative path.
    /// Returns null if the file doesn't exist or is outside project root.
    /// </summary>
    public string? ReadFileContent(string relativePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));

            // Security: ensure path stays within project root
            if (!fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
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
    /// Invalidates the file cache so next GetFiles() call rescans.
    /// </summary>
    public void RefreshCache()
    {
        _cachedFiles = null;
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
}
