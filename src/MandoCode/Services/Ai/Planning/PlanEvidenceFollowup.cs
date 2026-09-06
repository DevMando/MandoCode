using Microsoft.Extensions.AI;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

/// <summary>Restricts automatic evidence collection; never grants general shell or mutation access.</summary>
public static class PlanEvidenceFollowup
{
    public static string[] CheckCommands(IEnumerable<ChatMessage> messages) => messages
        .SelectMany(m => m.Contents).OfType<FunctionCallContent>()
        .Where(c => c.Name == "execute_command")
        .Select(c => c.Arguments != null && c.Arguments.TryGetValue("command", out var command) ? command?.ToString() ?? "" : "")
        .Where(IsCheckCommand).Distinct(StringComparer.Ordinal).ToArray();

    public static bool IsCheckCommand(string command) =>
        Regex.IsMatch(command, @"^node\s+(?:--check\s+[\w./\\-]+\.(?:js|mjs|cjs)|[\w./\\-]*(?:test|spec)[\w./\\-]*\.(?:js|mjs|cjs))\s*$", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(command, @"^dotnet\s+test(?:\s+[\w./\\-]+)?(?:\s+--no-(?:restore|build))*\s*$", RegexOptions.IgnoreCase);

    public static bool Allows(string tool, IDictionary<string, object?> arguments, IReadOnlySet<string> checkCommands)
    {
        if (tool == "execute_command")
        {
            var command = arguments.TryGetValue("command", out var value) ? value?.ToString() ?? "" : "";
            return IsCheckCommand(command) && checkCommands.Contains(command);
        }
        return tool is "read_file_contents" or "read_file" or "list_all_project_files" or "list_files" or
            "list_files_match_glob_pattern" or "search_in_files" or "search_files" or "get_absolute_path" or
            "grep_files" or "search_text_in_files";
    }
}
