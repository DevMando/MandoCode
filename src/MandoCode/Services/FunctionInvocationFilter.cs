using Microsoft.SemanticKernel;
using MandoCode.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MandoCode.Services;

/// <summary>
/// Filter that intercepts function invocations to emit events for the UI.
/// Includes deduplication to prevent repeated identical calls.
/// </summary>
public class FunctionInvocationFilter : IFunctionInvocationFilter
{
    /// <summary>
    /// Event raised when a function is about to be invoked.
    /// </summary>
    public event Action<FunctionCall>? OnFunctionInvoked;

    /// <summary>
    /// Event raised when a function completes (success or failure).
    /// </summary>
    public event Action<FunctionExecutionResult>? OnFunctionCompleted;

    /// <summary>
    /// Async callback for requesting user approval before writing a file.
    /// Parameters: (relativePath, oldContent, newContent) → DiffApprovalResult.
    /// </summary>
    public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested { get; set; }

    /// <summary>
    /// Async callback for requesting user approval before deleting a file.
    /// Parameters: (relativePath, existingContent) → DiffApprovalResult.
    /// </summary>
    public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested { get; set; }

    // Project root for resolving file paths when reading existing content
    private readonly string? _projectRoot;

    // Cache for deduplication - stores recent function calls and their results
    private readonly Dictionary<string, (DateTime Time, object? Result)> _recentCalls = new();

    // Deduplication windows for different operation types
    private readonly TimeSpan _readDeduplicationWindow;
    private readonly TimeSpan _writeDeduplicationWindow;

    // Count of currently executing functions (for completion tracking)
    private int _pendingFunctionCount;
    private readonly object _pendingLock = new();

    /// <summary>
    /// Gets the number of functions currently executing.
    /// </summary>
    public int PendingFunctionCount
    {
        get { lock (_pendingLock) return _pendingFunctionCount; }
    }

    /// <summary>
    /// Event raised when a function starts execution.
    /// </summary>
    public event Action? OnFunctionStarted;

    /// <summary>
    /// Event raised when a function finishes execution (success or failure).
    /// </summary>
    public event Action? OnFunctionFinished;

    public FunctionInvocationFilter() : this(5)
    {
    }

    public FunctionInvocationFilter(int defaultDeduplicationWindowSeconds, string? projectRoot = null)
    {
        // Read operations use shorter window (2s), writes use configured window
        _readDeduplicationWindow = TimeSpan.FromSeconds(2);
        _writeDeduplicationWindow = TimeSpan.FromSeconds(defaultDeduplicationWindowSeconds);
        _projectRoot = projectRoot;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Build function name and description
        var functionName = $"{context.Function.PluginName}_{context.Function.Name}";
        var description = GetFunctionDescription(context.Function.PluginName, context.Function.Name, context.Arguments);

        // Determine if this is a write operation
        var isWriteOperation = IsWriteOperation(context.Function.Name);
        var deduplicationWindow = isWriteOperation ? _writeDeduplicationWindow : _readDeduplicationWindow;

        // Create a unique key for this function call (function name + arguments + content hash for writes)
        var callKey = CreateCallKey(functionName, context.Arguments, isWriteOperation);

        // Check for duplicate call within the deduplication window
        if (_recentCalls.TryGetValue(callKey, out var cached) &&
            DateTime.UtcNow - cached.Time < deduplicationWindow)
        {
            // Skip duplicate call - use cached result
            context.Result = new Microsoft.SemanticKernel.FunctionResult(
                context.Function,
                cached.Result ?? "Operation already completed."
            );
            return;
        }

        // Track pending function
        lock (_pendingLock) _pendingFunctionCount++;
        OnFunctionStarted?.Invoke();

        // Emit function call event before invocation
        var functionCall = new FunctionCall
        {
            FunctionName = functionName,
            Description = description,
            Arguments = context.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        OnFunctionInvoked?.Invoke(functionCall);

        // Intercept write_file calls for diff approval
        if (context.Function.Name == "write_file" && OnWriteApprovalRequested != null)
        {
            var approvalResult = await HandleWriteApprovalAsync(context);
            if (approvalResult != null)
            {
                // User denied or redirected — skip actual write
                context.Result = new Microsoft.SemanticKernel.FunctionResult(
                    context.Function,
                    approvalResult
                );

                var skipResult = new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = approvalResult.Length > 200 ? approvalResult[..200] + "..." : approvalResult,
                    Success = true
                };
                OnFunctionCompleted?.Invoke(skipResult);

                lock (_pendingLock) _pendingFunctionCount--;
                OnFunctionFinished?.Invoke();
                return;
            }
        }

        // Intercept delete_file calls for approval
        if (context.Function.Name == "delete_file" && OnDeleteApprovalRequested != null)
        {
            var approvalResult = await HandleDeleteApprovalAsync(context);
            if (approvalResult != null)
            {
                context.Result = new Microsoft.SemanticKernel.FunctionResult(
                    context.Function,
                    approvalResult
                );

                var skipResult = new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = approvalResult.Length > 200 ? approvalResult[..200] + "..." : approvalResult,
                    Success = true
                };
                OnFunctionCompleted?.Invoke(skipResult);

                lock (_pendingLock) _pendingFunctionCount--;
                OnFunctionFinished?.Invoke();
                return;
            }
        }

