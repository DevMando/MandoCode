using System.Runtime.InteropServices;

namespace MandoCode.Services;

/// <summary>
/// Detects the shell <c>execute_command</c> actually invokes and produces a
/// rules-string the model can use to avoid emitting unix tools on Windows cmd
/// (or vice-versa). Called once at AIService startup and appended to the system
/// prompt so the model sees it in every turn.
/// </summary>
public static class ShellEnvironment
{
    /// <summary>
    /// Human-readable label, e.g. "cmd.exe on Windows" or "bash on Linux".
    /// </summary>
    public static string Label { get; } = DetectLabel();

    /// <summary>
    /// Block of shell-rule text to append to the system prompt.
    /// </summary>
    public static string SystemPromptRules { get; } = BuildRules();

    private static string DetectLabel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "cmd.exe on Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "bash on macOS";
        return "bash on Linux";
    }

    private static string BuildRules()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return @"SHELL COMMAND RULES (IMPORTANT):
- execute_command runs in cmd.exe on Windows. You are NOT on a Unix system.
- DO NOT use unix tools: head, tail, cat, grep, ls, find, awk, sed, wc — none of these exist on cmd.exe.
- Windows equivalents: use 'type' (not cat), 'dir' (not ls), 'findstr' (not grep), 'more' (not less).
- PREFER MandoCode's file tools for reading/searching source files:
    • Use read_file_contents instead of 'type' or 'cat'
    • Use search_text_in_files instead of 'findstr' or 'grep'
    • Use list_files_match_glob_pattern instead of 'dir' or 'ls'
  These are faster, structured, and cross-platform.
- Chaining: '&&' and '||' work in cmd.exe. '&' also chains unconditionally.
- Paths: backslashes work; forward slashes work in most modern Windows tools but quote paths with spaces.
- execute_command has a 30-second timeout — don't start long-running servers or watchers.";
        }

        return @"SHELL COMMAND RULES:
- execute_command runs in bash on Unix. Standard POSIX tools are available: head, tail, cat, grep, ls, find, awk, sed.
- '&&', '||', ';' all work for command chaining.
- Filesystem is case-sensitive; forward slashes only.
- PREFER MandoCode's file tools where possible (read_file_contents, search_text_in_files, list_files_match_glob_pattern) — faster than shelling out.
- execute_command has a 30-second timeout — don't start long-running servers or watchers.";
    }
}
