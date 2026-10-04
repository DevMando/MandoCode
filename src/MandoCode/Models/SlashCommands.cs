namespace MandoCode.Models;

/// <summary>
/// Single source of truth for built-in slash commands and their descriptions.
/// Consumed by both the input state machine (Program.cs) and the autocomplete
/// renderer (CommandAutocomplete) — keep all new commands here so the two
/// surfaces can't drift.
/// </summary>
public static class SlashCommands
{
    public static bool IsAvailable(string command, MandoCodeConfig config) => command switch
    {
        "/agent-dim" => false,
        "/agent-dim-on" => !config.DimUnfocusedAgents,
        "/agent-dim-off" => config.DimUnfocusedAgents,
        "/tips-on" => !config.ShowTips,
        "/tips-off" => config.ShowTips,
        _ => true
    };
    /// <summary>
    /// Command name → description. Keys include the leading slash.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        { "/help", "Show this help message" },
        { "/keybindings", "Show keyboard shortcuts grouped by task" },
        { "/agent-new", $"Open another independent agent pane ({Keybindings.AgentNew})" },
        { "/agent-settings", $"Customize this agent's model settings, behavior, and integrations ({Keybindings.AgentSettings})" },
        { "/agent-file-explorer", $"Show or hide this agent's files and folders ({Keybindings.AgentFileExplorer})" },
        { "/git-changes", $"View this agent's changed files and Git diffs ({Keybindings.AgentGitChanges})" },
        { "/agent-focus", $"Switch agents (optional: left/right) ({Keybindings.AgentFocus})" },
        { "/agent-close", $"Close the current idle agent pane ({Keybindings.AgentClose})" },
        { "/agent-rename", "Rename this agent: /agent-rename <name>" },
        { "/agent-dim", "Toggle grayscale for unfocused agents (optional: on/off)" },
        { "/agent-dim-off", "Keep unfocused agents in full color" },
        { "/agent-dim-on", "Show unfocused agents in grayscale" },
        { "/tips-off", "Hide helpful tips in the CLI" },
        { "/tips-on", "Show helpful tips in the CLI" },
        { "/workspace-new", $"Open a workspace (optional: name), with up to four agents ({Keybindings.WorkspaceNew})" },
        { "/workspace", $"Switch workspace (optional: name or tab number) ({Keybindings.WorkspaceSwitch})" },
        { "/workspace-all", $"Find a workspace in the All picker ({Keybindings.WorkspaceAll})" },
        { "/workspace-rename", "Rename this workspace: /workspace-rename <name>" },
        { "/workspace-close", $"Close this workspace when all its agents are idle ({Keybindings.WorkspaceClose})" },
        { "/setup", "Reconnect to Ollama or pick a different model (guided wizard)" },
        { "/model", "Quick switch — pick a different model" },
        { "/change-directory", $"Change only this agent's directory (optional: path) ({Keybindings.ChangeDirectory})" },
        { "/config", "Adjust settings — guided wizard (model, temperature, tokens, context window, timeout)" },
        { "/config set", "Set one setting inline (usage: /config set modelResponseTimeout 300)" },
        { "/copy", "Copy last AI response to clipboard" },
        { "/copy-code", "Copy code blocks from last AI response" },
        { "/command", "Run a shell command (also: !<cmd>)" },
        { "/plan", "Force planning for a goal, or show an unfinished plan" },
        { "/plan-resume", "Continue an unfinished plan where it left off" },
        { "/plan-discard", "Forget an unfinished plan" },
        { "/compact", "Compress conversation context into a recap (keeps this transcript)" },
        { "/clear", "Wipe all conversation context and start fresh" },
        { "/history", $"Find and restore closed agents, transcripts, and conversation context ({Keybindings.AgentHistory})" },
        { "/ollama-serve", "Start local Ollama and retry the connection twice" },
        { "/learn", "Learn about LLMs and local AI models" },
        { "/retry", "Retry Ollama connection" },
        { "/update", "Check for a stable release and update MandoCode after closing the CLI" },
        { "/music", "Play music" },
        { "/music-stop", "Stop music playback" },
        { "/music-pause", "Pause/resume music" },
        { "/music-next", "Skip to next track" },
        { "/music-vol", "Set volume (0-100), e.g. /music-vol 70" },
        { "/music-playlist", "Select a genre and start playing" },
        { "/music-list", "Show available tracks" },
        { "/skills", "List installed skills (auto-invoked by the model when relevant)" },
        { "/force-skill", "Override: force a specific skill to run now" },
        { "/mcp", "List configured MCP servers with status and tool counts" },
        { "/mcp add", "Interactively add a new MCP server to config" },
        { "/mcp remove", "Remove an MCP server from config (usage: /mcp remove <name>)" },
        { "/mcp tools", "List tools exposed by connected MCP servers (usage: /mcp tools <server>)" },
        { "/mcp-reload", "Restart all MCP servers and re-register their tools" },
        { "/exit", "Exit MandoCode" }
    };
}
