namespace MandoCode.Models;

/// <summary>
/// Single implementation behind both the CLI (`mandocode --config set`) and the in-app
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
        /// <summary>Wired at app startup — needs a MandoCode restart.</summary>
        AppRestart,
        /// <summary>Applied via OLLAMA_CONTEXT_LENGTH when MandoCode starts the Ollama daemon.</summary>
        DaemonRestart
    }

    public sealed record SetResult(bool Ok, string Message, ApplyScope Scope = ApplyScope.Immediate);

    private static SetResult Fail(string message) => new(false, message);

    public static SetResult TrySet(MandoCodeConfig config, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "endpoint":
            case "ollamaendpoint":
                config.OllamaEndpoint = value;
                return new(true, $"✓ Set Ollama endpoint to: {value}", ApplyScope.KernelRebuild);

            case "model":
            case "modelname":
                config.ModelName = value;
                return new(true, $"✓ Set model to: {value}", ApplyScope.KernelRebuild);

            case "modelpath":
                config.ModelPath = value;
                return new(true, $"✓ Set model path to: {value}", ApplyScope.KernelRebuild);

            case "temperature":
                if (double.TryParse(value, out var temp) && MandoCodeConfig.IsValidTemperature(temp))
                {
                    config.Temperature = temp;
                    return new(true, $"✓ Set temperature to: {temp}", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Temperature must be a number between {MandoCodeConfig.MinTemperature} and {MandoCodeConfig.MaxTemperature}");

            case "maxtokens":
                if (int.TryParse(value, out var tokens) && MandoCodeConfig.IsValidMaxTokens(tokens))
                {
                    config.MaxTokens = tokens;
                    return new(true, $"✓ Set max response tokens to: {tokens}", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Max tokens must be between {MandoCodeConfig.MinMaxTokens} and {MandoCodeConfig.MaxMaxTokens}");

            case "contextlength":
            case "contextwindow":
            case "numctx":
                if (int.TryParse(value, out var ctxLen) && MandoCodeConfig.IsValidContextLength(ctxLen))
                {
                    config.ContextLength = ctxLen;
                    var msg = ctxLen == 0
                        ? "✓ Context length cleared — Ollama's own default applies"
                        : $"✓ Set local context window to: {ctxLen:N0} tokens";
                    return new(true, msg + "\n  Applies when MandoCode starts the Ollama daemon (restart Ollama for it to take effect). Desktop-app users: the app's Settings → Context length slider governs instead.", ApplyScope.DaemonRestart);
                }
                return Fail($"Error: Context length must be 0 (Ollama default) or between {MandoCodeConfig.MinContextLength:N0} and {MandoCodeConfig.MaxContextLength:N0} tokens");

            case "modelresponsetimeout":
            case "modelresponsetimeoutseconds":
            case "watchdog":
                if (int.TryParse(value, out var stallSecs) && MandoCodeConfig.IsValidModelResponseTimeout(stallSecs))
                {
                    config.ModelResponseTimeoutSeconds = stallSecs;
                    return new(true, $"✓ Set stall watchdog to: {stallSecs}s per model call");
                }
                return Fail($"Error: Stall watchdog must be between {MandoCodeConfig.MinModelResponseTimeoutSeconds} and {MandoCodeConfig.MaxModelResponseTimeoutSeconds} seconds");

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
                if (int.TryParse(value, out var maxCont) && MandoCodeConfig.IsValidMaxAutoContinuations(maxCont))
                {
                    config.MaxAutoContinuations = maxCont;
                    return new(true, $"✓ Max auto-continuations set to: {maxCont}");
                }
                return Fail($"Error: Max continuations must be between {MandoCodeConfig.MinMaxAutoContinuations} and {MandoCodeConfig.MaxMaxAutoContinuations}");

            case "toolbudget":
            case "toolresultbudget":
            case "toolresultcharbudget":
                if (long.TryParse(value, out var budget) && MandoCodeConfig.IsValidToolResultCharBudget(budget))
                {
                    config.ToolResultCharBudget = budget;
                    return new(true, $"✓ Set tool-result budget to: {budget:N0} chars (~{budget / 4:N0} tokens)", ApplyScope.KernelRebuild);
                }
                return Fail($"Error: Tool-result budget must be between {MandoCodeConfig.MinToolResultCharBudget:N0} and {MandoCodeConfig.MaxToolResultCharBudget:N0} chars");

            case "timeout":
            case "requesttimeout":
            case "requesttimeoutminutes":
                if (int.TryParse(value, out var timeout) && MandoCodeConfig.IsValidRequestTimeout(timeout))
                {
                    config.RequestTimeoutMinutes = timeout;
                    return new(true, $"✓ Set request timeout to: {timeout} min");
                }
                return Fail($"Error: Timeout must be between {MandoCodeConfig.MinRequestTimeoutMinutes} and {MandoCodeConfig.MaxRequestTimeoutMinutes} minutes");

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
                if (int.TryParse(value, out var renderTimeout) && MandoCodeConfig.IsValidMarkdownRenderTimeout(renderTimeout))
                {
                    config.MarkdownRenderTimeoutSeconds = renderTimeout;
                    return new(true, $"✓ Set markdown render timeout to: {renderTimeout}s");
                }
                return Fail($"Error: Render timeout must be between {MandoCodeConfig.MinMarkdownRenderTimeoutSeconds} and {MandoCodeConfig.MaxMarkdownRenderTimeoutSeconds} seconds");

            case "mcp":
            case "enablemcp":
                if (bool.TryParse(value, out var enableMcp))
                {
                    config.EnableMcp = enableMcp;
                    var mcpMsg = $"✓ MCP {(enableMcp ? "enabled" : "disabled")}";
                    if (enableMcp && config.McpServers.Count == 0)
                        mcpMsg += $"\n  Edit {MandoCodeConfig.GetDefaultConfigPath()} to add servers under \"mcpServers\".";
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
    public static string DescribeKeys(MandoCodeConfig config) =>
        $"""
        model                {config.GetEffectiveModelName()}
        endpoint             {config.OllamaEndpoint}
        temperature          {config.Temperature}  (0.0-1.0)
        maxTokens            {config.MaxTokens}  (max response length, {MandoCodeConfig.MinMaxTokens}-{MandoCodeConfig.MaxMaxTokens})
        contextLength        {(config.ContextLength == 0 ? "Ollama default" : config.ContextLength.ToString())}  (local window, 0 or {MandoCodeConfig.MinContextLength}-{MandoCodeConfig.MaxContextLength}; applied when MandoCode starts Ollama)
        modelResponseTimeout {config.ModelResponseTimeoutSeconds}s  (stall watchdog, {MandoCodeConfig.MinModelResponseTimeoutSeconds}-{MandoCodeConfig.MaxModelResponseTimeoutSeconds})
        timeout              {config.RequestTimeoutMinutes} min  (per-request ceiling, {MandoCodeConfig.MinRequestTimeoutMinutes}-{MandoCodeConfig.MaxRequestTimeoutMinutes})
        toolBudget           {config.ToolResultCharBudget:N0} chars  ({MandoCodeConfig.MinToolResultCharBudget:N0}-{MandoCodeConfig.MaxToolResultCharBudget:N0})
        autoContinue         {config.EnableAutoContinuation}
        maxContinuations     {config.MaxAutoContinuations}  ({MandoCodeConfig.MinMaxAutoContinuations}-{MandoCodeConfig.MaxMaxAutoContinuations})
        renderTimeout        {config.MarkdownRenderTimeoutSeconds}s  ({MandoCodeConfig.MinMarkdownRenderTimeoutSeconds}-{MandoCodeConfig.MaxMarkdownRenderTimeoutSeconds})
        taskPlanning         {config.EnableTaskPlanning}
        diffApprovals        {config.EnableDiffApprovals}
        webSearch            {config.EnableWebSearch}
        mcp                  {config.EnableMcp}
        themeCustomization   {config.EnableThemeCustomization}
        """;
}
