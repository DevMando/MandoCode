using System.Text;
using Microsoft.Extensions.AI;

namespace MandoCode.Services;

/// <summary>Collects actual tool responses, excluding assistant claims and terminal markers.</summary>
public static class PlanToolEvidence
{
    public static string Capture(IEnumerable<ChatMessage> messages)
    {
        var history = messages.ToList();
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.Last());
        var entries = new List<string>();
        foreach (var result in history.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            if (!calls.TryGetValue(result.CallId, out var call)) continue;
            var arguments = call.Arguments?.ToDictionary(p => p.Key, p =>
                p.Key is "content" or "new_text" or "old_text" ? (object?)"[edit body omitted; use file-read evidence]" : p.Value);
            entries.Add($"Observation {entries.Count + 1}. Tool: {call.Name}\nArguments: {System.Text.Json.JsonSerializer.Serialize(arguments)}\nResult: " +
                PlanRepositoryContext.Clip(result.Result?.ToString() ?? "", 8000));
        }
        if (entries.Count == 0) return "";
        // Equal per-call clipping used to erase the middle of small source files (including KEYMAP)
        // even when the complete set of useful results fit comfortably in the overall budget.
        return PlanRepositoryContext.Clip(string.Join("\n\n", entries), 24000);
    }

    public static IReadOnlyDictionary<string, string> SnapshotFileVersions(
        IEnumerable<ChatMessage> messages, string projectRoot, IEnumerable<string>? previousPaths = null)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var paths = (previousPaths ?? []).Concat(messages.SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>().Select(GetPath)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                if (path == "unknown path") continue;
                var full = Path.GetFullPath(Path.Combine(root, path));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(full)) continue;
                if (!File.Exists(full)) { versions[path] = "missing"; continue; }
                if (new FileInfo(full).Length > 2 * 1024 * 1024) continue;
                using var stream = File.OpenRead(full);
                versions[path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return versions;
    }

    /// <summary>A known filesystem edit invalidates earlier checks, regardless of a success claim.</summary>
    public static string? AssessFreshness(IEnumerable<ChatMessage> messages)
    {
        var history = messages.ToList();
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId).ToDictionary(g => g.Key, g => g.Last());
        FunctionCallContent? uncheckedEdit = null;
        var needsExecutionCheck = false;
        foreach (var result in history.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
        {
            if (!calls.TryGetValue(result.CallId, out var call)) continue;
            if (call.Name is "write_file" or "edit_file" or "delete_file" or "delete_folder" or "create_folder")
            {
                uncheckedEdit = call;
                var extension = Path.GetExtension(GetPath(call)).ToLowerInvariant();
                // Documentation can be checked by reading it back; code needs an executable or browser check.
                needsExecutionCheck |= extension is not (".md" or ".txt" or ".rst");
            }
            else if (call.Name is "execute_command" ||
                call.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                call.Name.Contains("browser", StringComparison.OrdinalIgnoreCase))
            {
                uncheckedEdit = null;
                needsExecutionCheck = false;
            }
            else if (!needsExecutionCheck && call.Name is ("read_file" or "read_multiple_files"))
                uncheckedEdit = null;
        }
        return uncheckedEdit == null ? null :
            $"No fresh acceptance check was observed after the final {uncheckedEdit.Name} " +
            $"({GetPath(uncheckedEdit)}). " +
            "Preserve the existing implementation and rerun the acceptance checks after this edit; earlier results cannot validate it.";
    }

    private static string GetPath(FunctionCallContent call) => call.Arguments?
        .FirstOrDefault(p => p.Key is "relativePath" or "path" or "file_path").Value?.ToString() ?? "unknown path";
}
