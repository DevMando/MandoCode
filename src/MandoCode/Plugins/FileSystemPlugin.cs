using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.SemanticKernel;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Plugins;

/// <summary>
/// Provides safe, controlled filesystem access for the AI assistant.
/// </summary>
public class FileSystemPlugin
{
    private readonly ProjectRootAccessor ProjectRootAccessor;
    private readonly SpinnerService? _spinner;
    private string ProjectRoot => ProjectRootAccessor.ProjectRoot;
    private readonly HashSet<string> _ignoreDirectories = new(MandoCodeConfig.DefaultIgnoreDirectories);

    // File list cache with TTL to avoid repeated full directory walks
    private List<string>? _cachedFiles;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public FileSystemPlugin(ProjectRootAccessor projectRootAccessor, SpinnerService? spinner = null)
    {
        ProjectRootAccessor = projectRootAccessor;
        _spinner = spinner;
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
    [Description("Lists all project files recursively, excluding ignored directories. Returns one relative file path per line. Use list_files_match_glob_pattern instead if you only need specific file types.")]
    public async Task<string> ListAllProjectFiles()
    {
        try
        {
            var files = await Task.Run(() => GetAllFilesCached());
            var relativeFiles = files.Select(f => Path.GetRelativePath(ProjectRoot, f)).ToList();

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
            var matcher = CreateMatcher(pattern);
            var allFiles = await Task.Run(() => GetAllFilesCached());
            var matchingFiles = allFiles
                .Where(f => MatchesPattern(Path.GetRelativePath(ProjectRoot, f), matcher))
                .Select(f => Path.GetRelativePath(ProjectRoot, f))
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
    [Description("Reads the contents of a file. Returns the file content as plain text. Use relative path from project root. Output is capped at 10,000 characters.")]
    public async Task<string> ReadFile(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                return BuildFileNotFoundMessage(relativePath, "read");
            }

            var content = await File.ReadAllTextAsync(fullPath);
            var lineCount = content.Split('\n').Length;

            if (content.Length > 10_000)
            {
                content = content[..10_000] + $"\n... [truncated — file has {lineCount} lines total]";
            }

            return $"File: {relativePath} ({lineCount} lines)\n{content}";
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
            InvalidateCache();

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
    [Description("Writes content to a file (full overwrite). Creates the file and directories if they don't exist. For small edits to existing files, prefer edit_file instead — it's more efficient and produces cleaner diffs. Do NOT use this to create empty folders — use create_folder instead.")]
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
            InvalidateCache();
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
    /// Edits a file by replacing a specific text fragment with new text.
    /// </summary>
    [KernelFunction("edit_file")]
    [Description("Edits an existing file by finding and replacing a specific text fragment. Much more efficient than rewriting the entire file with write_file. The old_text must match exactly (including whitespace and indentation). Use this for targeted edits instead of write_file when modifying existing files.")]
    public async Task<string> EditFile(
        [Description("Relative path to the file from project root")] string relativePath,
        [Description("The exact text to find in the file (must match exactly, including whitespace)")] string old_text,
        [Description("The new text to replace it with")] string new_text)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                return BuildFileNotFoundMessage(relativePath, "edit");
            }

            var content = await File.ReadAllTextAsync(fullPath);

            // Fast path: exact byte-for-byte match.
            var index = content.IndexOf(old_text, StringComparison.Ordinal);
            var matchMethod = "exact";
            string matchedOldText = old_text;
            string insertedNewText = new_text;
            string finalContent;

            if (index < 0)
            {
                // Fallback: CRLF/LF normalization. Models commonly emit LF-only old_text
                // while Windows files are CRLF — a single \r throws off exact matching.
                var normalizedContent = NormalizeLineEndings(content);
                var normalizedOld = NormalizeLineEndings(old_text);
                var normalizedNew = NormalizeLineEndings(new_text);
                var nIndex = normalizedContent.IndexOf(normalizedOld, StringComparison.Ordinal);

                if (nIndex < 0)
                {
                    return $"Error: Could not find the specified text in {relativePath}.\n" +
                           "Common causes:\n" +
                           "  1. The file was modified since you last read it — re-read before editing.\n" +
                           "  2. Whitespace or indentation differs from what's in the file.\n" +
                           "  3. old_text is too large or includes reconstructed-from-memory content.\n" +
                           "Tip: for edits, use a SMALL unique old_text (5-20 lines). " +
                           "For large rewrites, use write_file instead.";
                }

                // Ensure the normalized match is unique.
                var nSecond = normalizedContent.IndexOf(normalizedOld, nIndex + normalizedOld.Length, StringComparison.Ordinal);
                if (nSecond >= 0)
                {
                    return $"Error: Found multiple occurrences of the specified text in {relativePath}. Provide a larger, more unique text fragment to match.";
                }

                // Apply replacement in the normalized domain, then re-apply the file's original
                // line-ending style so we don't accidentally convert CRLF files to LF or vice-versa.
                var updatedNormalized = normalizedContent[..nIndex] + normalizedNew + normalizedContent[(nIndex + normalizedOld.Length)..];
                var usesCrlf = content.Contains("\r\n");
                finalContent = usesCrlf ? updatedNormalized.Replace("\n", "\r\n") : updatedNormalized;
                matchMethod = "line-endings normalized";
                matchedOldText = normalizedOld;
                insertedNewText = normalizedNew;
            }
            else
            {
                // Exact match: ensure it's unique, then apply as-is.
                var secondIndex = content.IndexOf(old_text, index + old_text.Length, StringComparison.Ordinal);
                if (secondIndex >= 0)
                {
                    return $"Error: Found multiple occurrences of the specified text in {relativePath}. Provide a larger, more unique text fragment to match.";
                }
                finalContent = content[..index] + new_text + content[(index + old_text.Length)..];
            }

            await File.WriteAllTextAsync(fullPath, finalContent);
            InvalidateCache();

            var lineCount = finalContent.Split('\n').Length;
            var suffix = matchMethod == "exact" ? "" : $" [{matchMethod}]";
            return $"Successfully edited {relativePath} ({lineCount} lines){suffix}\n" +
                   $"Replaced {matchedOldText.Split('\n').Length} lines with {insertedNewText.Split('\n').Length} lines.\n" +
                   $"Absolute path: {fullPath}";
        }
        catch (Exception ex)
        {
            return $"Error editing file '{relativePath}': {ex.Message}";
        }
    }

