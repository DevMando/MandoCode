using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MandoCode.Models;
using YamlDotNet.Serialization;

namespace MandoCode.Services;

/// <summary>Manages user skills with the Desktop-compatible disabled-file convention.</summary>
public sealed class SkillManagementStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public sealed record Entry(string Name, string Description, string Body, string Folder, bool Enabled, string? Error);
    public sealed record InstallResult(IReadOnlyList<string> Installed, IReadOnlyList<string> Skipped);

    public IReadOnlyList<Entry> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateDirectories(Root).Where(folder => !Path.GetFileName(folder).StartsWith('.') && !IsLink(folder))
            .Select(folder =>
            {
                var enabled = File.Exists(Path.Combine(folder, "SKILL.md"));
                var file = Path.Combine(folder, enabled ? "SKILL.md" : "SKILL.md.disabled");
                if (!File.Exists(file) || IsLink(file)) return null;
                var skill = SkillParser.ParseFile(file, SkillSource.User, out var error);
                return new Entry(skill?.Name ?? Path.GetFileName(folder), skill?.Description ?? "", skill?.Body ?? "", folder, enabled, error);
            }).OfType<Entry>().OrderByDescending(entry => entry.Enabled).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string Slug(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0 || slug.Length > 80 || new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(slug))
            throw new ArgumentException("Use a name with letters or numbers, up to 80 characters, that can be used as a folder name.");
        return slug;
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private string CheckFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || Path.GetFileName(full).StartsWith('.') || (Directory.Exists(full) && IsLink(full)))
            throw new IOException("Select a skill directly inside the user skills directory.");
        return full;
    }

    public string Save(string? original, string name, string description, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Skill instructions cannot be empty.");
        var target = CheckFolder(Path.Combine(Root, Slug(name)));
        original = original is null ? null : CheckFolder(original);
        if (Directory.Exists(target) && !string.Equals(original, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("A skill with this folder name already exists. Edit it instead.");
        var disabled = original is not null && !File.Exists(Path.Combine(original, "SKILL.md")) && File.Exists(Path.Combine(original, "SKILL.md.disabled"));
        Directory.CreateDirectory(Root);
        if (original is not null && !string.Equals(original, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) Directory.Move(original, target);
        Directory.CreateDirectory(target);
        var file = Path.Combine(target, disabled ? "SKILL.md.disabled" : "SKILL.md");
        if (File.Exists(file) && IsLink(file)) throw new IOException("Linked skill files cannot be edited.");
        var yaml = new SerializerBuilder().Build().Serialize(new Dictionary<string, string> { ["name"] = name.Trim(), ["description"] = description.Trim() });
        File.WriteAllText(file, "---\n" + yaml + "---\n\n" + body.Trim() + "\n", new UTF8Encoding(false));
        return target;
    }

    public void SetEnabled(string folder, bool enabled)
    {
        folder = CheckFolder(folder);
        var source = Path.Combine(folder, enabled ? "SKILL.md.disabled" : "SKILL.md");
        var target = Path.Combine(folder, enabled ? "SKILL.md" : "SKILL.md.disabled");
        if (!File.Exists(source)) return;
        if (IsLink(source) || File.Exists(target)) throw new IOException("Conflicting or linked skill files. Resolve them before toggling this skill.");
        if (enabled && SkillParser.ParseFile(source, SkillSource.User, out var error) is null) throw new IOException("Fix the skill before enabling it: " + error);
        File.Move(source, target);
    }

    public string Remove(string folder)
    {
        folder = CheckFolder(folder);
        var trash = Path.Combine(Root, ".trash");
        Directory.CreateDirectory(trash);
        if (IsLink(trash)) throw new IOException("The skill trash directory cannot be a link.");
        var destination = Path.Combine(trash, Path.GetFileName(folder) + "-" + Guid.NewGuid().ToString("N"));
        Directory.Move(folder, destination);
        return destination;
    }

    public async Task<InstallResult> InstallAsync(string source, CancellationToken cancellationToken)
    {
        source = source.Trim().Trim('"');
        var staging = Path.Combine(Path.GetTempPath(), "mandocode-skill-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var search = source;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" || source.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                search = Path.Combine(staging, "repo");
                var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "clone", "--depth", "1", "--", source, search }) start.ArgumentList.Add(arg);
                start.Environment["GIT_TERMINAL_PROMPT"] = "0";
                using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");
                var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errors = process.StandardError.ReadToEndAsync(cancellationToken);
                try { await process.WaitForExitAsync(cancellationToken); }
                catch { try { process.Kill(true); } catch { } throw; }
                await output;
                var error = await errors;
                if (process.ExitCode != 0) throw new IOException("Git clone failed: " + error.Trim());
            }
            else if (File.Exists(source) && source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(source);
                if (archive.Entries.Count > 10000 || archive.Entries.Sum(e => e.Length) > 100_000_000) throw new IOException("This ZIP is too large (maximum 100 MB and 10,000 entries).");
                search = Path.Combine(staging, "zip");
                archive.ExtractToDirectory(search);
            }
            else if (!Directory.Exists(source)) throw new IOException("Enter an existing folder, ZIP file, or Git repository URL.");
            var folders = new List<string>();
            Scan(search, 0, folders);
            var installed = new List<string>(); var skipped = new List<string>();
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var skill = SkillParser.ParseFile(Path.Combine(folder, "SKILL.md"), SkillSource.User, out var error);
                if (skill is null) { skipped.Add(Path.GetFileName(folder) + ": " + error); continue; }
                var target = CheckFolder(Path.Combine(Root, Slug(skill.Name)));
                if (Directory.Exists(target)) { skipped.Add(skill.Name + " (already installed)"); continue; }
                Directory.CreateDirectory(Root);
                // Stage on the destination volume, then publish the complete skill atomically.
                var copy = Path.Combine(Root, ".install-" + Guid.NewGuid().ToString("N"));
                try { Copy(folder, copy, cancellationToken, new CopyBudget()); cancellationToken.ThrowIfCancellationRequested(); Directory.Move(copy, target); }
                finally { if (Directory.Exists(copy) && !IsLink(copy)) Directory.Delete(copy, true); }
                installed.Add(skill.Name);
            }
            return new(installed, skipped);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };
    private static void Scan(string folder, int depth, List<string> found)
    {
        if (depth > 6 || IsLink(folder)) return;
        var file = Path.Combine(folder, "SKILL.md");
        if (File.Exists(file)) { if (!IsLink(file)) found.Add(folder); return; }
        foreach (var child in Directory.EnumerateDirectories(folder)) if (!Ignored.Contains(Path.GetFileName(child))) Scan(child, depth + 1, found);
    }
    private sealed class CopyBudget { public long Bytes; public int Files; }
    private static void Copy(string source, string target, CancellationToken token, CopyBudget budget, int depth = 0)
    {
        if (depth > 20 || IsLink(source)) throw new IOException("Skill folders cannot contain symbolic links or exceed 20 levels.");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            if (IsLink(file)) throw new IOException("Skill files cannot be symbolic links.");
            budget.Bytes += new FileInfo(file).Length;
            if (++budget.Files > 10000 || budget.Bytes > 100_000_000) throw new IOException("Skill is too large to install.");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
            if (!Ignored.Contains(Path.GetFileName(folder))) Copy(folder, Path.Combine(target, Path.GetFileName(folder)), token, budget, depth + 1);
    }
}
