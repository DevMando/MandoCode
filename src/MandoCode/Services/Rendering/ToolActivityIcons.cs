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
            "list_agents" or "get_agent_status" or "read_agent_transcript" or "ask_agent" or "ask_agent_and_wait" or "ask_agent_async" or "send_agent_message" or "delegate_to_agent" or "request_agent_review" or "handoff_to_agent" or "check_delegations" or "update_agent_job" or "cancel_agent_job" or "wait_for_agent_job" => "🤖",
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
    public static string Label(string name, string fallback) => name switch
    {
        "ask_agent_and_wait" => "Asking another agent · waiting for reply",
        "ask_agent_async" => "Sending a question · reply will arrive later",
        "send_agent_message" => "Sending an agent inbox message · no reply requested",
        "delegate_to_agent" => "Assigning background work",
        "request_agent_review" => "Requesting a background review",
        "handoff_to_agent" => "Transferring task responsibility",
        "update_agent_job" => "Queuing an interaction update",
        "cancel_agent_job" => "Requesting interaction cancellation",
        "wait_for_agent_job" => "Waiting for a background result",
        "check_delegations" => "Checking background interactions",
        _ => fallback
    };
}
