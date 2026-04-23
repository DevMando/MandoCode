/**
 *  Author: DevMando
 *  Date: 2025-12-10
 *  Description: AIService.cs - Manages AI interactions using Semantic Kernel with Ollama.
 *  File: AIService.cs
 */

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using MandoCode.Models;
using MandoCode.Plugins;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MandoCode.Services;


/// <summary>
/// Manages AI interactions using Semantic Kernel with Ollama.
/// </summary>
public class AIService
{
    private Kernel _kernel;
    private IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private readonly string _systemPrompt;
    private MandoCodeConfig _config;
    private OllamaPromptExecutionSettings _settings;
    private readonly ProjectRootAccessor _projectRootAccessor;
    private readonly FunctionCompletionTracker _completionTracker = new();
    private FunctionInvocationFilter _functionFilter;
    private readonly TokenTrackingService _tokenTracker;
    private readonly PlanHandoff _planHandoff;
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    // Named event handlers stored so we can detach them when rebuilding the kernel
    private Action<FunctionCall>? _filterInvokedHandler;
    private Action<FunctionExecutionResult>? _filterCompletedHandler;
    private Action? _filterStartedHandler;
    private Action? _filterFinishedHandler;

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

    public AIService(ProjectRootAccessor projectRootAccessor, MandoCodeConfig config, TokenTrackingService tokenTracker, PlanHandoff planHandoff)
    {
        _projectRootAccessor = projectRootAccessor;
        _config = config;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        // Append shell-specific rules (cmd.exe vs bash) so the model stops emitting
        // unix commands on Windows or vice-versa.
        _systemPrompt = SystemPrompts.MandoCodeAssistant + "\n\n" + ShellEnvironment.SystemPromptRules;

        BuildKernel();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Reinitializes the AI service with a new configuration.
    /// Rebuilds the kernel with the updated model and settings.
    /// </summary>
    public async Task ReinitializeAsync(MandoCodeConfig config)
    {
        _config = config;
        BuildKernel();
        await ClearHistoryAsync();
    }

    private void BuildKernel()
    {
        _settings = new()
        {
            Temperature = (float)_config.Temperature,
            NumPredict = _config.MaxTokens,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
        };

        var builder = Kernel.CreateBuilder();

        builder.AddOllamaChatCompletion(
            modelId: _config.GetEffectiveModelName(),
            endpoint: new Uri(_config.OllamaEndpoint)
        );

        var fileSystemPlugin = new FileSystemPlugin(_projectRootAccessor);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        if (_config.EnableWebSearch)
        {
            builder.Plugins.AddFromObject(new WebSearchPlugin(), "WebSearch");
        }

        if (_config.EnableTaskPlanning)
        {
            builder.Plugins.AddFromObject(new PlanningPlugin(), "Planning");
        }

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Detach event handlers from old filter before creating a new one
        if (_functionFilter != null)
        {
            if (_filterInvokedHandler != null) _functionFilter.OnFunctionInvoked -= _filterInvokedHandler;
            if (_filterCompletedHandler != null) _functionFilter.OnFunctionCompleted -= _filterCompletedHandler;
            if (_filterStartedHandler != null) _functionFilter.OnFunctionStarted -= _filterStartedHandler;
            if (_filterFinishedHandler != null) _functionFilter.OnFunctionFinished -= _filterFinishedHandler;
        }

        // Set up function invocation filter for UI events, deduplication, and propose_plan interception
        _functionFilter = new FunctionInvocationFilter(_config.FunctionDeduplicationWindowSeconds, _projectRootAccessor, _tokenTracker, _planHandoff, _config.ToolResultCharBudget);
        _filterInvokedHandler = call => OnFunctionInvoked?.Invoke(call);
        _filterCompletedHandler = result => OnFunctionCompleted?.Invoke(result);
        _filterStartedHandler = () => _completionTracker.RegisterStart();
        _filterFinishedHandler = () => _completionTracker.RegisterCompletion();
        _functionFilter.OnFunctionInvoked += _filterInvokedHandler;
        _functionFilter.OnFunctionCompleted += _filterCompletedHandler;
        _functionFilter.OnFunctionStarted += _filterStartedHandler;
        _functionFilter.OnFunctionFinished += _filterFinishedHandler;

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
            var modelName = _config.GetEffectiveModelName();

            // Check if model exists and get its info
            var response = await client.PostAsync(
                $"{_config.OllamaEndpoint}/api/show",
                new StringContent(JsonSerializer.Serialize(new { name = modelName }), System.Text.Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"Model '{modelName}' not found. Run: ollama pull {modelName}");
            }

            // Model exists and is available — Ollama handles tool support at the API level
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
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var result = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await _chatService.GetChatMessageContentAsync(
                    _chatHistory,
                    _settings,
                    _kernel,
                    linkedCts.Token
                ),
                _config.MaxRetryAttempts,
                "ChatStreamAsync",
                linkedCts.Token
            );

            ExtractAndRecordTokens(result, "Chat");

            var rawResponse = result.Content ?? "No response from AI.";
            response = _config.EnableFallbackFunctionParsing
                ? await ProcessTextFunctionCallsAsync(rawResponse)
                : rawResponse;

            if (result.InnerContent is OllamaSharp.Models.Chat.ChatDoneResponseStream doneStream
                && string.Equals(doneStream.DoneReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                response += "\n\n⚠ Response was cut off (hit the token limit). " +
                           "You can say \"continue\" to keep going, or increase max tokens with /config.";
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
        catch (OperationCanceledException)
        {
            response = "Error: Request timed out. The model took too long to respond.\n\n" +
                      "Try breaking your request into smaller parts, or use a faster model.";
        }
        // Provider-side context-window rejection in direct chat — compact the history
        // and auto-retry. The persistent _chatHistory is replaced with a compacted recap
        // so the next turn fits under the provider's limit.
        catch (Exception ex) when (IsContextOverflowError(ex)
                                    && _config.EnableAutoContinuation
                                    && continuationIndex < _config.MaxAutoContinuations)
        {
            await CompactChatHistoryAsync();
            needsContinuation = true;
            response = $"⚠ Provider rejected request (context window full). " +
                       $"Compacting conversation history and retrying ({continuationIndex + 1}/{_config.MaxAutoContinuations})...\n";
        }
        catch (HttpRequestException ex)
        {
            response = $"Error: Connection to Ollama failed.\n\n" +
                      $"Details: {ex.Message}\n\n" +
                      "Make sure Ollama is running: ollama serve";
        }
        catch (Exception ex)
        {
            response = FormatErrorMessage(ex);
        }

        return (response, needsContinuation);
    }

    /// <summary>
    /// Formats error messages for display.
    /// </summary>
    private string FormatErrorMessage(Exception ex)
    {
        // Context-window overflow — actionable message, don't blame Ollama setup.
        if (IsContextOverflowError(ex))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' rejected the request because the conversation exceeded its context window.\n\n" +
                   $"Details: {ex.Message}\n\n" +
                   "What to do:\n" +
                   "  • Try /clear to start a fresh conversation, OR\n" +
                   $"  • Lower the tool-result budget: mandocode --config set toolBudget 50000, OR\n" +
                   "  • Switch to a model with a larger context window via /config.";
        }

        // Check if the error is about tool support
        if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' does not support tool calling.\n\n" +
                   $"MandoCode uses agentic tool calling to read, write, and manage files.\n" +
                   $"Your current model doesn't support this — you'll need to switch to a tool-enabled model.\n\n" +
                   $"To change your model, run /config and select a model that supports tool use.\n\n" +
                   $"Cloud models (no GPU required):\n" +
                   $"  • kimi-k2.5:cloud\n" +
                   $"  • minimax-m2.5:cloud\n" +
                   $"  • qwen3-coder:480b-cloud\n\n" +
                   $"Local models:\n" +
                   $"  • qwen3:8b (recommended, runs on most hardware)\n" +
                   $"  • qwen2.5-coder:7b\n" +
                   $"  • mistral\n" +
                   $"  • llama3.1";
        }

        return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
    }

