using System.Security.Cryptography;
using System.Text;

namespace MandoCode.Services;

/// <summary>
/// Backing store for `mandocode --continue`: one most-recent-session file per project root
/// under ~/.mandocode/sessions/, holding the JSON that AIService.ExportHistoryJson produces.
/// Written whole-file (write-then-rename, so a crash can't tear a good save) at the end of
/// every turn; reloaded via TryRestoreHistoryJson when the user asks to continue.
///
/// The per-project layout (leaf name + path hash) deliberately mirrors tools like Claude
/// Code, and leaves room for a future --resume picker: multiple files per project instead
/// of one. Best-effort everywhere — persistence must never break the chat.
/// </summary>
public static class SessionResumeStore
{
    /// <summary>Safety valve — the harness's own compaction keeps live histories far
    /// smaller than this in practice.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "sessions");

    /// <summary>Stable file path for a project root: readable leaf + hash of the full,
    /// case-normalized path (two folders named "api" must not collide).</summary>
    public static string PathFor(string projectRoot)
    {
        var full = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..12];
        var leaf = new string(Path.GetFileName(full).Where(char.IsLetterOrDigit).Take(24).ToArray());
        return Path.Combine(Folder, leaf.Length > 0 ? $"{leaf}-{hash}.json" : $"{hash}.json");
    }

    public static void Save(string projectRoot, string? historyJson)
    {
        try
        {
            if (string.IsNullOrEmpty(historyJson) || historyJson.Length > MaxBytes) return;
            Directory.CreateDirectory(Folder);
            var path = PathFor(projectRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, historyJson);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    public static string? Load(string projectRoot)
    {
        try
        {
            var path = PathFor(projectRoot);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>/clear forgets the stored session too — cleared means cleared.</summary>
    public static void Delete(string projectRoot)
    {
        try { File.Delete(PathFor(projectRoot)); } catch { }
    }
}
