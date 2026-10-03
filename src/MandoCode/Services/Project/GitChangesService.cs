using System.Diagnostics;

namespace MandoCode.Services;

public sealed record GitChangedFile(string Path, string Status, string? OriginalPath, int Added, int Deleted, bool Binary)
{
    public bool Untracked => Status == "??";
    public string Label => Untracked ? "Untracked" : Status.Contains('U') || Status is "AA" or "DD" ? "Conflict"
        : Status.Contains('R') ? "Renamed" : Status.Contains('D') ? "Deleted" : Status.Contains('A') ? "Added" : "Modified";
    public string Stage => Untracked ? "" : Status[0] != ' ' && Status[1] != ' ' ? "staged + unstaged" : Status[0] != ' ' ? "staged" : "unstaged";
}
public sealed record GitFilePreview(IReadOnlyList<string> Lines, bool Binary, bool Truncated);

/// <summary>Selected-project Git changes, combined against HEAD like Desktop.</summary>
public static class GitChangesService
{
    public static async Task<IReadOnlyList<GitChangedFile>> ReadAsync(string root, CancellationToken token = default)
    {
        var files = await Status(root, ".", token);
        var result = new List<GitChangedFile>();
        var hasHead = await HasHead(root, token);
        foreach (var file in files)
        {
            if (!file.Untracked && hasHead)
            {
                var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--numstat", "HEAD", "--", file.Path };
                if (file.OriginalPath is not null) args.Add(file.OriginalPath);
                var stats = await Run(root, token, args.ToArray());
                int added = 0, deleted = 0; bool binary = false;
                foreach (var line in stats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;
                    if (parts[0] == "-") { binary = true; continue; }
                    if (int.TryParse(parts[0], out var a)) added += a;
                    if (int.TryParse(parts[1], out var d)) deleted += d;
                }
                result.Add(file with { Added = added, Deleted = deleted, Binary = binary });
                continue;
            }
            var preview = await PreviewAsync(root, file, token);
            result.Add(file with { Added = preview.Truncated ? -1 : preview.Lines.Count, Deleted = 0, Binary = preview.Binary });
        }
        return result.OrderBy(x => x.Label == "Conflict" ? 0 : 1).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static IReadOnlyList<GitChangedFile> Parse(string status)
    {
        var fields = status.Split('\0');
        var result = new List<GitChangedFile>();
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4) continue;
            var xy = entry[..2];
            var path = entry[3..];
            string? original = null;
            if (xy.Contains('R') || xy.Contains('C')) original = ++i < fields.Length ? fields[i] : null;
            if (path.StartsWith("../", StringComparison.Ordinal)) continue;
            result.Add(new(path, xy, original, 0, 0, false));
        }
        return result;
    }
    private static async Task<IReadOnlyList<GitChangedFile>> Status(string root, string path, CancellationToken token)
    {
        // Porcelain -z is always repository-relative, even when invoked from a
        // project subdirectory. Convert it before previewing or restoring files.
        var repo = (await Run(root, token, "rev-parse", "--show-toplevel")).Trim();
        var status = await Run(root, token, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", path);
        return Parse(status).Select(file => file with
        {
            Path = System.IO.Path.GetRelativePath(root, System.IO.Path.Combine(repo, file.Path)).Replace('\\', '/'),
            OriginalPath = file.OriginalPath is null ? null : System.IO.Path.GetRelativePath(root, System.IO.Path.Combine(repo, file.OriginalPath)).Replace('\\', '/')
        }).Where(file => !file.Path.StartsWith("../", StringComparison.Ordinal)).ToArray();
    }
    public static async Task<GitFilePreview> PreviewAsync(string root, GitChangedFile file, CancellationToken token = default)
    {
        var path = SafePath(root, file.Path);
        string text;
        if (file.Untracked || !await HasHead(root, token))
        {
            if (!File.Exists(path)) return new([], false, false);
            if (new FileInfo(path).Length > 1024 * 1024) return new(["File exceeds the 1 MB preview limit."], false, true);
            text = await File.ReadAllTextAsync(path, token);
            if (text.Contains('\0')) return new([], true, false);
            if (text.Length == 0) return new([], false, false);
            return Limit(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(x => "+" + x));
        }
        var args = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=3", "HEAD", "--", file.Path };
        if (file.OriginalPath is not null) args.Add(file.OriginalPath);
        text = await Run(root, token, args.ToArray());
        if (text.Contains("Binary files ") || text.Contains("GIT binary patch")) return new([], true, false);
        return Limit(text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Where(x => x.Length > 0));
    }
    private static GitFilePreview Limit(IEnumerable<string> lines)
    {
        var bounded = lines.Take(4001).ToArray();
        return new(bounded.Take(4000).ToArray(), false, bounded.Length > 4000);
    }
    public static async Task DiscardAsync(string root, GitChangedFile file, CancellationToken token = default)
    {
        var current = await Status(root, file.Path, token);
        if (!current.Any(x => x.Path == file.Path && x.Status == file.Status)) throw new IOException("File status changed. Refresh and review it again.");
        if (file.Untracked)
        {
            var path = SafePath(root, file.Path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new IOException("Only individual files can be deleted.");
            File.Delete(path);
            return;
        }
        // Renames and newly staged files need separate decisions about both paths
        // and the index. Never turn a failed restore into a delete operation.
        if (file.OriginalPath is not null || file.Label == "Added") throw new IOException("Revert renamed or newly staged files with Git; this action supports existing tracked files only.");
        await Run(root, token, "restore", "--source=HEAD", "--staged", "--worktree", "--", file.Path);
    }
    private static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Path is outside the project directory.");
        return path;
    }
    private static async Task<bool> HasHead(string root, CancellationToken token)
    {
        try { await Run(root, token, "rev-parse", "--verify", "HEAD"); return true; }
        catch (IOException) { return false; }
    }
    private static async Task<string> Run(string root, CancellationToken token, params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Couldn't start Git.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            if (process.ExitCode != 0) throw new IOException((await error).Trim());
            return text;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
