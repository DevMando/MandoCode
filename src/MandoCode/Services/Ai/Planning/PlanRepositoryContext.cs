using System.Text;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

/// <summary>Bounded, read-only grounding. Never follows links or reads credential files.</summary>
public static class PlanRepositoryContext
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", "bin", "obj", "node_modules", "packages", "dist", "build", ".venv" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".razor", ".csproj", ".sln", ".ts", ".tsx", ".js", ".jsx", ".py", ".md", ".toml" };

    public static string Clip(string text, int budget)
    {
        if (text.Length <= budget) return text;
        const string gap = "\n...[excerpt]...\n";
        var half = Math.Max(1, (budget - gap.Length) / 2);
        return text[..half] + gap + text[^half..];
    }

    public static string Capture(string root, string request, CancellationToken ct = default, int maxChars = 8000)
    {
        var files = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((Path.GetFullPath(root), 0));
        var visited = 0;
        while (queue.Count > 0 && files.Count < 300 && visited++ < 100)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(500))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var name = Path.GetFileName(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (depth < 4 && !Excluded.Contains(name) && !name.StartsWith('.')) queue.Enqueue((entry, depth + 1));
                    }
                    else if (Extensions.Contains(Path.GetExtension(entry)) &&
                        !name.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("credential", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(entry);
                        if (files.Count >= 300) break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var sb = new StringBuilder($"Project root: {Path.GetFullPath(root)}\nPartial source inventory:\n");
        foreach (var file in files) sb.AppendLine(Path.GetRelativePath(root, file));
        var terms = Regex.Matches(request, @"[a-zA-Z][a-zA-Z0-9_]{3,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = files.OrderByDescending(f =>
            (Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase) ? 10 : 0) +
            (Path.GetExtension(f) == ".csproj" ? 8 : 0) +
            terms.Count(t => Path.GetRelativePath(root, f).Contains(t, StringComparison.OrdinalIgnoreCase)) * 3).Take(6);
        foreach (var file in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var reader = File.OpenText(file);
                var buffer = new char[1600];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                sb.AppendLine($"\nFile excerpt: {Path.GetRelativePath(root, file)}\n{new string(buffer, 0, count)}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Clip(sb.ToString(), maxChars);
    }
}
