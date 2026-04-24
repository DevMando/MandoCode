namespace MandoCode.Models;

/// <summary>
/// Single source of truth for built-in slash commands and their descriptions.
/// Consumed by both the input state machine (Program.cs) and the autocomplete
/// renderer (CommandAutocomplete) — keep all new commands here so the two
/// surfaces can't drift.
/// </summary>
public static class SlashCommands
{
    /// <summary>
    /// Command name → description. Keys include the leading slash.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        { "/help", "Show this help message" },
        { "/config", "Open configuration menu" },
        { "/copy", "Copy last AI response to clipboard" },
        { "/copy-code", "Copy code blocks from last AI response" },
        { "/command", "Run a shell command (also: !<cmd>)" },
        { "/clear", "Clear conversation history" },
        { "/learn", "Learn about LLMs and local AI models" },
        { "/retry", "Retry Ollama connection" },
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