    /// <summary>
    /// Executes a single step of a task plan with function calling enabled.
    /// Uses previous step results as context for continuity.
    /// </summary>
    public async Task<string> ExecutePlanStepAsync(string stepInstruction, List<string> previousResults, CancellationToken cancellationToken = default)
    {
        // Build context from previous step results — only include last 2 steps
        // to keep token count manageable as plans grow
        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine(_systemPrompt);

        var recentResults = previousResults.Count > 2
            ? previousResults.Skip(previousResults.Count - 2).ToList()
            : previousResults;

        if (recentResults.Any())
        {
            contextBuilder.AppendLine("\n--- Results from Previous Steps ---");
            foreach (var result in recentResults)
            {
                contextBuilder.AppendLine(result);
            }
            contextBuilder.AppendLine("--- End of Previous Steps ---\n");
        }

        // Create a temporary chat history for this step
        var stepHistory = new ChatHistory(contextBuilder.ToString());
        stepHistory.AddUserMessage($"Execute this step now: {stepInstruction}\n\nRemember: Use the available functions to complete this task. Do not describe the function call - actually invoke it.");

        // Allow concurrent function invocation within a step for parallel file operations
        var stepSettings = new OllamaPromptExecutionSettings
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
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.RequestTimeoutMinutes));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

                    var result = await RetryPolicy.ExecuteWithRetryAsync(
                        async () => await _chatService.GetChatMessageContentAsync(
                            stepHistory,
                            stepSettings,
                            _kernel,
                            linkedCts.Token
                        ),
                        _config.MaxRetryAttempts,
                        "ExecutePlanStepAsync",
                        linkedCts.Token
                    );

                    ExtractAndRecordTokens(result, stepLabel);

                    var response = result.Content ?? "Step completed (no response content).";

                    await _completionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(5));

                    processedResponse = _config.EnableFallbackFunctionParsing
                        ? await ProcessTextFunctionCallsAsync(response)
                        : response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Step cancelled.", cancellationToken);
                }
                catch (OperationCanceledException)
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
                    throw new Exception($"Connection to Ollama failed: {ex.Message}");
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
    /// Processes text responses to detect and execute function calls that were output as text.
    /// Some local models (especially smaller ones) output function calls as JSON text instead of proper tool calls.
    /// Models known to need this fallback: smaller qwen variants, some mistral variants.
    /// </summary>
    private async Task<string> ProcessTextFunctionCallsAsync(string response)
    {
        // Extract JSON objects that look like function calls
        var functionCalls = ExtractFunctionCallsFromText(response);

        if (functionCalls.Count == 0)
        {
            return response;
        }

        // Log fallback invocation for debugging
        System.Diagnostics.Debug.WriteLine(
            $"[FallbackParsing] Detected {functionCalls.Count} text-based function call(s) in response. Model: {_config.GetEffectiveModelName()}");

        var resultBuilder = new System.Text.StringBuilder();
        var functionsExecuted = new List<string>();

        foreach (var (functionName, parametersJson) in functionCalls)
        {
            try
            {
                // Normalize function name (remove FileSystem_ prefix if present)
                var normalizedName = functionName.Replace("FileSystem_", "").Replace("FileSystem.", "");

                // Map common function name variations
                normalizedName = NormalizeFunctionName(normalizedName);

                System.Diagnostics.Debug.WriteLine(
                    $"[FallbackParsing] Invoking function: {normalizedName} (original: {functionName})");

                // Try to find and invoke the function
                var functionResult = await InvokeFunctionByNameAsync(normalizedName, parametersJson);

                if (functionResult != null)
                {
                    functionsExecuted.Add($"{normalizedName}: {functionResult}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FallbackParsing] Error executing function {functionName}: {ex.Message}");
                functionsExecuted.Add($"Error executing function: {ex.Message}");
            }
        }

        if (functionsExecuted.Any())
        {
            // Clean up the response by removing JSON function call text
            var cleanedResponse = response;

            // Remove JSON objects that look like function calls
            cleanedResponse = RemoveFunctionCallJson(cleanedResponse);

            // Remove common preamble text
            cleanedResponse = Regex.Replace(cleanedResponse, @"Here is the JSON for the function call:?\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = Regex.Replace(cleanedResponse, @"I will (call|use) the [`']?[\w_]+[`']? function[^.]*\.\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = Regex.Replace(cleanedResponse, @"To \w+ the \w+[^,]*,\s*I will use the [`']?[\w_]+[`']? function[^.]*\.\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = cleanedResponse.Trim();

            if (!string.IsNullOrWhiteSpace(cleanedResponse))
            {
                resultBuilder.AppendLine(cleanedResponse);
                resultBuilder.AppendLine();
            }

            resultBuilder.AppendLine("--- Function Results ---");
            foreach (var result in functionsExecuted)
            {
                resultBuilder.AppendLine(result);
            }

            return resultBuilder.ToString();
        }

        return response;
    }

    /// <summary>
    /// Extracts function calls from text by finding JSON objects with "name" and "parameters" fields.
    /// Handles nested braces properly and supports multiple JSON formats:
    /// - {"name": "func", "parameters": {...}}
    /// - {"function_call": {"name": "func", "arguments": {...}}}
    /// - {"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}
    /// </summary>
    private List<(string FunctionName, string ParametersJson)> ExtractFunctionCallsFromText(string text)
    {
        var results = new List<(string, string)>();

        // Pattern 1: Standard {"name": "func", "parameters": {...}}
        var namePattern = @"\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""(?:parameters|arguments)""\s*:\s*";
        var matches = Regex.Matches(text, namePattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var functionName = match.Groups[1].Value;
            var startIndex = match.Index + match.Length;

            // Extract the parameters JSON by counting braces
            var parametersJson = ExtractJsonObject(text, startIndex);

            if (!string.IsNullOrEmpty(parametersJson))
            {
                results.Add((functionName, parametersJson));
            }
        }

        // Pattern 2: {"function_call": {"name": "func", "arguments": {...}}}
        if (results.Count == 0)
        {
            var fcPattern = @"""function_call""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:\s*";
            matches = Regex.Matches(text, fcPattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var functionName = match.Groups[1].Value;
                var startIndex = match.Index + match.Length;
                var parametersJson = ExtractJsonObject(text, startIndex);

                if (!string.IsNullOrEmpty(parametersJson))
                {
                    results.Add((functionName, parametersJson));
                }
            }
        }

        // Pattern 3: {"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}
        if (results.Count == 0)
        {
            var tcPattern = @"""function""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:\s*";
            matches = Regex.Matches(text, tcPattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var functionName = match.Groups[1].Value;
                var startIndex = match.Index + match.Length;

                // Arguments might be a string (escaped JSON) or an object
                var argsStart = text.IndexOf('"', startIndex);
                if (argsStart == startIndex || argsStart == startIndex + 1)
                {
                    // Arguments is a string, need to extract and unescape
                    var argsEnd = FindStringEnd(text, argsStart);
                    if (argsEnd > argsStart)
                    {
                        var argsStr = text.Substring(argsStart + 1, argsEnd - argsStart - 1);
                        // Unescape the JSON string
                        argsStr = argsStr.Replace("\\\"", "\"").Replace("\\\\", "\\");
                        results.Add((functionName, argsStr));
                    }
                }
                else
                {
                    var parametersJson = ExtractJsonObject(text, startIndex);
                    if (!string.IsNullOrEmpty(parametersJson))
                    {
                        results.Add((functionName, parametersJson));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the end of a JSON string starting at the given position.
    /// </summary>
    private static int FindStringEnd(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '"')
            return -1;

        for (int i = startIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++; // Skip escaped character
                continue;
            }
            if (text[i] == '"')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Extracts a JSON object starting at the given index by counting brace depth.
    /// </summary>
    /// <summary>
    /// Finds the bounds (start, end exclusive) of a JSON object starting at the given index.
    /// Handles nested braces and string escaping. Returns null if no valid object found.
    /// </summary>
    private static (int Start, int End)? FindJsonObjectBounds(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '{')
            return null;

        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escapeNext) { escapeNext = false; continue; }
            if (c == '\\' && inString) { escapeNext = true; continue; }
            if (c == '"') { inString = !inString; continue; }

            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return (startIndex, i + 1);
                }
            }
        }

        return null;
    }

    private string ExtractJsonObject(string text, int startIndex)
    {
        var bounds = FindJsonObjectBounds(text, startIndex);
        return bounds.HasValue ? text[bounds.Value.Start..bounds.Value.End] : string.Empty;
    }

    /// <summary>
    /// Removes function call JSON from the response text.
    /// </summary>
    private string RemoveFunctionCallJson(string text)
    {
        var result = text;
        var namePattern = @"\{\s*""name""\s*:\s*""[^""]+""";
        var matches = Regex.Matches(text, namePattern);

        var jsonRanges = new List<(int Start, int End)>();

        foreach (Match match in matches)
        {
            var bounds = FindJsonObjectBounds(text, match.Index);
            if (bounds.HasValue)
            {
                jsonRanges.Add(bounds.Value);
            }
        }

        // Remove ranges in reverse order to maintain indices
        foreach (var (start, end) in jsonRanges.OrderByDescending(r => r.Start))
        {
            result = result.Remove(start, end - start);
        }

        return result;
    }

    // Alias dictionary for common alternative function names
    private static readonly Dictionary<string, string> FunctionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common short aliases
        { "mkdir", "create_folder" },
        { "make_directory", "create_folder" },
        { "create_directory", "create_folder" },
        { "read_file", "read_file_contents" },
        { "remove_file", "delete_file" },
        { "rm", "delete_file" },
        { "unlink", "delete_file" },
        { "list_files", "list_files_match_glob_pattern" },
        { "list_files_glob", "list_files_match_glob_pattern" },
        { "list_project_files", "list_all_project_files" },
        { "search_in_files", "search_text_in_files" },
        { "find_in_files", "search_text_in_files" },
    };

    /// <summary>
    /// Normalizes function names to match the actual plugin function names.
    /// Uses convention-based conversion (PascalCase/camelCase to snake_case) and alias lookup.
    /// </summary>
    private string NormalizeFunctionName(string functionName)
    {
        // First check aliases for common alternative names
        if (FunctionAliases.TryGetValue(functionName, out var aliasedName))
        {
            return aliasedName;
        }

        // Convert to snake_case if it appears to be PascalCase or camelCase
        var snakeCased = ToSnakeCase(functionName);

        // Check aliases again with the converted name
        if (FunctionAliases.TryGetValue(snakeCased, out aliasedName))
        {
            return aliasedName;
        }

        return snakeCased;
    }

    /// <summary>
    /// Converts PascalCase or camelCase to snake_case.
    /// </summary>
    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // If already snake_case (contains underscore), just lowercase it
        if (input.Contains('_'))
            return input.ToLowerInvariant();

        // Convert PascalCase/camelCase to snake_case
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                // Add underscore before uppercase letters (except at start)
                if (i > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Invokes a FileSystem plugin function by name with JSON parameters.
    /// </summary>
    private async Task<string?> InvokeFunctionByNameAsync(string functionName, string parametersJson)
    {
        try
        {
            // Get the FileSystem plugin
            if (!_kernel.Plugins.TryGetPlugin("FileSystem", out var plugin))
            {
                return null;
            }

            // Find the function
            if (!plugin.TryGetFunction(functionName, out var function))
            {
                // Try case-insensitive search
                function = plugin.FirstOrDefault(f =>
                    f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

                if (function == null)
                {
                    return $"Function '{functionName}' not found in FileSystem plugin";
                }
            }

            // Parse parameters
            var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson);
            if (parameters == null)
            {
                return "Failed to parse function parameters";
            }

            // Build kernel arguments
            var arguments = new KernelArguments();
            foreach (var param in parameters)
            {
                // Convert parameter name to match function parameter names
                var paramName = NormalizeParameterName(param.Key);
                var paramValue = param.Value.ValueKind == JsonValueKind.String
                    ? param.Value.GetString()
                    : param.Value.ToString();

                arguments[paramName] = paramValue;
            }

            // Raise function invoked event
            OnFunctionInvoked?.Invoke(new FunctionCall
            {
                FunctionName = functionName,
                Description = $"Executing {functionName} (fallback)",
                Arguments = parameters.ToDictionary(p => p.Key, p => (object?)p.Value.ToString())
            });

            // Invoke the function
            var result = await function.InvokeAsync(_kernel, arguments);
            var resultString = result.GetValue<string>() ?? result.ToString() ?? "Function completed";

            // Raise function completed event
            OnFunctionCompleted?.Invoke(new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = resultString.Length > 200 ? resultString[..200] + "..." : resultString,
                Success = true
            });

            return resultString;
        }
        catch (Exception ex)
        {
            OnFunctionCompleted?.Invoke(new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = ex.Message,
                Success = false
            });

            return $"Error: {ex.Message}";
        }
    }

    // Static parameter name mappings to avoid allocation per call
    private static readonly Dictionary<string, string> ParamMappings =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "pattern", "pattern" },
        { "glob_pattern", "pattern" },
        { "globPattern", "pattern" },
        { "relativePath", "relativePath" },
        { "relative_path", "relativePath" },
        { "path", "relativePath" },
        { "filePath", "relativePath" },
        { "file_path", "relativePath" },
        { "content", "content" },
        { "file_content", "content" },
        { "fileContent", "content" },
        { "searchPattern", "searchPattern" },
        { "search_pattern", "searchPattern" },
        { "query", "searchPattern" },
    };

    /// <summary>
    /// Normalizes parameter names to match function parameter names.
    /// </summary>
    private static string NormalizeParameterName(string paramName)
    {
        return ParamMappings.TryGetValue(paramName, out var normalizedName)
            ? normalizedName
            : paramName;
    }

    /// <summary>
    /// Extracts real token counts from a ChatMessageContent response and records them.
    /// Non-critical — failures are silently swallowed.
    /// </summary>
    private void ExtractAndRecordTokens(ChatMessageContent response, string label)
    {
        try
        {
            if (response.InnerContent is OllamaSharp.Models.Chat.ChatDoneResponseStream done)
            {
                var promptTokens = done.PromptEvalCount;
                var completionTokens = done.EvalCount;
                if (promptTokens > 0 || completionTokens > 0)
                {
                    // EvalDuration is in nanoseconds — convert to seconds
                    double? generationSeconds = done.EvalDuration > 0
                        ? done.EvalDuration / 1_000_000_000.0
                        : null;

                    _tokenTracker.RecordModelUsage(promptTokens, completionTokens, label, generationSeconds);
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
