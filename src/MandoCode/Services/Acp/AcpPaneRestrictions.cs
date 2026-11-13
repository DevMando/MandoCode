namespace MandoCode.Services.Acp;

/// <summary>
/// The one place that says what an ACP agent pane does not offer: commands, Alt shortcuts and the <c>@</c> picker's
/// Agents group. Autocomplete, the command dispatcher, the shortcut router and the composer all read from here, so a
/// restriction cannot be hidden in one surface and still reachable through another. Nothing in this class names an
/// adapter: these features belong to MandoCode's own model loop, its own tools, or its native history.
/// </summary>
public static class AcpPaneRestrictions
{
    private const string ModelLoop = "the external agent runs the conversation, so MandoCode's own model settings do not apply";
    private const string NativeTools = "the external agent does not use MandoCode's skills or MCP servers";
    private const string NativeHistory = "saved history restores native MandoCode agents, and ACP panes are not saved to it";

    /// <summary>Command name (no slash) → why it is unavailable in an ACP pane.</summary>
    private static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["setup"] = ModelLoop,
        ["retry"] = ModelLoop,
        ["config"] = ModelLoop,
        ["agent-settings"] = ModelLoop,
        ["force-skill"] = ModelLoop,
        ["plan"] = ModelLoop,
        ["plan-resume"] = ModelLoop,
        ["plan-discard"] = ModelLoop,
        ["context-snap-create"] = ModelLoop,
        ["context-snap-import"] = ModelLoop,
        ["skills"] = NativeTools,
        ["mcp"] = NativeTools,
        ["mcp-reload"] = NativeTools,
        ["history"] = NativeHistory,
        ["agent-history"] = NativeHistory,
    };

    /// <summary>Alt+letter → the command that letter normally opens. Pressing it in an ACP pane gives that command's explanation.</summary>
    private static readonly IReadOnlyDictionary<string, string> Shortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["s"] = "/agent-settings",
        ["c"] = "/context-snap-import",
        ["k"] = "/skills",
        ["m"] = "/mcp",
        ["h"] = "/history",
    };

    /// <summary>The Alt+V image paste: an ACP prompt carries text and file links only for now.</summary>
    public const string ImagePasteReason = "images are not sent to ACP agents yet";

    /// <summary>The <c>@</c> picker lists files only; MandoCode's agents are not reachable from an external agent.</summary>
    public static bool PickerShowsAgents => false;

    /// <param name="command">Dispatch text, with or without the leading slash (<c>"mcp tools x"</c>, <c>"/plan fix it"</c>).</param>
    public static bool IsCommandBlocked(string command) => ReasonFor(command) is not null;

    public static string? ReasonFor(string command)
    {
        var name = NameOf(command);
        if (Commands.TryGetValue(name, out var reason)) return reason;
        return name.StartsWith("ollama-", StringComparison.Ordinal) ? ModelLoop : null;
    }

    /// <summary>
    /// Whether the <c>/</c> popup of an ACP pane lists <paramref name="command"/>. <c>/model</c> and <c>/compact</c> only
    /// work when the agent reports a model option or a compact command, so they are listed only then; everything else
    /// the popup offers is always available. Typing a command that is not listed still answers with an explanation.
    /// </summary>
    public static bool IsListed(string command, IAcpAgentRuntime? runtime) => NameOf(command) switch
    {
        "model" => runtime is not null && AcpPaneCommands.ModelOption(runtime) is not null,
        "compact" => runtime is not null && AcpPaneCommands.AgentOffersCompact(runtime),
        _ => true,
    };

    /// <summary>Popup descriptions that say what these commands do in an ACP pane (they are not the native ones).</summary>
    public static IReadOnlyDictionary<string, string> DescribeCommands(IReadOnlyDictionary<string, string> native)
    {
        var described = native.ToDictionary(kv => kv.Key, kv => kv.Value);
        described["/clear"] = "Start a new ACP conversation in this agent (the native conversation is untouched)";
        described["/model"] = "Pick the agent's model";
        described["/compact"] = "Ask the agent to compact its conversation";
        described["/agent-acp-mode"] = "Choose the agent's permissions and behavior mode";
        return described;
    }

    /// <summary>The explanation printed when a blocked command is typed or reached through a shortcut.</summary>
    public static string Message(string command) =>
        $"/{NameOf(command)} is not available in an ACP agent pane: {ReasonFor(command) ?? "it belongs to the native agent"}.";

    /// <summary>The command an Alt+<paramref name="key"/> shortcut stands for, when that shortcut is blocked here.</summary>
    public static string? BlockedShortcutCommand(string key) => Shortcuts.TryGetValue(key, out var command) ? command : null;

    public static bool IsImagePasteBlocked(string key) => key.Equals("v", StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string command) =>
        command.TrimStart('/').Split(' ', 2)[0].ToLowerInvariant();
}
