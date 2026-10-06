namespace MandoCode.Services;

/// <summary>Action icons use tool identity rather than user-controlled arguments.</summary>
public static class ToolActivityIcons
{
    public static string For(string functionName, bool isMcp = false)
    {
        if (isMcp) return "🔌";
        var name = functionName.ToLowerInvariant();
        if (name.StartsWith("filesystem_")) name = name[11..];
        if (name.StartsWith("websearch_")) name = name[10..];
        if (name.StartsWith("skills_")) name = name[7..];
        return name switch
        {
            "search_web" => "🔎",
            "fetch_webpage" => "🌐",
            "load_skill" => "🧠",
            "read_file_contents" or "list_all_project_files" or "list_files_match_glob_pattern"
                or "grep_files" or "search_text_in_files" or "get_absolute_path" => "📄",
            "write_file" or "edit_file" or "create_folder" => "✏️",
            "delete_file" or "delete_folder" => "🗑️",
            _ => "⚡"
        };
    }
}
