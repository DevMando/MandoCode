using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace MandoCode.Plugins;

/// <summary>
/// Provides safe, controlled filesystem access for the AI assistant.
/// </summary>
public class FileSystemPlugin
{
    private readonly string _projectRoot;
    private readonly HashSet<string> _ignoreDirectories = new()
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
        "packages", "dist", "build", "__pycache__", ".idea"
    };

    public FileSystemPlugin(string projectRoot)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>
    /// Adds additional directories to ignore during file operations.
    /// </summary>
    public void AddIgnoreDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            _ignoreDirectories.Add(dir);
        }
    }

    /// <summary>
    /// Lists all project files recursively, excluding ignored directories.
    /// </summary>
    [KernelFunction("list_all_project_files")]
    [Description("Lists all project files recursively, excluding ignored directories like .git, node_modules, bin, obj, etc.")]
    public async Task<string> ListAllProjectFiles()
    {
        try
        {
            var files = await Task.Run(() => GetAllFiles(_projectRoot));
            var relativeFiles = files.Select(f => Path.GetRelativePath(_projectRoot, f)).ToList();

            if (!relativeFiles.Any())
            {
                return "No files found in the project.";
            }

            return string.Join("\n", relativeFiles);
        }
        catch (Exception ex)
        {
            return $"Error listing files: {ex.Message}";
        }
    }

    /// <summary>
    /// Lists files matching a specific glob pattern.
    /// </summary>
    [KernelFunction("list_files_match_glob_pattern")]
    [Description("Lists files matching a glob pattern. Examples: '*.cs', '*.js', 'src/**/*.ts', '*.*'")]
    public async Task<string> ListFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', '*.js', 'src/**/*.ts')")] string pattern)
    {
        try
        {
            var allFiles = await Task.Run(() => GetAllFiles(_projectRoot));
            var matchingFiles = allFiles
                .Where(f => MatchesPattern(Path.GetRelativePath(_projectRoot, f), pattern))
                .Select(f => Path.GetRelativePath(_projectRoot, f))
                .ToList();

            if (!matchingFiles.Any())
            {
                return $"No files found matching pattern: {pattern}";
            }

            return string.Join("\n", matchingFiles);
        }
        catch (Exception ex)
        {
            return $"Error listing files with pattern '{pattern}': {ex.Message}";
        }
    }

    /// <summary>
    /// Reads the contents of a file.
    /// </summary>
    [KernelFunction("read_file_contents")]
    [Description("Reads the complete contents of a file. Use relative path from project root.")]
    public async Task<string> ReadFile(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {relativePath}";
            }

            var content = await File.ReadAllTextAsync(fullPath);
            var lineCount = content.Split('\n').Length;

            return $"File: {relativePath} ({lineCount} lines)\n" +
                   $"{'='.ToString().PadRight(50, '=')}\n" +
                   $"{content}";
        }
        catch (Exception ex)
        {
            return $"Error reading file '{relativePath}': {ex.Message}";
        }
    }

    /// <summary>
    /// Creates a folder/directory.
    /// </summary>
    [KernelFunction("create_folder")]
    [Description("Creates a new folder/directory. Use this when asked to create a folder, directory, or organize files into a new location.")]
    public Task<string> CreateFolder(
        [Description("Relative path to the folder from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (Directory.Exists(fullPath))
            {
                return Task.FromResult($"Folder already exists:\nRelative path: {relativePath}\nAbsolute path: {fullPath}");
            }

            Directory.CreateDirectory(fullPath);

            return Task.FromResult($"Successfully created folder:\nRelative path: {relativePath}\nAbsolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error creating folder '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Writes content to a file, creating it if it doesn't exist.
    /// </summary>
    [KernelFunction("write_file")]
    [Description("Writes content to a file. Creates the file and directories if they don't exist. Overwrites existing files. Do NOT use this to create empty folders - use create_folder instead.")]
    public async Task<string> WriteFile(
        [Description("Relative path to the file from project root")] string relativePath,
        [Description("Content to write to the file")] string content)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content);
            var lineCount = content.Split('\n').Length;

            return $"Successfully wrote {lineCount} lines to:\n" +
                   $"Relative path: {relativePath}\n" +
                   $"Absolute path: {fullPath}";
        }
        catch (Exception ex)
        {
            return $"Error writing file '{relativePath}': {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes a file from the project.
    /// </summary>
    [KernelFunction("delete_file")]
    [Description("Deletes a file. Use relative path from project root. Cannot delete directories — only files.")]
    public Task<string> DeleteFile(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                return Task.FromResult($"Error: File not found: {relativePath}");
            }

            File.Delete(fullPath);

            return Task.FromResult($"Successfully deleted file:\nRelative path: {relativePath}\nAbsolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error deleting file '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for text within files.
    /// </summary>
    [KernelFunction("search_text_in_files")]
    [Description("Searches for text within files matching a pattern. Returns file paths and line numbers where text is found.")]
    public async Task<string> FindInFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', '*.js')")] string pattern,
        [Description("Text to search for")] string searchText)
    {
        try
        {
            var allFiles = await Task.Run(() => GetAllFiles(_projectRoot));
            var matchingFiles = allFiles
                .Where(f => MatchesPattern(Path.GetRelativePath(_projectRoot, f), pattern))
                .ToList();

            var results = new List<string>();

            foreach (var file in matchingFiles)
            {
                var lines = await File.ReadAllLinesAsync(file);
                var matches = lines
                    .Select((line, index) => new { line, index })
                    .Where(x => x.line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Any())
                {
                    var relativePath = Path.GetRelativePath(_projectRoot, file);
                    results.Add($"{relativePath}:");
                    foreach (var match in matches)
                    {
                        results.Add($"  Line {match.index + 1}: {match.line.Trim()}");
                    }
                }
            }

            if (!results.Any())
            {
                return $"No matches found for '{searchText}' in files matching '{pattern}'";
            }

            return string.Join("\n", results);
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the absolute path for a file.
    /// </summary>
    [KernelFunction("get_absolute_path")]
    [Description("Converts a relative file path to its absolute path on the filesystem. Use this when the user needs the full path to a file.")]
    public async Task<string> GetAbsolutePath(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return $"Path does not exist: {fullPath}";
            }

            return await Task.FromResult($"Absolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return $"Error getting absolute path for '{relativePath}': {ex.Message}";
        }
    }

    private List<string> GetAllFiles(string directory)
    {
        var files = new List<string>();

        try
        {
            // Add files in current directory
            files.AddRange(Directory.GetFiles(directory));

            // Recursively add files from subdirectories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!_ignoreDirectories.Contains(dirName))
                {
                    files.AddRange(GetAllFiles(subDir));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we don't have access to
        }

        return files;
    }

    private bool MatchesPattern(string filePath, string pattern)
    {
        // Simple glob pattern matching
        filePath = filePath.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        // Handle special patterns
        if (pattern == "*.*")
        {
            return true;
        }

        if (pattern.StartsWith("**"))
        {
            var extension = pattern.Replace("**", "").Replace("/", "").Replace("*", "");
            return filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith("*."))
        {
            var extension = pattern.Substring(1);
            return filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains("**"))
        {
            var parts = pattern.Split("**");
            if (parts.Length == 2)
            {
                var startsWith = parts[0].TrimEnd('/');
                var endsWith = parts[1].TrimStart('/').Replace("*", "");
                return (string.IsNullOrEmpty(startsWith) || filePath.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)) &&
                       (string.IsNullOrEmpty(endsWith) || filePath.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Exact match
        return filePath.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private string GetFullPath(string relativePath)
    {
        var fullPath = Path.Combine(_projectRoot, relativePath);
        fullPath = Path.GetFullPath(fullPath);

        // Security check: ensure the path is within project root
        if (!fullPath.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Access denied: Path is outside project root: {relativePath}");
        }

        return fullPath;
    }
}
