namespace ArdinCode.Models;

/// <summary>
/// Single implementation behind both the CLI (`ardincode --config set`) and the in-app
/// `/config set` command, so key names, aliases, and validation can never drift apart.
/// (They already had: the stall-watchdog error recommended `--config set
/// modelResponseTimeout 300` for months while the CLI had no such key.)
/// Returns the outcome instead of printing — each entry point renders it its own way.
/// </summary>
public static class ConfigKeySetter
{
    /// <summary>
    /// When a successful set actually takes effect. The in-app command uses this to
    /// apply changes (kernel rebuild) or set expectations (restart-scoped keys);
    /// the CLI ignores it — nothing is running, everything applies on next launch.
    /// </summary>
    public enum ApplyScope
    {
        /// <summary>Read fresh every turn — live as soon as it's saved.</summary>
        Immediate,
        /// <summary>Baked into the kernel/settings at build time — rebuild applies it without losing history.</summary>
        KernelRebuild,
        /// <summary>Wired at app startup — needs a ArdinCode restart.</summary>
        AppRestart,
        /// <summary>Applied via OLLAMA_CONTEXT_LENGTH when ArdinCode starts the Ollama daemon.</summary>
        DaemonRestart
    }

    /// <param name="PostSetValidation">
    /// Optional async check the caller runs AFTER saving (e.g. a live Tavily API probe
    /// for tavilyKey) and prints the returned message. Lives on the result rather than
    /// inside TrySet so the setter stays synchronous and shared between the CLI and the
    /// in-app command; the save is never gated on it — the user may simply be offline.
    /// </param>
    public sealed record SetResult(bool Ok, string Message, ApplyScope Scope = ApplyScope.Immediate, Func<Task<string>>? PostSetValidation = null);

    private static SetResult Fail(string message) => new(false, message);

