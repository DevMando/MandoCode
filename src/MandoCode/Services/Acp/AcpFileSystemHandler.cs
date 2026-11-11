using System.Text;
using MandoCode.Acp;
using MandoCode.Acp.Protocol;
using MandoCode.Plugins;

namespace MandoCode.Services.Acp;

/// <summary>What <c>fs/write_text_file</c> changed; the pane shows it as a diff.</summary>
/// <param name="OldText">The previous content; null when the file did not exist (or was too large to diff).</param>
public sealed record AcpFileChange(string Path, string? OldText, string NewText);

/// <summary>
/// <c>fs/read_text_file</c> and <c>fs/write_text_file</c> for the agent, confined to the project folder:
/// absolute paths only, checked with <see cref="FileSystemPlugin.ResolvePath"/> and then canonicalised so that a
/// symlink or junction on the final target <i>or on any parent directory</i> that leads outside the folder is refused.
/// Writes are atomic (temp file + replace) and create directories only inside the folder.
/// </summary>
/// <remarks>
/// The check and the use are separate steps, so a link swapped in between them by another process could still escape;
/// closing that needs handle-based opens and is not attempted here.
/// </remarks>
public sealed class AcpFileSystemHandler
{
    /// <summary>Largest file read whole; larger ones need <c>line</c>/<c>limit</c>.</summary>
    public const long MaxReadBytes = 5 * 1024 * 1024;

    /// <summary>Largest result returned from a ranged read.</summary>
    public const int MaxResultChars = 5 * 1024 * 1024;

    /// <summary>Files up to this size are read before a write so that the transcript can show a diff.</summary>
    private const long MaxDiffBytes = 2 * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;
    private readonly string _canonicalRoot;
    private readonly Action<AcpFileChange>? _written;

    public AcpFileSystemHandler(string projectRoot, Action<AcpFileChange>? written = null)
    {
        _root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _canonicalRoot = Canonicalize(_root);
        _written = written;
    }

    public ReadTextFileResponse Read(ReadTextFileRequest request)
    {
        var path = Guard(request.Path);
        if (Directory.Exists(path))
            throw Invalid($"'{request.Path}' is a directory.");
        if (!File.Exists(path))
            throw new JsonRpcException(AcpErrorCodes.ResourceNotFound, $"File not found: {request.Path}");
        if (request.Line is < 1)
            throw Invalid("line must be 1 or greater.");
        if (request.Limit is < 0)
            throw Invalid("limit must not be negative.");

        try
        {
            if (request.Line is null && request.Limit is null)
            {
                var length = new FileInfo(path).Length;
                if (length > MaxReadBytes)
                    throw Invalid($"The file is {length} bytes; reading it whole is limited to {MaxReadBytes}. Ask for a range with line and limit.");
                return new ReadTextFileResponse { Content = File.ReadAllText(path) };
            }

            var first = (request.Line ?? 1) - 1;
            var builder = new StringBuilder();
            var index = 0;
            var taken = 0;
            foreach (var line in File.ReadLines(path))
            {
                if (index++ < first)
                    continue;
                if (request.Limit is { } limit && taken >= limit)
                    break;
                if (builder.Length + line.Length + 1 > MaxResultChars)
                    throw Invalid($"The requested range is larger than {MaxResultChars} characters; ask for fewer lines.");
                if (taken > 0)
                    builder.Append('\n');
                builder.Append(line);
                taken++;
            }

            return new ReadTextFileResponse { Content = builder.ToString() };
        }
        catch (IOException ex)
        {
            throw new JsonRpcException(AcpErrorCodes.InternalError, $"Could not read '{request.Path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new JsonRpcException(AcpErrorCodes.InternalError, $"Could not read '{request.Path}': {ex.Message}");
        }
    }

    public WriteTextFileResponse Write(WriteTextFileRequest request)
    {
        var path = Guard(request.Path);
        if (Directory.Exists(path))
            throw Invalid($"'{request.Path}' is a directory.");

        try
        {
            string? old = null;
            var exists = File.Exists(path);
            if (exists && new FileInfo(path).Length <= MaxDiffBytes)
                old = File.ReadAllText(path);

            var directory = Path.GetDirectoryName(path)!;
            // Guard() canonicalised the path against the root, so everything created here is inside it.
            Directory.CreateDirectory(directory);

            var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temp, request.Content, new UTF8Encoding(false));
                if (exists)
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            try
            {
                _written?.Invoke(new AcpFileChange(path, exists ? old : null, request.Content));
            }
            catch (Exception)
            {
                // Showing the diff is a courtesy; the write already happened.
            }

            return new WriteTextFileResponse();
        }
        catch (IOException ex)
        {
            throw new JsonRpcException(AcpErrorCodes.InternalError, $"Could not write '{request.Path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new JsonRpcException(AcpErrorCodes.InternalError, $"Could not write '{request.Path}': {ex.Message}");
        }
    }

    /// <summary>Returns the canonical path to use, or throws an invalid-params error saying why the path is refused.</summary>
    public string Guard(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw Invalid("The path is empty or contains invalid characters.");
        if (!Path.IsPathFullyQualified(path))
            throw Invalid($"The path must be absolute: {path}");

        var full = Path.GetFullPath(path);
        string resolved;
        try
        {
            resolved = FileSystemPlugin.ResolvePath(_root, full);
        }
        catch (InvalidOperationException)
        {
            throw Invalid($"The path is outside the project folder: {path}");
        }

        // ResolvePath may "correct" a path that does not exist; an absolute path is taken literally or not at all.
        if (!string.Equals(resolved, full, PathComparison))
            throw Invalid($"The path is outside the project folder: {path}");
        if (string.Equals(full, _root, PathComparison))
            throw Invalid("The project folder itself is not a file.");

        var canonical = Canonicalize(full);
        if (!IsInside(canonical, _canonicalRoot))
            throw Invalid($"The path leads outside the project folder through a link: {path}");
        return canonical;
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || string.Equals(path, root, PathComparison);

    /// <summary>
    /// The path with every link on it (the target and each parent directory) replaced by what it points to.
    /// Parts that do not exist yet are kept as they are, so a path that is about to be created is judged by where
    /// its nearest existing parent really is.
    /// </summary>
    private static string Canonicalize(string fullPath, int depth = 0)
    {
        if (depth > 16)
            throw Invalid("Too many nested links.");

        var root = Path.GetPathRoot(fullPath) ?? "";
        var current = root;
        var rest = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in rest)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!IsLink(info))
                continue;

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                throw Invalid($"A link on the path cannot be resolved: {current}");
            }

            if (target is null)
                throw Invalid($"A link on the path cannot be resolved: {current}");
            current = Canonicalize(Path.GetFullPath(target.FullName), depth + 1);
        }

        return current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : current;
    }

    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static JsonRpcException Invalid(string message) => new(AcpErrorCodes.InvalidParams, message);
}