        // Invoke the function
        try
        {
            await next(context);

            // Cache the result for deduplication
            _recentCalls[callKey] = (DateTime.UtcNow, context.Result?.GetValue<object>());
            CleanupOldEntries();

            // Check if result indicates an error from the plugin itself
            var resultStr = context.Result?.ToString() ?? string.Empty;
            var isError = resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

            var successResult = new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = TruncateResult(resultStr),
                Success = !isError
            };

            OnFunctionCompleted?.Invoke(successResult);
        }
        catch (Exception ex)
        {
            // Emit error result but don't re-throw - let the AI see and handle the error
            var errorResult = new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = $"Error: {ex.Message}",
                Success = false
            };

            OnFunctionCompleted?.Invoke(errorResult);

            // Set the result so the AI can see the error
            context.Result = new Microsoft.SemanticKernel.FunctionResult(
                context.Function,
                $"Function failed: {ex.Message}"
            );
        }
        finally
        {
            // Track completion
            lock (_pendingLock) _pendingFunctionCount--;
            OnFunctionFinished?.Invoke();
        }
    }

    /// <summary>
    /// Handles the write approval workflow. Returns a result message to use instead of writing,
    /// or null if the write should proceed normally.
    /// </summary>
    private async Task<string?> HandleWriteApprovalAsync(FunctionInvocationContext context)
    {
        if (OnWriteApprovalRequested == null)
            return null;

        // Extract arguments
        context.Arguments.TryGetValue("relativePath", out var pathObj);
        context.Arguments.TryGetValue("content", out var contentObj);

        var relativePath = pathObj?.ToString();
        var newContent = contentObj?.ToString();

        if (string.IsNullOrEmpty(relativePath) || newContent == null)
            return null;

        // Read existing file content (null if new file)
        string? oldContent = null;
        if (!string.IsNullOrEmpty(_projectRoot))
        {
            var fullPath = Path.Combine(_projectRoot, relativePath);
            fullPath = Path.GetFullPath(fullPath);
            if (File.Exists(fullPath))
            {
                try
                {
                    oldContent = await File.ReadAllTextAsync(fullPath);
                }
                catch
                {
                    // If we can't read the file, treat it as new
                }
            }
        }

        // Request approval from the UI
        var approval = await OnWriteApprovalRequested(relativePath, oldContent, newContent);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null; // Proceed with the write

            case DiffApprovalResponse.Denied:
                return $"User denied the file write to '{relativePath}'. Do not retry this write unless the user asks.";

            case DiffApprovalResponse.NewInstructions:
                return $"User rejected the file write to '{relativePath}' and provided new instructions: {approval.UserMessage}";

            default:
                return null;
        }
    }

    /// <summary>
    /// Handles the delete approval workflow. Returns a result message to use instead of deleting,
    /// or null if the delete should proceed normally.
    /// </summary>
    private async Task<string?> HandleDeleteApprovalAsync(FunctionInvocationContext context)
    {
        if (OnDeleteApprovalRequested == null)
            return null;

        context.Arguments.TryGetValue("relativePath", out var pathObj);
        var relativePath = pathObj?.ToString();

        if (string.IsNullOrEmpty(relativePath))
            return null;

        // Read existing file content to show in the diff
        string? existingContent = null;
        if (!string.IsNullOrEmpty(_projectRoot))
        {
            var fullPath = Path.Combine(_projectRoot, relativePath);
            fullPath = Path.GetFullPath(fullPath);
            if (File.Exists(fullPath))
            {
                try
                {
                    existingContent = await File.ReadAllTextAsync(fullPath);
                }
                catch
                {
                    // If we can't read it, show deletion without content
                }
            }
        }

        var approval = await OnDeleteApprovalRequested(relativePath, existingContent);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null; // Proceed with the delete

            case DiffApprovalResponse.Denied:
                return $"User denied the deletion of '{relativePath}'. Do not retry unless the user asks.";

            case DiffApprovalResponse.NewInstructions:
                return $"User rejected the deletion of '{relativePath}' and provided new instructions: {approval.UserMessage}";

            default:
                return null;
        }
    }

    /// <summary>
    /// Determines if a function is a write operation (modifies files/folders).
    /// </summary>
    private bool IsWriteOperation(string? functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return false;

        return functionName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("delete", StringComparison.OrdinalIgnoreCase);
    }

    private string GetFunctionDescription(string? pluginName, string? functionName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (pluginName == "FileSystem")
        {
            switch (functionName)
            {
                case "create_folder":
                    if (arguments.TryGetValue("relativePath", out var folderPath))
                        return $"Creating folder {folderPath}";
                    return "Creating folder";

                case "write_file":
                    if (arguments.TryGetValue("relativePath", out var writePath))
                        return $"Writing to {writePath}";
                    return "Writing file";

                case "delete_file":
                    if (arguments.TryGetValue("relativePath", out var deletePath))
                        return $"Deleting {deletePath}";
                    return "Deleting file";

                case "read_file_contents":
                    if (arguments.TryGetValue("relativePath", out var readPath))
                        return $"Reading {readPath}";
                    return "Reading file";

                case "list_all_project_files":
                    return "Listing all project files";

                case "list_files_match_glob_pattern":
                    if (arguments.TryGetValue("pattern", out var pattern))
                        return $"Finding files matching '{pattern}'";
                    return "Listing files";

                case "search_text_in_files":
                    if (arguments.TryGetValue("searchText", out var searchText))
                        return $"Searching for '{searchText}'";
                    return "Searching files";

                case "get_absolute_path":
                    if (arguments.TryGetValue("relativePath", out var absPath))
                        return $"Getting absolute path for {absPath}";
                    return "Getting absolute path";

                default:
                    return $"{functionName}";
            }
        }

        return $"{pluginName ?? "Unknown"}.{functionName ?? "Unknown"}";
    }

    /// <summary>
    /// Truncates large results to avoid flooding the console.
    /// </summary>
    private string TruncateResult(string result, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(result))
            return "[empty]";

        if (result.Length <= maxLength)
            return result;

        return result.Substring(0, maxLength) + "... [truncated]";
    }

    /// <summary>
    /// Creates a unique key for a function call based on name and arguments.
    /// For write operations, includes a hash of the content to detect duplicate writes with same content.
    /// </summary>
    private string CreateCallKey(string functionName, IReadOnlyDictionary<string, object?> arguments, bool isWriteOperation)
    {
        // For write operations, include the path and a hash of the content
        if (isWriteOperation && functionName.Contains("write_file"))
        {
            var keyBuilder = new StringBuilder(functionName);

            if (arguments.TryGetValue("relativePath", out var path))
            {
                keyBuilder.Append(':').Append(path);
            }

            // Include content hash to detect different content being written to same path
            if (arguments.TryGetValue("content", out var content) && content != null)
            {
                var contentStr = content.ToString() ?? "";
                var contentHash = ComputeContentHash(contentStr);
                keyBuilder.Append(':').Append(contentHash);
            }

            return keyBuilder.ToString();
        }

        // For other operations, include all arguments
        var argsJson = JsonSerializer.Serialize(arguments.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value?.ToString() ?? ""));
        return $"{functionName}:{argsJson}";
    }

    /// <summary>
    /// Computes a short hash of the content for deduplication purposes.
    /// </summary>
    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = SHA256.HashData(bytes);
        // Use first 8 bytes for a shorter hash (sufficient for dedup purposes)
        return Convert.ToHexString(hashBytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Removes old entries from the deduplication cache.
    /// </summary>
    private void CleanupOldEntries()
    {
        // Use the longer window for cleanup to ensure we don't remove entries prematurely
        var cutoff = DateTime.UtcNow - _writeDeduplicationWindow;
        var keysToRemove = _recentCalls
            .Where(kvp => kvp.Value.Time < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _recentCalls.Remove(key);
        }
    }

    /// <summary>
    /// Clears the deduplication cache. Call this when starting a new conversation.
    /// </summary>
    public void ClearCache()
    {
        _recentCalls.Clear();
    }
}
