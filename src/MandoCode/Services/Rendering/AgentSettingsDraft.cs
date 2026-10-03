using MandoCode.Models;
using System.Text.Json;

namespace MandoCode.Services;

public sealed class AgentSettingsDraft
{
    public sealed record Field(string Group, string Key, string Label, string Description, Func<MandoCodeConfig, string> Read, bool Toggle = false);
    public static readonly Field[] Fields =
    [
        new("Model", "temperature", "Temperature", "Use 0 to 1. Lower values keep wording focused; higher values make answers more varied. Try 0.2 for precise code edits or 0.7 for general use.", c => c.Temperature.ToString()),
        new("Model", "maxTokens", "Max response tokens", "Maximum length of one reply. Raise this if code is cut off; lower it for shorter replies. Valid range: 256 to 65,536 tokens.", c => c.MaxTokens.ToString()),
        new("Model", "contextLength", "Context window", "How much conversation and file content a local model can use at once. Larger windows need more memory. Auto sizes it for the model; 0 uses Ollama's default.", c => c.ContextLengthSetByUser ? c.ContextLength.ToString() : "auto"),
        new("Model", "streaming", "Response streaming", "All receives replies in chunks from every model. Cloud does this only for cloud models. Off waits for a complete reply. All helps track progress during long replies.", c => c.ResponseStreaming),
        new("Behavior", "diffApprovals", "Diff approvals", "On asks you to review file writes, deletions, and commands. Off lets the agent apply them without asking.", c => c.EnableDiffApprovals.ToString(), true),
        new("Behavior", "autoContinue", "Auto-continue", "Keep working automatically when a request reaches its tool-output budget. Max continuations limits how many extra rounds are allowed.", c => c.EnableAutoContinuation.ToString(), true),
        new("Behavior", "maxContinuations", "Max continuations", "Maximum extra rounds allowed after a tool-budget limit. A smaller number helps stop tasks that keep continuing without finishing.", c => c.MaxAutoContinuations.ToString()),
        new("Behavior", "timeout", "Request timeout (minutes)", "Maximum time for the whole request, including tools.", c => c.RequestTimeoutMinutes.ToString()),
        new("Behavior", "modelResponseTimeout", "Stall watchdog (seconds)", "How long a model may stay silent before its reply is cancelled. Raise this if a slow model needs more time to think.", c => c.ModelResponseTimeoutSeconds.ToString()),
        new("Behavior", "toolBudget", "Tool-result character budget", "Maximum file contents and command output sent back to the model in one request. Higher limits show it more information but use more context.", c => c.ToolResultCharBudget.ToString()),
        new("Behavior", "renderTimeout", "Render timeout (seconds)", "Formatting time before a reply falls back to plain text.", c => c.MarkdownRenderTimeoutSeconds.ToString()),
        new("Integrations", "webSearch", "Web search", "Allow this agent to search the web for current information.", c => c.EnableWebSearch.ToString(), true),
        new("Integrations", "mcp", "MCP tools", "Use configured MCP servers for this agent. Manage servers with /mcp.", c => c.EnableMcp.ToString(), true)
    ];
    public MandoCodeConfig Config { get; }
    public AgentSettingsDraft(MandoCodeConfig source)
        => Config = JsonSerializer.Deserialize<MandoCodeConfig>(JsonSerializer.Serialize(source))!;
    public ConfigKeySetter.SetResult Set(Field field, string value) => ConfigKeySetter.TrySet(Config, field.Key, value);
    public static string[] Presets(Field field) => field.Key switch
    {
        "temperature" => new[] { 0.0, 0.2, 0.4, 0.7, 1.0 }.Select(value => value.ToString()).ToArray(),
        "maxTokens" => ["1024", "4096", "8192", "16384", "32768", "65536"],
        "contextLength" => ["auto", "0", "4096", "8192", "16384", "32768", "65536", "131072", "262144"],
        "streaming" => ["all", "cloud", "off"],
        "maxContinuations" => ["0", "1", "3", "5", "10"],
        "timeout" => ["5", "10", "30", "60"],
        "modelResponseTimeout" => ["60", "180", "300", "680", "1200"],
        "toolBudget" => ["50000", "100000", "250000", "500000", "1000000"],
        "renderTimeout" => ["15", "30", "60", "120"],
        _ => []
    };
    public static string[] OrderedPresets(Field field, MandoCodeConfig config)
    {
        var values = Presets(field).Append(field.Read(config)).Distinct().ToArray();
        if (field.Key == "streaming") return values;
        return values.OrderBy(value => value == "auto" ? double.NegativeInfinity :
            double.TryParse(value, out var number) ? number : double.PositiveInfinity).ToArray();
    }

    public static string[] Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && RazorConsole.Core.Input.TextSelectionState.CellWidth(line + " " + word) > width)
            { lines.Add(line); line = ""; }
            if (line.Length > 0) line += " ";
            foreach (var rune in word.EnumerateRunes())
            {
                if (RazorConsole.Core.Input.TextSelectionState.CellWidth(line + rune) > width && line.Length > 0)
                { lines.Add(line); line = ""; }
                line += rune;
            }
        }
        if (line.Length > 0) lines.Add(line);
        return lines.Count == 0 ? [""] : lines.ToArray();
    }
    public static Dictionary<string, string> Capture(MandoCodeConfig config)
        => Fields.ToDictionary(field => field.Key, field => field.Read(config));
    public static void Restore(MandoCodeConfig config, Dictionary<string, string>? options)
    {
        if (options is null) return;
        var defaults = config.DefaultAgentOptions;
        config.DefaultAgentOptions = null;
        try
        {
            foreach (var field in Fields)
                if (options.TryGetValue(field.Key, out var value)) ConfigKeySetter.TrySet(config, field.Key, value);
        }
        finally { config.DefaultAgentOptions = defaults; }
    }
    public void ApplyTo(MandoCodeConfig target)
    {
        Restore(target, Capture(Config));
    }
}