    /// <summary>
    /// Collapses any of \r\n, \r, or \n into a single \n. Used to make edit_file
    /// tolerant of models that emit LF-only text against a CRLF file.
    /// </summary>
    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Searches file contents using a text pattern (grep-like).
    /// </summary>
    [KernelFunction("grep_files")]
    [Description("Searches for a text pattern across all project files (like grep). Returns matching file paths, line numbers, and line content. Case-insensitive. Results capped at 50 matches.")]
    public async Task<string> GrepFiles(
        [Description("Text or pattern to search for across all project files")] string searchText)
    {
        try
        {
            var allFiles = await Task.Run(() => GetAllFilesCached());
            var results = new List<string>();
            var matchCount = 0;
            const int maxMatches = 50;

            foreach (var file in allFiles)
            {
                if (matchCount >= maxMatches) break;

                try
                {
                    using var reader = new StreamReader(file);
                    string? line;
                    int lineNum = 0;
                    var fileHasMatch = false;

                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        lineNum++;
                        if (line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!fileHasMatch)
                            {
                                results.Add($"\n{Path.GetRelativePath(ProjectRoot, file)}:");
                                fileHasMatch = true;
                            }
                            results.Add($"  {lineNum}: {line.Trim()}");
                            matchCount++;
                            if (matchCount >= maxMatches) break;
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }

            if (results.Count == 0)
            {
                return $"No matches found for '{searchText}' across project files.";
            }

            var header = matchCount >= maxMatches
                ? $"Found {matchCount}+ matches for '{searchText}' (showing first {maxMatches}):"
                : $"Found {matchCount} match(es) for '{searchText}':";

            return header + string.Join("\n", results);
        }
        catch (Exception ex)
        {
            return $"Error searching files: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes a file from the project.
    /// </summary>
    [KernelFunction("delete_file")]
    [Description("Deletes a single file. Use relative path from project root. For deleting folders/directories, use delete_folder instead.")]
    public Task<string> DeleteFile(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(BuildFileNotFoundMessage(relativePath, "delete"));
            }

            File.Delete(fullPath);
            InvalidateCache();

            return Task.FromResult($"Successfully deleted file:\nRelative path: {relativePath}\nAbsolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error deleting file '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a folder/directory and all its contents from the project.
    /// </summary>
    [KernelFunction("delete_folder")]
    [Description("Deletes a folder/directory and all its contents. Use relative path from project root. Use this when asked to delete, remove, or clean up a folder or directory.")]
    public Task<string> DeleteFolder(
        [Description("Relative path to the folder from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult($"Error: Folder not found: {relativePath}");
            }

            var fileCount = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories).Length;
            var dirCount = Directory.GetDirectories(fullPath, "*", SearchOption.AllDirectories).Length;

            Directory.Delete(fullPath, recursive: true);
            InvalidateCache();

            return Task.FromResult($"Successfully deleted folder ({fileCount} files, {dirCount} subdirectories):\nRelative path: {relativePath}\nAbsolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error deleting folder '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for text within files.
    /// </summary>
    [KernelFunction("search_text_in_files")]
    [Description("Searches for text within files matching a glob pattern. Case-insensitive. Returns file paths and line numbers. Use grep_files instead for searching ALL files without a glob filter.")]
    public async Task<string> FindInFiles(
        [Description("Glob pattern to match files (e.g., '*.cs', '*.js')")] string pattern,
        [Description("Text to search for")] string searchText)
    {
        try
        {
            var matcher = CreateMatcher(pattern);
            var allFiles = await Task.Run(() => GetAllFilesCached());
            var matchingFiles = allFiles
                .Where(f => MatchesPattern(Path.GetRelativePath(ProjectRoot, f), matcher))
                .ToList();

            var results = new List<string>();
            var matchCount = 0;
            const int maxMatches = 50;

            foreach (var file in matchingFiles)
            {
                if (matchCount >= maxMatches) break;

                try
                {
                    using var reader = new StreamReader(file);
                    string? line;
                    int lineNum = 0;
                    var fileHasMatch = false;

                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        lineNum++;
                        if (line.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!fileHasMatch)
                            {
                                results.Add($"{Path.GetRelativePath(ProjectRoot, file)}:");
                                fileHasMatch = true;
                            }
                            results.Add($"  Line {lineNum}: {line.Trim()}");
                            matchCount++;
                            if (matchCount >= maxMatches) break;
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }

            if (results.Count == 0)
            {
                return $"No matches found for '{searchText}' in files matching '{pattern}'";
            }

            var header = matchCount >= maxMatches
                ? $"Found {matchCount}+ matches (showing first {maxMatches}):\n"
                : "";
            return header + string.Join("\n", results);
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
    public Task<string> GetAbsolutePath(
        [Description("Relative path to the file from project root")] string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return Task.FromResult($"Path does not exist: {fullPath}");
            }

            return Task.FromResult($"Absolute path: {fullPath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error getting absolute path for '{relativePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a shell command in the project root directory with idle-based timeout.
    /// </summary>
    [KernelFunction("execute_command")]
    [Description("Executes a shell command (e.g., git status, dotnet build, npm install). Runs in the project root directory. Killed after 30 seconds of no output, or 10 minutes total. Output capped at 5000 characters.")]
    public async Task<string> ExecuteCommand(
        [Description("The shell command to execute (e.g., 'git status', 'dotnet build')")] string command)
    {
        // Intercept cd commands to update the project root
        if (command == "cd" || command.StartsWith("cd "))
        {
            return HandleCdCommand(command);
        }

        const int maxOutputChars = 5000;
        var idleTimeout = TimeSpan.FromSeconds(30);
        var hardCeiling = TimeSpan.FromMinutes(10);

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = ProjectRoot
            };
            // Use ArgumentList for proper escaping instead of manual string building
            psi.ArgumentList.Add(isWindows ? "/c" : "-c");
            psi.ArgumentList.Add(command);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return "Error: Failed to start process.";
            }

            var output = new StringBuilder();
            var startedAt = DateTime.UtcNow;
            var stateLock = new object();
            var lastOutputAt = startedAt;
            var lastLine = string.Empty;
            var capped = false;

            void OnLine(string? line, bool isErr)
            {
                if (line == null) return;
                lock (stateLock)
                {
                    lastOutputAt = DateTime.UtcNow;
                    lastLine = line;
                    if (!capped)
                    {
                        var prefix = isErr ? "[err] " : string.Empty;
                        // Reserve room for the truncation footer; cap when we'd cross.
                        if (output.Length + prefix.Length + line.Length + 1 > maxOutputChars)
                        {
                            capped = true;
                        }
                        else
                        {
                            output.Append(prefix).AppendLine(line);
                        }
                    }
                }
                _spinner?.UpdateActivity(BuildActivity(command, line));
            }

            proc.OutputDataReceived += (_, e) => OnLine(e.Data, false);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data, true);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _spinner?.UpdateActivity(BuildActivity(command, null));

            string? killReason = null;
            while (!proc.HasExited)
            {
                var now = DateTime.UtcNow;
                DateTime lastSeen;
                lock (stateLock) { lastSeen = lastOutputAt; }

                if (now - lastSeen > idleTimeout)
                {
                    killReason = $"idle {(int)idleTimeout.TotalSeconds}s with no output";
                    break;
                }
                if (now - startedAt > hardCeiling)
                {
                    killReason = $"hit {(int)hardCeiling.TotalMinutes}m hard ceiling";
                    break;
                }
                await Task.Delay(250);
            }

            if (killReason != null)
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception ex) { Debug.WriteLine($"Kill failed: {ex.Message}"); }
                try { proc.WaitForExit(2000); } catch { }

                var elapsed = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
                string snapshot;
                lock (stateLock) { snapshot = output.ToString().TrimEnd(); }
                var lastLineSafe = string.IsNullOrEmpty(lastLine) ? "(none)" : Truncate(lastLine, 200);
                var partial = string.IsNullOrEmpty(snapshot) ? "(no output before kill)" : snapshot;
                return $"Killed: {killReason}. Elapsed: {elapsed}s. Last line: {lastLineSafe}\n--- partial output ---\n{partial}";
            }

            // Process exited cleanly. WaitForExit() (no timeout) flushes any
            // outstanding async OutputDataReceived callbacks.
            proc.WaitForExit();

            string result;
            bool wasCapped;
            lock (stateLock)
            {
                result = output.ToString().TrimEnd();
                wasCapped = capped;
            }

            if (string.IsNullOrEmpty(result)) result = "(no output)";
            if (wasCapped) result += "\n... [output truncated at 5000 characters]";

            return $"Exit code: {proc.ExitCode}\n{result}";
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    private static string BuildActivity(string command, string? latestLine)
    {
        var cmd = Truncate(command, 60);
        if (string.IsNullOrEmpty(latestLine))
            return $"$ {cmd}";
        return $"$ {cmd} → {Truncate(latestLine, 80)}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private string HandleCdCommand(string command)
    {
        var target = command.Length > 2 ? command[3..].Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(target))
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var newDir = Path.GetFullPath(Path.Combine(ProjectRoot, target));
            if (!Directory.Exists(newDir))
            {
                return $"Error: No such directory: {target}";
            }

            Directory.SetCurrentDirectory(newDir);
            ProjectRootAccessor.ProjectRoot = newDir;

            return $"Changed directory to {newDir}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns the cached file list, refreshing if expired or invalidated.
    /// </summary>
    private List<string> GetAllFilesCached()
    {
        lock (_cacheLock)
        {
            if (_cachedFiles != null && DateTime.UtcNow < _cacheExpiry)
                return _cachedFiles;
        }

        var files = GetAllFiles(ProjectRoot);

        lock (_cacheLock)
        {
            _cachedFiles = files;
            _cacheExpiry = DateTime.UtcNow + CacheTtl;
        }

        return files;
    }

    /// <summary>
    /// Invalidates the file cache (call after write/delete operations).
    /// </summary>
    private void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedFiles = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }

    private List<string> GetAllFiles(string directory, HashSet<string>? visited = null)
    {
        visited ??= new(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();

        try
        {
            // Guard against symlink cycles by tracking visited directories
            var realPath = Path.GetFullPath(directory);
            if (!visited.Add(realPath))
                return files;

            // Add files in current directory
            files.AddRange(Directory.GetFiles(directory));

            // Recursively add files from subdirectories
            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(subDir);
                if (!_ignoreDirectories.Contains(dirName))
                {
                    files.AddRange(GetAllFiles(subDir, visited));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we don't have access to
        }
        catch (IOException)
        {
            // Skip directories with I/O errors (e.g., broken symlinks, locked files)
        }

        return files;
    }

    private static Matcher CreateMatcher(string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);
        return matcher;
    }

    private static bool MatchesPattern(string filePath, Matcher matcher)
    {
        return matcher.Match(filePath).HasMatches;
    }

    /// <summary>
    /// Builds an actionable "file not found" error. Includes up to N same-filename
    /// matches elsewhere in the project plus a listing of the nearest existing parent
    /// directory so the model can self-correct without re-guessing.
    /// </summary>
    private string BuildFileNotFoundMessage(string relativePath, string operation)
    {
        var msg = new System.Text.StringBuilder();
        msg.Append($"Error: File not found: {relativePath} ({operation}).");

        try
        {
            var fileName = Path.GetFileName(relativePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                var allFiles = GetAllFilesCached();
                var matches = allFiles
                    .Where(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(ProjectRoot, f).Replace('\\', '/'))
                    .Take(5)
                    .ToList();

                if (matches.Count > 0)
                {
                    msg.Append("\n  Files with the same name elsewhere: ")
                       .Append(string.Join(", ", matches));
                }
            }

            var probedDir = Path.GetDirectoryName(relativePath) ?? "";
            while (true)
            {
                var probedFull = string.IsNullOrEmpty(probedDir)
                    ? ProjectRoot
                    : Path.GetFullPath(Path.Combine(ProjectRoot, probedDir));

                if (Directory.Exists(probedFull))
                {
                    var entries = Directory.EnumerateFileSystemEntries(probedFull)
                        .Select(p =>
                        {
                            var name = Path.GetFileName(p);
                            return Directory.Exists(p) ? name + "/" : name;
                        })
                        .OrderBy(n => n)
                        .Take(15)
                        .ToList();

                    if (entries.Count > 0)
                    {
                        var shownPath = string.IsNullOrEmpty(probedDir) ? "(project root)" : probedDir;
                        msg.Append($"\n  Contents of {shownPath}: ")
                           .Append(string.Join(", ", entries));
                    }
                    break;
                }

                if (string.IsNullOrEmpty(probedDir)) break;
                probedDir = Path.GetDirectoryName(probedDir) ?? "";
            }
        }
        catch
        {
            // Diagnostics are best-effort — never let them break the error path.
        }

        return msg.ToString();
    }

    private string GetFullPath(string relativePath)
    {
        // Resolve the literal path first. Only fall back to StripRedundantRootPrefix
        // if nothing exists at the un-stripped target — otherwise legitimate nested
        // folders that share a name with the project root's last segment (e.g. the
        // "MyApp/MyApp/MyApp.csproj" pattern from `dotnet new`) get mangled.
        var literalFull = Path.GetFullPath(Path.Combine(ProjectRoot, relativePath));
        var resolvedRelative = LiteralPathLooksReal(literalFull, relativePath)
            ? relativePath
            : StripRedundantRootPrefix(relativePath);

        var fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, resolvedRelative));

        // Security check: ensure the path is within project root with separator boundary
        // Without the separator, "C:\projects\myapp" would match "C:\projects\myappevil"
        var normalizedRoot = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(ProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Access denied: Path is outside project root: {relativePath}");
        }

        return fullPath;
    }

    // The literal path "looks real" if the file/dir exists, OR (for create operations)
    // its parent directory exists. The parent check handles write_file/create_folder
    // where the target itself doesn't exist yet but the user clearly meant the
    // un-stripped location.
    private static bool LiteralPathLooksReal(string fullPath, string relativePath)
    {
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            return true;

        var parent = Path.GetDirectoryName(fullPath);
        return !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
    }

    /// <summary>
    /// Detects when the model includes part of the project root in a relative path
    /// and strips the redundant prefix. For example, if ProjectRoot ends with
    /// "src/MandoCode/bin/Debug/net8.0" and relativePath is
    /// "src/MandoCode/bin/Debug/net8.0/Games/file.js", returns "Games/file.js".
    /// </summary>
    private string StripRedundantRootPrefix(string relativePath)
    {
        var normalizedRelative = relativePath.Replace('\\', '/').TrimStart('/');
        var normalizedRoot = ProjectRoot.Replace('\\', '/');
        var rootParts = normalizedRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Try progressively longer suffixes of the project root path.
        // If the relative path starts with a matching suffix, strip it.
        for (int i = 0; i < rootParts.Length; i++)
        {
            var suffix = string.Join('/', rootParts.Skip(i));
            if (normalizedRelative.StartsWith(suffix + "/", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = normalizedRelative[(suffix.Length + 1)..];
                if (!string.IsNullOrEmpty(stripped))
                    return stripped;
            }
        }

        return relativePath;
    }
}
