/**
 *  Author: DevArdin
 *  Date: 2025-12-10
 *  Description: AIService.cs - Manages AI interactions using Semantic Kernel.
 *  File: AIService.cs
 */

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ArdinCode.Models;
using ArdinCode.Plugins;
using ModelContextProtocol.Client;
using System.Net;
using System.Text.Json;

namespace ArdinCode.Services;


/// <summary>
/// Manages AI interactions using Semantic Kernel.
/// </summary>
public class AIService
{
    private Kernel _kernel;
    private IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private string _systemPrompt;

    // The verbatim user message that opened the current chat turn (including @file/@folder
    // expansions). Plan steps execute in isolated chat histories and need it for ground
    // truth about target paths — see ChatStreamAsync and BuildStepContext.
    private string? _currentTurnUserMessage;
    private ArdinCodeConfig _config;
    private OpenAIPromptExecutionSettings _settings;
    private readonly ProjectRootAccessor _projectRootAccessor;
    private readonly FunctionCompletionTracker _completionTracker = new();
    private FunctionInvocationFilter _functionFilter;
    private readonly TokenTrackingService _tokenTracker;
    private readonly PlanHandoff _planHandoff;
    private readonly SkillLoader _skillLoader;
    private readonly McpClientManager _mcpManager;
    private readonly McpApprovalGate _mcpApprovalGate;
    private readonly SpinnerService _spinner;
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private readonly FallbackFunctionCallExecutor _fallbackExecutor;

    /// <summary>
    /// Event raised when a function is about to be invoked.
    /// </summary>
    public event Action<FunctionCall>? OnFunctionInvoked;

    /// <summary>
    /// Event raised when a function completes (success or failure).
    /// </summary>
    public event Action<FunctionExecutionResult>? OnFunctionCompleted;

    /// <summary>
    /// Exposes the completion tracker for external consumers (e.g., TaskPlannerService).
    /// </summary>
    public FunctionCompletionTracker CompletionTracker => _completionTracker;

