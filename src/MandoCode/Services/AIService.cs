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
    private readonly string _projectRoot;
    private readonly FunctionCompletionTracker _completionTracker = new();
    private FunctionInvocationFilter _functionFilter;

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

    public AIService(string projectRoot, MandoCodeConfig config)
    {
        _projectRoot = projectRoot;
        _config = config;
        _systemPrompt = SystemPrompts.MandoCodeAssistant;

        BuildKernel();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Reinitializes the AI service with a new configuration.
    /// Rebuilds the kernel with the updated model and settings.
    /// </summary>
    public void Reinitialize(MandoCodeConfig config)
    {
        _config = config;
        BuildKernel();
        ClearHistory();
    }

    private void BuildKernel()
    {
        _settings = new()
        {
            Temperature = (float)_config.Temperature,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
        };

        var builder = Kernel.CreateBuilder();

        builder.AddOllamaChatCompletion(
            modelId: _config.GetEffectiveModelName(),
            endpoint: new Uri(_config.OllamaEndpoint)
        );

        var fileSystemPlugin = new FileSystemPlugin(_projectRoot);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Set up function invocation filter for UI events and deduplication
        _functionFilter = new FunctionInvocationFilter(_config.FunctionDeduplicationWindowSeconds, _projectRoot);
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

            var content = await response.Content.ReadAsStringAsync();

            // Check modelfile for known tool-supporting model families
            var supportedModelFamilies = new[]
            {
                "qwen", "mistral", "llama3", "llama-3", "mixtral", "command-r",
                "deepseek", "phi3", "phi-3", "gemma2", "gemma-2"
            };

            var modelLower = modelName.ToLowerInvariant();
            var hasToolSupport = supportedModelFamilies.Any(family => modelLower.Contains(family));

            if (!hasToolSupport)
            {
                return (false, $"Warning: Model '{modelName}' may not support function calling.\n\n" +
                    "Recommended models with tool support:\n" +
                    "  - qwen2.5-coder:14b (best for coding)\n" +
                    "  - qwen2.5-coder:7b\n" +
                    "  - mistral\n" +
                    "  - llama3.1\n\n" +
                    "Continue anyway? The model might not execute file operations correctly.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Could not validate model: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a message to the AI and gets a response.
    /// Includes retry logic for transient connection errors.
    /// </summary>
    public async Task<string> ChatAsync(string userMessage)
    {
        _chatHistory.AddUserMessage(userMessage);

        try
        {
            // Get the response with automatic function calling, with retry for transient errors
            var response = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await _chatService.GetChatMessageContentAsync(
                    _chatHistory,
                    _settings,
                    _kernel
                ),
                _config.MaxRetryAttempts,
                "ChatAsync"
            );

            // Add the final response to history
            if (!string.IsNullOrEmpty(response.Content))
            {
                _chatHistory.AddAssistantMessage(response.Content);
            }

            var rawResponse = response.Content ?? "No response from AI.";

            // Check if the model output function calls as text instead of invoking them
            var processedResponse = _config.EnableFallbackFunctionParsing
                ? await ProcessTextFunctionCallsAsync(rawResponse)
                : rawResponse;

            // Update history with processed response
            if (!string.IsNullOrEmpty(processedResponse) && processedResponse != rawResponse)
            {
                // Remove the raw response and add processed one
                if (_chatHistory.Count > 0 && _chatHistory.Last().Role == AuthorRole.Assistant)
                {
                    _chatHistory.RemoveAt(_chatHistory.Count - 1);
                }
                _chatHistory.AddAssistantMessage(processedResponse);
            }

            return processedResponse;
        }
        catch (Exception ex)
        {
            // Check if the error is about tool support
            if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
            {
                return $"Error: The model '{_config.GetEffectiveModelName()}' does not support function calling (tools).\n\n" +
                       $"MandoCode requires a model with function calling support to use FileSystem plugins.\n\n" +
                       $"Recommended models with tool support:\n" +
                       $"  • ollama pull minimax-m2.5:cloud\n" +
                       $"  • ollama pull qwen2.5-coder:14b\n" +
                       $"  • ollama pull mistral\n" +
                       $"  • ollama pull llama3.1\n\n" +
                       $"Then update your configuration:\n" +
                       $"  Type 'config' and select 'Run configuration wizard'\n" +
                       $"  Or run: dotnet run -- config set model minimax-m2.5:cloud";
            }

            return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
        }
    }

    /// <summary>
    /// Sends a message to the AI and streams the response chunk by chunk.
    /// NOTE: Uses non-streaming mode internally for reliable function execution with local models.
    /// Streaming with auto-invocation causes issues where function calls are not properly parsed
    /// or executed by the Semantic Kernel with local Ollama models.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string userMessage)
    {
        _chatHistory.AddUserMessage(userMessage);

        string response;
        try
        {
            // Use a cancellation token with timeout for long operations
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var result = await _chatService.GetChatMessageContentAsync(
                _chatHistory,
                _settings,
                _kernel,
                cts.Token
            );

            var rawResponse = result.Content ?? "No response from AI.";

            // Check if the model output function calls as text instead of invoking them
            response = await ProcessTextFunctionCallsAsync(rawResponse);

            // Add the response to history
            if (!string.IsNullOrEmpty(response))
            {
                _chatHistory.AddAssistantMessage(response);
            }
        }
        catch (OperationCanceledException)
        {
            response = "Error: Request timed out. The model took too long to respond.\n\n" +
                      "Try breaking your request into smaller parts, or use a faster model.";
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

        // Yield the complete response
        yield return response;
    }

    /// <summary>
    /// Formats error messages for display.
    /// </summary>
    private string FormatErrorMessage(Exception ex)
    {
        // Check if the error is about tool support
        if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
        {
            return $"Error: The model '{_config.GetEffectiveModelName()}' does not support function calling (tools).\n\n" +
                   $"MandoCode requires a model with function calling support to use FileSystem plugins.\n\n" +
                   $"Recommended models with tool support:\n" +
                   $"  • ollama pull qwen2.5-coder:14b\n" +
                   $"  • ollama pull qwen2.5-coder:7b\n" +
                   $"  • ollama pull mistral\n" +
                   $"  • ollama pull llama3.1\n\n" +
                   $"Then update your configuration:\n" +
                   $"  Type 'config' and select 'Run configuration wizard'\n" +
                   $"  Or run: dotnet run -- config set model qwen2.5-coder:14b";
        }

        return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
    }

    /// <summary>
    /// Gets a task plan from the AI for a complex request.
    /// Uses a longer timeout to allow the model to generate a complete plan.
    /// </summary>
    public async Task<string> GetPlanAsync(string userMessage)
    {
        // Use a separate chat history for planning (doesn't pollute main conversation)
        var planningHistory = new ChatHistory(SystemPrompts.TaskPlannerPrompt);
        planningHistory.AddUserMessage($"Create a step-by-step plan for: {userMessage}");

        try
        {
            // Use a longer timeout for planning (90 seconds) - local models can be slow
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            // Use simpler settings without function calling for faster planning
            var planSettings = new OllamaPromptExecutionSettings
            {
                Temperature = (float)_config.Temperature
            };

            var result = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await _chatService.GetChatMessageContentAsync(
                    planningHistory,
                    planSettings,
                    _kernel,
                    cts.Token
                ),
                _config.MaxRetryAttempts,
                "GetPlanAsync"
            );

            return result.Content ?? "Failed to generate plan.";
        }
        catch (OperationCanceledException)
        {
            throw new Exception("Planning timed out. The model took too long to generate a plan.");
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Connection to Ollama failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a single step of a task plan with function calling enabled.
    /// Uses previous step results as context for continuity.
    /// </summary>
    public async Task<string> ExecutePlanStepAsync(string stepInstruction, List<string> previousResults)
    {
        // Build context from previous step results
        var contextBuilder = new System.Text.StringBuilder();
        contextBuilder.AppendLine(_systemPrompt);

        if (previousResults.Any())
        {
            contextBuilder.AppendLine("\n--- Results from Previous Steps ---");
            foreach (var result in previousResults)
            {
                contextBuilder.AppendLine(result);
            }
            contextBuilder.AppendLine("--- End of Previous Steps ---\n");
        }

        // Create a temporary chat history for this step
        var stepHistory = new ChatHistory(contextBuilder.ToString());
        stepHistory.AddUserMessage($"Execute this step now: {stepInstruction}\n\nRemember: Use the available functions to complete this task. Do not describe the function call - actually invoke it.");

        try
        {
            // Use the standard timeout for execution (5 minutes)
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            // Use settings that disable concurrent invocation for plan steps
            // This ensures functions complete sequentially before moving to next step
            var stepSettings = new OllamaPromptExecutionSettings
            {
                Temperature = (float)_config.Temperature,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                    autoInvoke: true,
                    options: new() { AllowConcurrentInvocation = false }
                )
            };

            // Execute with retry for transient errors
            var result = await RetryPolicy.ExecuteWithRetryAsync(
                async () => await _chatService.GetChatMessageContentAsync(
                    stepHistory,
                    stepSettings,
                    _kernel,
                    cts.Token
                ),
                _config.MaxRetryAttempts,
                "ExecutePlanStepAsync"
            );

            var response = result.Content ?? "Step completed (no response content).";

            // Wait for any auto-invoked functions to complete using the completion tracker
            await _completionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(30));

            // Check if the model output function calls as text instead of invoking them
            var processedResponse = _config.EnableFallbackFunctionParsing
                ? await ProcessTextFunctionCallsAsync(response)
                : response;

            // Also add to main history so user can see the full conversation
            _chatHistory.AddUserMessage($"[Plan Step] {stepInstruction}");
            _chatHistory.AddAssistantMessage(processedResponse);

            return processedResponse;
        }
        catch (OperationCanceledException)
        {
            throw new Exception("Step execution timed out. Try breaking this step into smaller parts.");
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Connection to Ollama failed: {ex.Message}");
        }
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
    private string ExtractJsonObject(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '{')
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escapeNext = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(startIndex, i - startIndex + 1);
                    }
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Removes function call JSON from the response text.
    /// </summary>
    private string RemoveFunctionCallJson(string text)
    {
        var result = text;
        var namePattern = @"\{\s*""name""\s*:\s*""[^""]+""";
        var matches = Regex.Matches(text, namePattern);

        // Process in reverse order to maintain indices
        var jsonRanges = new List<(int Start, int End)>();

        foreach (Match match in matches)
        {
            // Find the complete JSON object
            var startIndex = match.Index;
            var depth = 0;
            var inString = false;
            var escapeNext = false;
            var endIndex = startIndex;

            for (int i = startIndex; i < text.Length; i++)
            {
                var c = text[i];

                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escapeNext = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            endIndex = i;
                            break;
                        }
                    }
                }
            }

            if (endIndex > startIndex)
            {
                jsonRanges.Add((startIndex, endIndex + 1));
            }
        }

        // Remove ranges in reverse order
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

    /// <summary>
    /// Normalizes parameter names to match function parameter names.
    /// </summary>
    private string NormalizeParameterName(string paramName)
    {
        var paramMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        if (paramMappings.TryGetValue(paramName, out var normalizedName))
        {
            return normalizedName;
        }

        return paramName;
    }

    /// <summary>
    /// Clears the chat history and starts a new conversation.
    /// </summary>
    public void ClearHistory()
    {
        _chatHistory.Clear();
        _chatHistory.AddSystemMessage(_systemPrompt);
        _functionFilter.ClearCache(); // Clear deduplication cache
    }

    /// <summary>
    /// Gets the current chat history.
    /// </summary>
    public IReadOnlyList<ChatMessageContent> GetHistory()
    {
        return _chatHistory.ToList().AsReadOnly();
    }
}