    public static SetResult TrySet(ArdinCodeConfig config, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "endpoint":
            case "apiendpoint":
            case "apiurl":
                config.ApiEndpoint = value;
                return new(true, $"✓ Set API endpoint to: {value}", ApplyScope.KernelRebuild);

            case "apikey":
            case "key":
                if (string.IsNullOrWhiteSpace(value) || value.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    config.ApiKey = null;
                    return new(true, "✓ API Key cleared", ApplyScope.KernelRebuild);
                }
                config.ApiKey = value.Trim();
                return new(true, $"✓ Set API Key to: {ArdinCodeConfig.MaskApiKey(value)}", ApplyScope.KernelRebuild);

            case "model":
            case "modelname":
                config.ModelName = value;
                return new(true, $"✓ Set model to: {value}", ApplyScope.KernelRebuild);

            case "modelpath":
                config.ModelPath = value;
                return new(true, $"✓ Set model path to: {value}", ApplyScope.KernelRebuild);

            case "temperature":
                if (double.TryParse(value, out var temp) && ArdinCodeConfig.IsValidTemperature(temp))
                {
                    config.Temperature = temp;
                    return new(true, $"✓ Set temperature to: {temp}", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Temperature must be a number between {ArdinCodeConfig.MinTemperature} and {ArdinCodeConfig.MaxTemperature}");

            case "maxtokens":
                if (int.TryParse(value, out var tokens) && ArdinCodeConfig.IsValidMaxTokens(tokens))
                {
                    config.MaxTokens = tokens;
                    return new(true, $"✓ Set max response tokens to: {tokens}", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Max tokens must be between {ArdinCodeConfig.MinMaxTokens} and {ArdinCodeConfig.MaxMaxTokens}");

            case "modelresponsetimeout":
            case "modelresponsetimeoutseconds":
            case "watchdog":
                if (int.TryParse(value, out var stallSecs) && ArdinCodeConfig.IsValidModelResponseTimeout(stallSecs))
                {
                    config.ModelResponseTimeoutSeconds = stallSecs;
                    return new(true, $"✓ Set stall watchdog to: {stallSecs}s per model call");
                }
                return Fail($"Error: Stall watchdog must be between {ArdinCodeConfig.MinModelResponseTimeoutSeconds} and {ArdinCodeConfig.MaxModelResponseTimeoutSeconds} seconds");

            case "autocontinue":
            case "autocontinuation":
            case "enableautocontinuation":
                if (bool.TryParse(value, out var enableAutoCont))
                {
                    config.EnableAutoContinuation = enableAutoCont;
                    return new(true, $"✓ Auto-continuation {(enableAutoCont ? "enabled" : "disabled")}");
                }
                return Fail("Error: Value must be 'true' or 'false'");

            case "maxcontinuations":
            case "maxautocontinuations":
                if (int.TryParse(value, out var maxCont) && ArdinCodeConfig.IsValidMaxAutoContinuations(maxCont))
                {
                    config.MaxAutoContinuations = maxCont;
                    return new(true, $"✓ Max auto-continuations set to: {maxCont}");
                }
                return Fail($"Error: Max continuations must be between {ArdinCodeConfig.MinMaxAutoContinuations} and {ArdinCodeConfig.MaxMaxAutoContinuations}");

            case "toolbudget":
            case "toolresultbudget":
            case "toolresultcharbudget":
                if (long.TryParse(value, out var budget) && ArdinCodeConfig.IsValidToolResultCharBudget(budget))
                {
                    config.ToolResultCharBudget = budget;
                    return new(true, $"✓ Set tool-result budget to: {budget:N0} chars (~{budget / 4:N0} tokens)", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Tool-result budget must be between {ArdinCodeConfig.MinToolResultCharBudget:N0} and {ArdinCodeConfig.MaxToolResultCharBudget:N0} chars");

            case "timeout":
            case "requesttimeout":
            case "requesttimeoutminutes":
                if (int.TryParse(value, out var timeout) && ArdinCodeConfig.IsValidRequestTimeout(timeout))
                {
                    config.RequestTimeoutMinutes = timeout;
                    return new(true, $"✓ Set request timeout to: {timeout} min");
                }
                return Fail($"Error: Timeout must be between {ArdinCodeConfig.MinRequestTimeoutMinutes} and {ArdinCodeConfig.MaxRequestTimeoutMinutes} minutes");

            case "taskplanning":
            case "enabletaskplanning":
                if (bool.TryParse(value, out var enablePlanning))
                {
                    config.EnableTaskPlanning = enablePlanning;
                    return new(true, $"✓ Task planning {(enablePlanning ? "enabled" : "disabled")}", ApplyScope.KernelRebuild);
                }
                return Fail("Error: Value must be 'true' or 'false'");

            case "diffapprovals":
            case "enablediffapprovals":
                if (bool.TryParse(value, out var enableDiff))
                {
                    config.EnableDiffApprovals = enableDiff;
                    return new(true, $"✓ Diff approvals {(enableDiff ? "enabled" : "disabled")}", ApplyScope.AppRestart);
                }
                return Fail("Error: Value must be 'true' or 'false'");

            case "themecustomization":
            case "enablethemecustomization":
                if (bool.TryParse(value, out var enableTheme))
                {
                    config.EnableThemeCustomization = enableTheme;
                    return new(true, $"✓ Theme customization {(enableTheme ? "enabled" : "disabled")}", ApplyScope.AppRestart);
                }
                return Fail("Error: Value must be 'true' or 'false'");

            case "websearch":
            case "enablewebsearch":
                if (bool.TryParse(value, out var enableWebSearch))
                {
                    config.EnableWebSearch = enableWebSearch;
                    return new(true, $"✓ Web search {(enableWebSearch ? "enabled" : "disabled")}", ApplyScope.KernelRebuild);
                }
                return Fail("Error: Value must be 'true' or 'false'");

            case "rendertimeout":
            case "markdownrendertimeout":
            case "markdownrendertimeoutseconds":
                if (int.TryParse(value, out var renderTimeout) && ArdinCodeConfig.IsValidMarkdownRenderTimeout(renderTimeout))
                {
                    config.MarkdownRenderTimeoutSeconds = renderTimeout;
                    return new(true, $"✓ Set markdown render timeout to: {renderTimeout}s");
                }
                return Fail($"Error: Render timeout must be between {ArdinCodeConfig.MinMarkdownRenderTimeoutSeconds} and {ArdinCodeConfig.MaxMarkdownRenderTimeoutSeconds} seconds");

            case "tavilykey":
            case "tavily":
            case "tavilyapikey":
                if (string.IsNullOrWhiteSpace(value) ||
                    value.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    config.TavilyApiKey = null;
                    return new(true, "✓ Tavily API key cleared — web search falls back to DuckDuckGo", ApplyScope.KernelRebuild);
                }
                var tavilyKey = value.Trim();
                config.TavilyApiKey = tavilyKey;
                var tavilyMsg = $"✓ Tavily API key saved ({ArdinCodeConfig.MaskApiKey(tavilyKey)}) — web search now prefers Tavily, with DuckDuckGo as fallback";
                if (!tavilyKey.StartsWith("tvly-", StringComparison.OrdinalIgnoreCase))
                    tavilyMsg += "\n  Note: Tavily keys normally start with \"tvly-\" — double-check yours if verification fails.";
                return new(true, tavilyMsg, ApplyScope.KernelRebuild,
                    PostSetValidation: () => Plugins.WebSearchPlugin.ValidateTavilyKeyAsync(tavilyKey));

            case "mcp":
            case "enablemcp":
                if (bool.TryParse(value, out var enableMcp))
                {
                    config.EnableMcp = enableMcp;
                    var mcpMsg = $"✓ MCP {(enableMcp ? "enabled" : "disabled")}";
                    if (enableMcp && config.McpServers.Count == 0)
                        mcpMsg += $"\n  Edit {ArdinCodeConfig.GetDefaultConfigPath()} to add servers under \"mcpServers\".";
                    return new(true, mcpMsg, ApplyScope.KernelRebuild);
                }
                return Fail("Error: Value must be 'true' or 'false'");

            default:
                return Fail($"Unknown configuration key: {key}");
        }
    }

    /// <summary>
    /// One line per key with its current value — the usage/"menu" output for
    /// `/config set` with no arguments. A menu of real values beats named presets:
    /// it shows the user where they are, not where we guess they should be.
    /// </summary>
    public static string DescribeKeys(ArdinCodeConfig config) =>
        $"""
        model                {config.GetEffectiveModelName()}
        endpoint             {config.ApiEndpoint}
        apikey               {(string.IsNullOrWhiteSpace(config.ApiKey) ? "not set" : ArdinCodeConfig.MaskApiKey(config.ApiKey))}
        temperature          {config.Temperature}  (0.0-1.0)
        maxTokens            {config.MaxTokens}  (max response length, {ArdinCodeConfig.MinMaxTokens}-{ArdinCodeConfig.MaxMaxTokens})
        modelResponseTimeout {config.ModelResponseTimeoutSeconds}s  (stall watchdog, {ArdinCodeConfig.MinModelResponseTimeoutSeconds}-{ArdinCodeConfig.MaxModelResponseTimeoutSeconds})
        timeout              {config.RequestTimeoutMinutes} min  (per-request ceiling, {ArdinCodeConfig.MinRequestTimeoutMinutes}-{ArdinCodeConfig.MaxRequestTimeoutMinutes})
        toolBudget           {config.ToolResultCharBudget:N0} chars  ({ArdinCodeConfig.MinToolResultCharBudget:N0}-{ArdinCodeConfig.MaxToolResultCharBudget:N0})
        autoContinue         {config.EnableAutoContinuation}
        maxContinuations     {config.MaxAutoContinuations}  ({ArdinCodeConfig.MinMaxAutoContinuations}-{ArdinCodeConfig.MaxMaxAutoContinuations})
        renderTimeout        {config.MarkdownRenderTimeoutSeconds}s  ({ArdinCodeConfig.MinMarkdownRenderTimeoutSeconds}-{ArdinCodeConfig.MaxMarkdownRenderTimeoutSeconds})
        taskPlanning         {config.EnableTaskPlanning}
        diffApprovals        {config.EnableDiffApprovals}
        webSearch            {config.EnableWebSearch}
        tavilyKey            {(string.IsNullOrWhiteSpace(config.TavilyApiKey) ? "not set" : ArdinCodeConfig.MaskApiKey(config.TavilyApiKey))}  (Tavily API key for reliable web search — free at https://app.tavily.com; "clear" to remove)
        mcp                  {config.EnableMcp}
        themeCustomization   {config.EnableThemeCustomization}
        """;
}