    /// <summary>
    /// Async callback for requesting user approval before writing a file.
    /// Set this from the UI layer (App.razor) to enable diff approvals.
    /// </summary>
    private Func<string, string?, string, Task<DiffApprovalResult>>? _onWriteApprovalRequested;
    public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested
    {
        get => _onWriteApprovalRequested;
        set
        {
            _onWriteApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnWriteApprovalRequested = value;
            }
        }
    }

    /// <summary>
    /// Async callback for requesting user approval before deleting a file.
    /// Set this from the UI layer (App.razor) to enable delete approvals.
    /// </summary>
    private Func<string, string?, Task<DiffApprovalResult>>? _onDeleteApprovalRequested;
    public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested
    {
        get => _onDeleteApprovalRequested;
        set
        {
            _onDeleteApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnDeleteApprovalRequested = value;
            }
        }
    }

    /// <summary>
    /// Async callback for requesting user approval before executing a shell command.
    /// Set this from the UI layer (App.razor) to enable command approvals.
    /// </summary>
    private Func<string, Task<DiffApprovalResult>>? _onCommandApprovalRequested;
    public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested
    {
        get => _onCommandApprovalRequested;
        set
        {
            _onCommandApprovalRequested = value;
            if (_functionFilter != null)
            {
                _functionFilter.OnCommandApprovalRequested = value;
            }
        }
    }

    public AIService(ProjectRootAccessor projectRootAccessor, ArdinCodeConfig config, TokenTrackingService tokenTracker, PlanHandoff planHandoff, SkillLoader skillLoader, McpClientManager mcpManager, McpApprovalGate mcpApprovalGate, SpinnerService spinner)
    {
        _projectRootAccessor = projectRootAccessor;
        _config = config;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        _skillLoader = skillLoader;
        _mcpManager = mcpManager;
        _mcpApprovalGate = mcpApprovalGate;
        _spinner = spinner;
        _fallbackExecutor = new FallbackFunctionCallExecutor(
            call => OnFunctionInvoked?.Invoke(call),
            result => OnFunctionCompleted?.Invoke(result));
        RebuildSystemPrompt();
        BuildKernel();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Composes the system prompt from the current config: main prompt (web-search
    /// claims conditional on EnableWebSearch), shell-specific rules (cmd.exe vs bash),
    /// and the skill index so the model knows which workflows load_skill() offers.
    /// Called from the constructor and from every settings path — the prompt must track
    /// the config, or toggling websearch would leave the model promising searches it
    /// can't run (or denying ones it can).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_systemPrompt))]
    private void RebuildSystemPrompt()
    {
        var skillIndex = SystemPrompts.BuildSkillIndex(_skillLoader.GetAll());
        _systemPrompt = SystemPrompts.BuildArdinCodeAssistant(_config.EnableWebSearch) + "\n\n" + ShellEnvironment.SystemPromptRules;
        if (!string.IsNullOrEmpty(skillIndex))
        {
            _systemPrompt += "\n\n" + skillIndex;
        }
    }

    /// <summary>
    /// Reinitializes the AI service with a new configuration.
    /// Rebuilds the kernel with the updated model and settings.
    /// </summary>
    public async Task ReinitializeAsync(ArdinCodeConfig config)
    {
        _config = config;
        RebuildSystemPrompt();
        BuildKernel();
        await AttachMcpPluginsAsync();
        await ClearHistoryAsync();
    }

    /// <summary>
    /// Rebuilds the kernel with the current config WITHOUT clearing chat history.
    /// Used by /config set for kernel-baked settings (temperature, maxTokens, toolBudget,
    /// plugin toggles) so an inline tweak doesn't nuke the conversation. Model/endpoint
    /// switches via /model and /setup keep using <see cref="ReinitializeAsync"/> —
    /// a different model mid-history is a different conversation.
    /// </summary>
    public async Task RefreshSettingsAsync(ArdinCodeConfig config)
    {
        _config = config;
        RebuildSystemPrompt();

        // The history-preserving path still has the OLD system prompt as message 0 —
        // swap it in place so a mid-conversation toggle (e.g. websearch) actually
        // reaches the model instead of waiting for the next /clear.
        if (_chatHistory.Count > 0 && _chatHistory[0].Role == AuthorRole.System)
        {
            _chatHistory[0] = new ChatMessageContent(AuthorRole.System, _systemPrompt);
        }

        BuildKernel();
        await AttachMcpPluginsAsync();
    }

    /// <summary>
    /// Registers tools from every active MCP client as SK plugins on the current kernel.
    /// Idempotent within a single kernel instance — plugin registration is skipped if a
    /// plugin with the same <c>mcp_&lt;server&gt;</c> name is already present. BuildKernel
    /// discards the old kernel, so after a rebuild the next call re-registers from scratch.
    /// </summary>
    public async Task AttachMcpPluginsAsync(CancellationToken cancellationToken = default)
    {
        if (!_config.EnableMcp || _mcpManager.ActiveClients.Count == 0) return;

        foreach (var (serverName, client) in _mcpManager.ActiveClients)
        {
            var pluginName = $"mcp_{serverName}";
            if (_kernel.Plugins.Any(p => p.Name == pluginName)) continue;

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                if (tools.Count == 0) continue;
                _kernel.Plugins.AddFromFunctions(
                    pluginName,
                    tools.Select(t => t.AsKernelFunction()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MCP] Failed to list tools for '{serverName}': {ex.Message}");
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_kernel), nameof(_chatService), nameof(_settings), nameof(_functionFilter))]
    private void BuildKernel()
    {
        _settings = new()
        {
            Temperature = (float)_config.Temperature,
            MaxTokens = _config.MaxTokens,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
        };

        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: _config.GetEffectiveModelName(),
            apiKey: _config.ApiKey ?? "",
            endpoint: new Uri(_config.ApiEndpoint)
        );

        var fileSystemPlugin = new FileSystemPlugin(_projectRootAccessor, _spinner);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        if (_config.EnableWebSearch)
        {
            builder.Plugins.AddFromObject(new WebSearchPlugin(_config.GetEffectiveTavilyApiKey()), "WebSearch");
        }

        if (_config.EnableTaskPlanning)
        {
            builder.Plugins.AddFromObject(new PlanningPlugin(), "Planning");
        }

        // Always register the Skills plugin — even when no skills are installed, so
        // users can add skills and trigger a reload without rebuilding the kernel.
        builder.Plugins.AddFromObject(new SkillsPlugin(_skillLoader), "Skills");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Set up function invocation filter for UI events, deduplication, and propose_plan interception.
        // Handlers on the PREVIOUS filter are deliberately left attached: a rebuild (e.g. /config set
        // mid-session) can race a function still in flight on the old kernel, and that function's
        // completion must still reach _completionTracker — detaching here would leak the pending count
        // and pin the stall watchdog paused. The old filter only fires for calls already routed through
        // the discarded kernel, so nothing fires twice; it becomes collectible once those finish.
        _functionFilter = new FunctionInvocationFilter(_config.FunctionDeduplicationWindowSeconds, _projectRootAccessor, _tokenTracker, _planHandoff, _config.ToolResultCharBudget);
        _functionFilter.OnFunctionInvoked += call => OnFunctionInvoked?.Invoke(call);
        _functionFilter.OnFunctionCompleted += result => OnFunctionCompleted?.Invoke(result);
        _functionFilter.OnFunctionStarted += () => _completionTracker.RegisterStart();
        _functionFilter.OnFunctionFinished += () => _completionTracker.RegisterCompletion();

        // Wire diff approval callbacks through to the filter
        if (_onWriteApprovalRequested != null)
        {
            _functionFilter.OnWriteApprovalRequested = _onWriteApprovalRequested;
        }
        if (_onDeleteApprovalRequested != null)
        {
            _functionFilter.OnDeleteApprovalRequested = _onDeleteApprovalRequested;
        }
        if (_onCommandApprovalRequested != null)
        {
            _functionFilter.OnCommandApprovalRequested = _onCommandApprovalRequested;
        }

        // MCP gate — filter delegates to the gate for any plugin whose name starts with "mcp_"
        _functionFilter.McpApprovalGate = _mcpApprovalGate;

        _kernel.FunctionInvocationFilters.Add(_functionFilter);
    }

    /// <summary>
    /// Validates that the configured model supports function calling (tools).
    /// </summary>
    public async Task<(bool IsValid, string? ErrorMessage)> ValidateModelAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ApiKey.Trim());
            }

            var modelName = _config.GetEffectiveModelName();
            var url = _config.ApiEndpoint.TrimEnd('/') + "/models";
            using var response = await client.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, "Error: 401 Unauthorized. Please check your API key.");
            }

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    var modelExists = false;
                    foreach (var modelObj in dataProp.EnumerateArray())
                    {
                        if (modelObj.TryGetProperty("id", out var idProp) && 
                            string.Equals(idProp.GetString(), modelName, StringComparison.OrdinalIgnoreCase))
                        {
                            modelExists = true;
                            break;
                        }
                    }
                    if (!modelExists)
                    {
                        return (false, $"Model '{modelName}' was not found in the provider's list of models.");
                    }
                }
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Could not validate model: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a message to the AI and streams the response chunk by chunk.
    /// NOTE: Uses non-streaming mode internally for reliable function execution with local models.
    /// Streaming with auto-invocation causes issues where function calls are not properly parsed
    /// or executed by the Semantic Kernel with local Ollama models.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Capture the verbatim request for plan-step context. If this turn proposes a
        // plan, each step runs in its own fresh chat history and only sees the model's
        // distilled `goal` — a lossy summary. Observed live: "@STarfox/ create a game…"
        // became goal "create a game…", and every step wrote to the project root instead
        // of STarfox/. The verbatim message (with App.razor's @file/@folder expansions)
        // is the ground truth for target paths.
        _currentTurnUserMessage = userMessage;

        // Add message under lock, then release before the long AI call
        await _historyLock.WaitAsync(cancellationToken);
        try { _chatHistory.AddUserMessage(userMessage); }
        finally { _historyLock.Release(); }

        int continuations = 0;
        while (true)
        {
            var (response, needsContinuation) = await RunOneChatTurnAsync(continuations, cancellationToken);
            yield return response;

            if (!needsContinuation)
                yield break;

            continuations++;
        }
    }

    /// <summary>
    /// Runs a single chat turn inside its own <see cref="InvocationScope"/>. When the
    /// tool budget is exhausted, the assistant's text response acts as an implicit
    /// progress summary — we return <c>needsContinuation=true</c>, push a "keep going"
    /// user message, and the caller loops for another turn with a fresh budget.
    /// </summary>
    private async Task<(string response, bool needsContinuation)> RunOneChatTurnAsync(int continuationIndex, CancellationToken cancellationToken)
    {
        string response;
        bool needsContinuation = false;

        try
        {
            using var scope = _functionFilter.BeginScope();

            // pauseDuringPlan: this outer call can run a whole plan (propose_plan). Both outer
            // timers (the stall watchdog and the request-timeout ceiling) pause for the plan's
            // duration so neither can cancel a step and surface as a bogus "Cancelled by user."
            // Each step has its own watchdog + request timeout, so steps stay bounded.
            var result = await ExecuteModelCallAsync(
                _chatHistory,
                _settings,
                retryOperationName: "ChatStreamAsync",
                tokenLabel: "Chat",
                spinnerMessage: "Thinking… (Esc to cancel)",
                pauseDuringPlan: true,
                cancellationToken);

            var rawResponse = result.Content ?? "No response from AI.";
            response = _config.EnableFallbackFunctionParsing
                ? await _fallbackExecutor.ProcessAsync(rawResponse, _kernel, _config.GetEffectiveModelName())
                : rawResponse;

            var finishReason = "";
            if (result.Metadata != null && result.Metadata.TryGetValue("FinishReason", out var reasonObj) && reasonObj != null)
            {
                finishReason = reasonObj.ToString();
            }

            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                var completionTokens = 0;
                if (result.Metadata != null && result.Metadata.TryGetValue("Usage", out var usageObj) && usageObj != null)
                {
                    var type = usageObj.GetType();
                    var completionProp = type.GetProperty("CompletionTokens") ?? type.GetProperty("OutputTokens") ?? type.GetProperty("CompletionTokenCount");
                    if (completionProp != null)
                    {
                        var compVal = completionProp.GetValue(usageObj);
                        if (compVal != null)
                        {
                            completionTokens = Convert.ToInt32(compVal);
                        }
                    }
                }

                response += BuildLengthCutoffNotice(
                    completionTokens,
                    _config.MaxTokens,
                    emptyContent: string.IsNullOrEmpty(result.Content));
            }

            await _historyLock.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(response))
                    _chatHistory.AddAssistantMessage(response);
            }
            finally { _historyLock.Release(); }

            if (scope.BudgetExhausted
                && _config.EnableAutoContinuation
                && continuationIndex < _config.MaxAutoContinuations)
            {
                needsContinuation = true;
                response += $"\n\n⟳ Auto-continuing ({continuationIndex + 1}/{_config.MaxAutoContinuations}) — tool budget was full; resuming with a fresh budget.\n";

                await _historyLock.WaitAsync();
                try
                {
                    _chatHistory.AddUserMessage(
                        "Continue from where you left off. Your previous response was a progress summary; " +
                        "the tool-call budget has been reset, so you can call tools again to finish the remaining work.");
                }
                finally { _historyLock.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response = "Request cancelled.";
        }
        catch (ModelStallException)
        {
            // Stall watchdog fired — the model went quiet (no tokens, no tool activity) for longer
            // than the per-call budget. Usually a local model stalling as context grows.
            response = $"Error: the model went silent for {_config.ModelResponseTimeoutSeconds}s and was stopped by the stall watchdog.\n\n" +
                       "This usually means a local model stalled as context grew. Try /clear to trim history, a smaller request, " +
                       $"or raise the watchdog: /config set modelResponseTimeout 300.";
        }
        catch (ModelCallTimeoutException)
        {
            response = "Error: Request timed out. The model took too long to respond.\n\n" +
                      "Try breaking your request into smaller parts, or use a faster model.";
        }
        // Provider-side "request too big" rejection in direct chat — covers both model
        // context-window overflow AND transport-level 413 / "request body too large"
        // from Ollama's Go HTTP server. Same recovery for both: compact the persistent
        // _chatHistory into a recap so the next turn fits under the provider's limit.
        catch (Exception ex) when (IsContextOverflowError(ex)
                                    && _config.EnableAutoContinuation
                                    && continuationIndex < _config.MaxAutoContinuations)
        {
            await CompactChatHistoryAsync();
            needsContinuation = true;
            response = $"⚠ Provider rejected request (payload too large). " +
                       $"Compacting conversation history and retrying ({continuationIndex + 1}/{_config.MaxAutoContinuations})...\n";
        }
        catch (HttpRequestException ex)
        {
            response = FormatHttpFailure(ex);
        }
        catch (Exception ex)
        {
            response = FormatErrorMessage(ex);
        }

        return (response, needsContinuation);
    }

    /// <summary>
    /// Composes the warning appended when generation stops with done_reason "length".
    /// Two distinct causes share that reason and need OPPOSITE advice:
    ///   • Output reached the response cap (EvalCount ≈ maxTokens) → say "continue" or raise maxTokens.
    ///   • The CONTEXT WINDOW filled mid-generation (output far below the cap) → num_ctx is the
    ///     bottleneck and raising maxTokens does nothing — the old one-size-fits-all message sent
    ///     users to exactly the wrong knob. Common when the daemon was started outside ArdinCode
    ///     (tray app) with Ollama's ~4k default window.
    /// <paramref name="emptyContent"/> flags the worst case: a thinking model (e.g. qwen3,
    /// minimax) spent the whole budget on internal reasoning and produced no visible answer.
    /// <paramref name="isCloudModel"/> swaps the window-filled advice: cloud context lives
    /// server-side at the model's full window, so the desktop-app slider / daemon-restart
    /// guidance is meaningless there — trimming history is the only lever.
    /// Static + public for direct unit testing without standing up the full service.
    /// </summary>
    public static string BuildLengthCutoffNotice(long completionTokens, int maxTokens, bool emptyContent)
    {
        // Formatted as markdown — the response path renders through MarkdownHtmlRenderer,
        // so a bold headline + bullet list reads far better than the old wall of text.
        if (completionTokens <= 0 || completionTokens >= maxTokens * 9L / 10)
        {
            var thinkingCapNote = emptyContent
                ? "\n- Note: thinking models spend reasoning tokens from this same budget — " +
                  "a small max tokens limit can be consumed entirely by internal reasoning before any visible answer."
                : "";
            return "\n\n⚠ **Response cut off — hit the max response tokens limit.**\n" +
                   "- Say \"continue\" to keep going\n" +
                   "- Or raise max tokens with /config" +
                   thinkingCapNote;
        }

        var thinkingNote = emptyContent
            ? "\nNo visible answer was produced — likely spent all tokens on internal reasoning."
            : "";

        return "\n\n⚠ **Response cut off — the model's CONTEXT WINDOW filled.**\n" +
               $"Only {completionTokens:N0} of your {maxTokens / 1024}k response budget was generated, " +
               "so raising max tokens won't help." +
               thinkingNote + "\n" +
               "\nThe conversation filled the model's server-side context window. How to fix:\n" +
               "- /clear to trim the conversation history\n" +
               "- Break the request into smaller pieces";
    }

    /// <summary>
    /// Shared model-call scaffolding for direct chat turns and plan steps: the per-turn
    /// request-timeout ceiling, the stall watchdog, the heartbeat spinner, the retry policy,
    /// and token recording. Cancellation is classified here — while the token sources are
    /// still in scope — into typed exceptions so each caller phrases its own user-facing
    /// message: <see cref="ModelStallException"/> when the watchdog fired,
    /// <see cref="ModelCallTimeoutException"/> when the request ceiling was hit. A
    /// user-initiated cancellation rethrows the original <see cref="OperationCanceledException"/>.
    /// All other exceptions (context overflow, HTTP failures) propagate unwrapped.
    /// </summary>
    private async Task<ChatMessageContent> ExecuteModelCallAsync(
        ChatHistory history,
        OpenAIPromptExecutionSettings settings,
        string retryOperationName,
        string tokenLabel,
        string spinnerMessage,
        bool pauseDuringPlan,
        CancellationToken cancellationToken)
    {
        // Two timeouts: requestCts is the generous per-turn ceiling (RequestTimeoutMinutes);
        // responseCts is the stall watchdog (ModelResponseTimeoutSeconds) — a much shorter
        // bound on a single model-silent stretch so a local model that stops streaming once
        // context grows recovers in minutes, not the full ceiling.
        using var requestCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));
        using var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestCts.Token, responseCts.Token);

        // The request ceiling only needs plan-pausing on the outer chat turn (the whole plan
        // runs inside that single model call); plan steps keep their own bounded ceiling.
        using var watchdog = AttachStallWatchdog(
            responseCts,
            pauseDuringPlan,
            requestCts: pauseDuringPlan ? requestCts : null,
            requestTimeout: TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));

        // Heartbeat over the model-generation stretch: keep a ticking spinner alive (the
        // existing one is stopped between tool events) and advertise the escape hatch so a
        // slow/stalled turn never looks dead.
        _spinner.Start(spinnerMessage);

        try
        {
            var result = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await _chatService.GetChatMessageContentAsync(
                    history,
                    settings,
                    _kernel,
                    linkedCts.Token
                ),
                _config.MaxRetryAttempts,
                retryOperationName,
                linkedCts.Token
            );

            ExtractAndRecordTokens(result, tokenLabel);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (responseCts.IsCancellationRequested)
        {
            throw new ModelStallException();
        }
        catch (OperationCanceledException)
        {
            throw new ModelCallTimeoutException();
        }
    }

    /// <summary>The stall watchdog fired: the model went silent (no tokens, no tool activity) past the per-call budget.</summary>
    private sealed class ModelStallException : Exception;

    /// <summary>The per-turn request-timeout ceiling (RequestTimeoutMinutes) was hit.</summary>
    private sealed class ModelCallTimeoutException : Exception;

    /// <summary>
    /// Attaches a stall watchdog to <paramref name="responseCts"/>: it fires after
    /// <see cref="ArdinCodeConfig.ModelResponseTimeoutSeconds"/> of pure model-generation time.
    /// Tool calls — and the approval prompts that run inside them — PAUSE the watchdog: while any
    /// function is in flight the timer is disabled, so a long-running tool (e.g. a build) or a user
    /// deliberating at an approval prompt is never mistaken for a stalled model. The watchdog only
    /// counts contiguous stretches where the model is generating with no tool activity. Dispose the
    /// returned handle once the model call completes to detach the hooks.
    /// </summary>
    private IDisposable AttachStallWatchdog(
        CancellationTokenSource responseCts,
        bool pauseDuringPlan = false,
        CancellationTokenSource? requestCts = null,
        TimeSpan requestTimeout = default)
    {
        // Capture the filter locally so subscribe/unsubscribe target the same instance even if the
        // kernel is rebuilt mid-flight. CancelAfter is thread-safe and a no-op once disposed.
        var filter = _functionFilter;
        var timeout = TimeSpan.FromSeconds(_config.ModelResponseTimeoutSeconds);

        // When a plan is running inside this call, suppress tool-event resumes — see below.
        var planActive = false;

        void Pause() { try { responseCts.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void Resume() { try { responseCts.CancelAfter(timeout); } catch (ObjectDisposedException) { } }

        // A function entering flight pauses the watchdog; the last one leaving flight resumes it.
        // PendingFunctionCount is already decremented before OnFunctionFinished fires, so a reading
        // of 0 means no tool is in flight. AllowConcurrentInvocation is handled: the count only hits
        // 0 when the final concurrent call completes.
        void OnStarted() => Pause();
        void OnFinished() { if (!planActive && filter.PendingFunctionCount == 0) Resume(); }

        filter.OnFunctionStarted += OnStarted;
        filter.OnFunctionFinished += OnFinished;

        // Outer chat turn only: the whole plan executes inside this single model call (the
        // propose_plan tool). Its steps legitimately generate large files with >timeout gaps
        // between tool calls, and this call's token is threaded into the plan — so if the watchdog
        // fired mid-plan it would cancel a step and surface as a bogus "Cancelled by user." Pause
        // it for the plan's entire duration; the plan's own per-step watchdogs cover stalls there.
        // (Plan-step watchdogs pass pauseDuringPlan=false: IsExecuting is already true for them, so
        // honoring it would pin them paused and disable their stall detection.)
        // The request-timeout ceiling (requestCts) ALSO wraps the whole plan and would
        // misfire the same way — a long (or thrashing) plan that crosses RequestTimeoutMinutes
        // got cancelled and mislabeled "Cancelled by user." So pause it for the plan too. Each
        // plan step still has its OWN request timeout, so steps stay bounded; only the redundant
        // outer ceiling is suspended. Resumed (fresh) for any post-plan model wrap-up.
        void PauseRequest() { try { requestCts?.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        void ResumeRequest() { try { requestCts?.CancelAfter(requestTimeout); } catch (ObjectDisposedException) { } }

        Action? onPlanStart = null, onPlanEnd = null;
        if (pauseDuringPlan && _planHandoff != null)
        {
            onPlanStart = () => { planActive = true; Pause(); PauseRequest(); };
            onPlanEnd = () => { planActive = false; Resume(); ResumeRequest(); };
            _planHandoff.ExecutionStarted += onPlanStart;
            _planHandoff.ExecutionFinished += onPlanEnd;
        }

        return new ActionDisposable(() =>
        {
            filter.OnFunctionStarted -= OnStarted;
            filter.OnFunctionFinished -= OnFinished;
            if (onPlanStart != null) _planHandoff!.ExecutionStarted -= onPlanStart;
            if (onPlanEnd != null) _planHandoff!.ExecutionFinished -= onPlanEnd;
        });
    }

    /// <summary>Runs an action on Dispose. Used to detach stall-watchdog hooks deterministically.</summary>
    private sealed class ActionDisposable : IDisposable
    {
        private Action? _onDispose;
        public ActionDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    /// <summary>
    /// Formats HttpRequestException specifically. 401 is the most common "weird"
    /// connection failure — the daemon is running fine but the user got signed out
    /// of ollama.com. The default "Make sure Ollama is running: ollama serve"
    /// message misleads users into running `ollama serve` again, which then fails
    /// with "port already in use" because the daemon they're hitting is already up.
    /// </summary>
    private string FormatHttpFailure(HttpRequestException ex)
    {
        if (IsUnauthorizedError(ex))
        {
            return "<red>Error: API Provider returned 401 Unauthorized.</red>\n\n" +
                   "Please make sure your API Key is correct.";
        }

        return "Error: Connection to AI Provider failed.\n\n" +
               $"Details: {ex.Message}\n\n" +
               "What to do:\n" +
               "  • Check your internet connection.\n" +
               "  • Verify the API Provider URL in config: " + _config.ApiEndpoint + "\n" +
               "  • Verify your API Key.\n" +
               "  • Then type /retry to reconnect, OR\n" +
               "  • Run /setup to walk through setup again.";
    }

    private static bool IsUnauthorizedError(HttpRequestException ex)
        => ex.StatusCode == HttpStatusCode.Unauthorized
           || (ex.Message?.Contains("401") ?? false)
           || (ex.Message?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Formats error messages for display.
    /// </summary>
    private string FormatErrorMessage(Exception ex)
    {
        if (ex is HttpRequestException http && IsUnauthorizedError(http))
            return FormatHttpFailure(http);
        if (ex.Message.Contains("401")
            || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "<red>Error: API Provider returned 401 Unauthorized.</red>\n\n" +
                   "Please make sure your API Key is correct.";
        }

        if (IsContextOverflowError(ex))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' rejected the request because the payload was too large for its context window.\n\n" +
                   $"Details: {ex.Message}\n\n" +
                   "What to do:\n" +
                   "  • Try /clear to start a fresh conversation, OR\n" +
                   $"  • Run /config and lower 'Max response tokens' (currently {_config.MaxTokens / 1024}k) — large limits eat into the context budget, OR\n" +
                   $"  • Lower the tool-result budget: /config set toolBudget 50000, OR\n" +
                   "  • Switch to a model with a larger context window via /config.";
        }

        if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' does not support tool calling.\n\n" +
                   $"ArdinCode uses agentic tool calling to read, write, and manage files.\n" +
                   $"Your current model doesn't support this — you'll need to switch to a tool-enabled model.\n\n" +
                   $"To change your model, run /config and select a model that supports tool use.";
        }

        return $"Error communicating with AI: {ex.Message}\n\nMake sure your API Provider endpoint and API Key are valid.\nEndpoint: {_config.ApiEndpoint}\nModel: {_config.GetEffectiveModelName()}\n\nOr run /setup to walk through setup again.";
    }

    /// <summary>
    /// Executes a single step of a task plan with function calling enabled.
    /// Uses previous step results as context for continuity.
    /// </summary>
    /// <summary>
    /// Builds the system-prompt context a plan step's fresh chat history is seeded with.
    /// Includes the verbatim user request that produced the plan: steps otherwise only
    /// see the model's distilled goal, which drops details like target folders (observed
    /// live: "@STarfox/ create a game…" → goal "create a game…" → every step wrote to the
    /// project root). Previous-step results are limited to the last 2 and the original
    /// request is capped so step context stays small on local models.
    /// </summary>
    public static string BuildStepContext(string systemPrompt, string? originalUserRequest, List<string> previousResults)
    {
        // Generous enough for a long message plus @folder listings; small enough that a
        // pasted @file of several thousand lines can't flood every step's context. Paths
        // and intent come first in a prompt, so head-truncation keeps what steps need.
        const int MaxOriginalRequestChars = 4000;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(systemPrompt);

        if (!string.IsNullOrWhiteSpace(originalUserRequest))
        {
            var request = originalUserRequest.Trim();
            if (request.Length > MaxOriginalRequestChars)
                request = request[..MaxOriginalRequestChars] + "\n…[truncated]";

            sb.AppendLine("\n--- The User's Original Request ---");
            sb.AppendLine(request);
            sb.AppendLine("--- End of Original Request ---");
            sb.AppendLine("This step is part of a plan fulfilling the request above. The request is " +
                          "authoritative for WHERE work happens: target folders and file paths mentioned " +
                          "in it (including attached @folder/@file references) override any unqualified " +
                          "paths in the step instruction.");
        }

        var recentResults = previousResults.Count > 2
            ? previousResults.Skip(previousResults.Count - 2).ToList()
            : previousResults;

        if (recentResults.Any())
        {
            sb.AppendLine("\n--- Results from Previous Steps ---");
            foreach (var result in recentResults)
            {
                sb.AppendLine(result);
            }
            sb.AppendLine("--- End of Previous Steps ---\n");
        }

        return sb.ToString();
    }

    public async Task<string> ExecutePlanStepAsync(string stepInstruction, List<string> previousResults, CancellationToken cancellationToken = default)
    {
        var contextBuilder = new System.Text.StringBuilder(
            BuildStepContext(_systemPrompt, _currentTurnUserMessage, previousResults));

        // Create a temporary chat history for this step
        var stepHistory = new ChatHistory(contextBuilder.ToString());
        stepHistory.AddUserMessage($"Execute this step now: {stepInstruction}\n\nRemember: Use the available functions to complete this task. Do not describe the function call - actually invoke it.");

        // Allow concurrent function invocation within a step for parallel file operations
        var stepSettings = new OpenAIPromptExecutionSettings
        {
            Temperature = (float)_config.Temperature,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                autoInvoke: true,
                options: new() { AllowConcurrentInvocation = true }
            )
        };

        var stepLabel = $"Step {previousResults.Count + 1}";
        var combined = new System.Text.StringBuilder();
        int continuations = 0;

        while (true)
        {
            string processedResponse = "";
            bool needsContinuation = false;
            bool contextOverflowRecovery = false;

            // Each continuation gets a fresh scope so the budget and dedup-set reset.
            using (var scope = _functionFilter.BeginScope())
            {
                try
                {
                    var result = await ExecuteModelCallAsync(
                        stepHistory,
                        stepSettings,
                        retryOperationName: "ExecutePlanStepAsync",
                        tokenLabel: stepLabel,
                        spinnerMessage: $"Working on {stepLabel} — press Esc to cancel",
                        pauseDuringPlan: false,
                        cancellationToken);

                    var response = result.Content ?? "Step completed (no response content).";

                    await _completionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(5));

                    processedResponse = _config.EnableFallbackFunctionParsing
                        ? await _fallbackExecutor.ProcessAsync(response, _kernel, _config.GetEffectiveModelName())
                        : response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Step cancelled.", cancellationToken);
                }
                catch (ModelStallException)
                {
                    // Stall watchdog fired — the model went quiet for longer than the per-call
                    // budget. Almost always a local model stalling as context grows.
                    throw new Exception(
                        $"The model stopped responding for {_config.ModelResponseTimeoutSeconds}s and was stopped by the stall watchdog. " +
                        "This usually means a local model stalled as context grew. Try a smaller step, /clear to trim history, " +
                        "or raise the watchdog: /config set modelResponseTimeout 300.");
                }
                catch (ModelCallTimeoutException)
                {
                    throw new Exception("Step execution timed out. Try breaking this step into smaller parts.");
                }
                // Provider-side context-window rejection — recoverable via synthetic-summary restart.
                catch (Exception ex) when (IsContextOverflowError(ex)
                                            && _config.EnableAutoContinuation
                                            && continuations < _config.MaxAutoContinuations)
                {
                    contextOverflowRecovery = true;
                }
                catch (HttpRequestException ex)
                {
                    throw new Exception($"Connection to AI Provider failed: {ex.Message}");
                }

                // Decide whether to auto-continue (while scope is still live so BudgetExhausted reads correctly).
                if (!contextOverflowRecovery
                    && scope.BudgetExhausted
                    && _config.EnableAutoContinuation
                    && continuations < _config.MaxAutoContinuations)
                {
                    needsContinuation = true;
                }

                // User picked "Cancel plan" from a diff approval mid-step — the filter set the flag.
                // Throw here so ExecutePlanAsync treats it as plan-level cancellation, not just step-level.
                if (scope.PlanCancellationRequested)
                    throw new PlanCancellationRequestedException();
            }

            // Context-overflow recovery: the turn never completed, so the model produced no
            // summary of its own. We build one from the tool-call trace and restart the step
            // with a fresh history seeded by that summary.
            if (contextOverflowRecovery)
            {
                var summary = SynthesizeHistorySummary(stepHistory);
                continuations++;

                combined.AppendLine();
                combined.AppendLine($"⚠ Provider rejected request (context window full). Restarting step with a compacted summary ({continuations}/{_config.MaxAutoContinuations}).");
                combined.AppendLine();

                stepHistory = new ChatHistory(contextBuilder.ToString());
                stepHistory.AddUserMessage(
                    $"Execute this step: {stepInstruction}\n\n" +
                    $"A previous attempt hit the provider's context-window limit and was aborted. " +
                    $"Here's what was partially completed (tool-call trace; do NOT redo these):\n\n{summary}\n\n" +
                    $"Continue from where it left off. Use the available functions to finish the step.");

                continue;
            }

            combined.AppendLine(processedResponse);

            if (!needsContinuation)
                return combined.ToString().TrimEnd();

            continuations++;
            combined.AppendLine();
            combined.AppendLine($"⟳ Auto-continuing ({continuations}/{_config.MaxAutoContinuations}) — tool budget reset.");
            combined.AppendLine();

            // Seed the next turn: assistant's summary, then a nudge to keep going.
            stepHistory.AddAssistantMessage(processedResponse);
            stepHistory.AddUserMessage(
                "Continue from where you left off. Your previous response was a progress summary; " +
                "the tool-call budget has been reset, so call tools again to finish this step.");
        }
    }

    /// <summary>
    /// Matches provider-side context-window rejections. Delegates to <see cref="RetryPolicy.IsContextOverflowError"/>
    /// so the retry policy and recovery path agree on exactly which errors skip retries and route to recovery.
    /// </summary>
    private static bool IsContextOverflowError(Exception? ex) => RetryPolicy.IsContextOverflowError(ex);

    /// <summary>
    /// Walks a chat history and produces a compact recap the next turn can read as
    /// "what's happened so far." Aggressively truncates content so the summary itself
    /// doesn't reintroduce the overflow. Start/end indices let callers exclude messages
    /// they're about to re-seed fresh (e.g. the system prompt or current user message).
    ///
    /// Tool-call activity often lives in <see cref="ChatMessageContent.Items"/> rather than
    /// <c>Content</c> — SK's auto-invoke loop puts function calls/results there. Skipping
    /// Items would drop most of the trace on a context-overflow recovery. We walk both.
    /// </summary>
    private static string SynthesizeHistorySummary(
        ChatHistory history,
        int startIndex = 2,
        int? endIndexExclusive = null,
        int maxChars = 1500)
    {
        var sb = new System.Text.StringBuilder();
        var end = endIndexExclusive ?? history.Count;
        for (int i = Math.Max(0, startIndex); i < end; i++)
        {
            var msg = history[i];
            var role = msg.Role.Label;
            var line = FormatMessageForSummary(msg);
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Length > 180) line = line[..180] + "...";
            sb.Append('[').Append(role).Append("] ").AppendLine(line);

            if (sb.Length > maxChars)
            {
                sb.AppendLine("... (older entries truncated)");
                break;
            }
        }
        return sb.Length == 0 ? "(no prior activity captured)" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Produces a one-line recap of a single chat message for the summary walker.
    /// Falls back to <c>Items</c> (function calls / results) when <c>Content</c> is empty.
    /// </summary>
    private static string FormatMessageForSummary(Microsoft.SemanticKernel.ChatMessageContent msg)
    {
        var content = msg.Content?.Trim();
        if (!string.IsNullOrEmpty(content)) return content;

        if (msg.Items == null || msg.Items.Count == 0) return "";

        var parts = new List<string>();
        foreach (var item in msg.Items)
        {
            switch (item)
            {
                case Microsoft.SemanticKernel.FunctionCallContent fc:
                {
                    var args = fc.Arguments != null && fc.Arguments.Count > 0
                        ? string.Join(", ", fc.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value?.ToString(), 40)}"))
                        : "";
                    parts.Add($"called {fc.FunctionName}({args})");
                    break;
                }
                case Microsoft.SemanticKernel.FunctionResultContent fr:
                {
                    var resultText = fr.Result?.ToString() ?? "";
                    parts.Add($"{fr.FunctionName} → {Truncate(resultText, 80)}");
                    break;
                }
                case Microsoft.SemanticKernel.TextContent tc when !string.IsNullOrWhiteSpace(tc.Text):
                    parts.Add(tc.Text.Trim());
                    break;
            }
        }
        return parts.Count == 0 ? "" : string.Join("; ", parts);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// Collapses the persistent <see cref="_chatHistory"/> into system prompt + recap +
    /// last user message. Used when a direct chat turn hits a provider context-window
    /// rejection — we compact the conversation so the next retry fits.
    /// </summary>
    private async Task CompactChatHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            int lastUserIdx = -1;
            for (int i = _chatHistory.Count - 1; i >= 0; i--)
            {
                if (_chatHistory[i].Role == AuthorRole.User)
                {
                    lastUserIdx = i;
                    break;
                }
            }
            if (lastUserIdx < 1) return; // Nothing to compact.

            var lastUserContent = _chatHistory[lastUserIdx].Content ?? "";

            // Summarize everything between the system prompt (0) and the current user turn.
            var recap = SynthesizeHistorySummary(_chatHistory, startIndex: 1, endIndexExclusive: lastUserIdx);

            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(_systemPrompt);
            if (!string.IsNullOrWhiteSpace(recap) && recap != "(no prior activity captured)")
            {
                _chatHistory.AddUserMessage(
                    "[Prior conversation recap — the previous attempt hit the provider's context-window limit and was compacted:]\n" +
                    recap);
            }
            _chatHistory.AddUserMessage(lastUserContent);
        }
        finally { _historyLock.Release(); }
    }

    /// <summary>
    /// Extracts real token counts from a ChatMessageContent response and records them.
    /// Non-critical — failures are silently swallowed.
    /// </summary>
    private void ExtractAndRecordTokens(ChatMessageContent response, string label)
    {
        try
        {
            if (response.Metadata != null && response.Metadata.TryGetValue("Usage", out var usageObj) && usageObj != null)
            {
                var type = usageObj.GetType();
                var promptProp = type.GetProperty("PromptTokens") ?? type.GetProperty("InputTokens") ?? type.GetProperty("PromptTokenCount");
                var completionProp = type.GetProperty("CompletionTokens") ?? type.GetProperty("OutputTokens") ?? type.GetProperty("CompletionTokenCount");
                
                if (promptProp != null && completionProp != null)
                {
                    var promptVal = promptProp.GetValue(usageObj);
                    var completionVal = completionProp.GetValue(usageObj);
                    if (promptVal != null && completionVal != null)
                    {
                        int promptTokens = Convert.ToInt32(promptVal);
                        int completionTokens = Convert.ToInt32(completionVal);
                        if (promptTokens > 0 || completionTokens > 0)
                        {
                            _tokenTracker.RecordModelUsage(promptTokens, completionTokens, label, null);
                        }
                    }
                }
            }
        }
        catch
        {
            // Token extraction is non-critical — never let it break the flow
        }
    }

    /// <summary>
    /// Exposes the token tracker for external consumers (e.g., App.razor display).
    /// </summary>
    public TokenTrackingService TokenTracker => _tokenTracker;

    /// <summary>
    /// Enters learn mode by clearing history and injecting the educator system prompt.
    /// The user can return to normal mode via /clear which restores the original system prompt.
    /// </summary>
    public async Task EnterLearnModeAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(SystemPrompts.LearnModePrompt);
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Clears the chat history and starts a new conversation.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            _chatHistory.Clear();
            _chatHistory.AddSystemMessage(_systemPrompt);
            _functionFilter.ClearCache();
            _tokenTracker.Reset();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Gets the current chat history.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessageContent>> GetHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            return _chatHistory.ToList().AsReadOnly();
        }
        finally
        {
            _historyLock.Release();
        }
    }
}
