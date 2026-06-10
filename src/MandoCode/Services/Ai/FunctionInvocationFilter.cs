using Microsoft.SemanticKernel;
using MandoCode.Models;
using MandoCode.Plugins;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Async callback for requesting user approval before executing a shell command.
    /// Parameter: (command) → DiffApprovalResult.
    /// </summary>
    public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested { get; set; }

    /// <summary>
    /// Gate consulted for any function whose plugin name starts with <c>"mcp_"</c>.
    /// First call of each (server, tool) pair prompts the user unless the tool is in the
    /// server's <c>autoApprove</c> list. Null in tests that bypass the UI layer.
    /// </summary>
    public McpApprovalGate? McpApprovalGate { get; set; }

    // Project root accessor for resolving file paths when reading existing content
    private readonly ProjectRootAccessor? _projectRootAccessor;
    private string? ProjectRoot => _projectRootAccessor?.ProjectRoot;

    // Optional token tracker for estimating file operation token costs
    private readonly TokenTrackingService? _tokenTracker;

    // Bridge to the UI for propose_plan interception
    private readonly PlanHandoff? _planHandoff;

    // Per-chat/per-step loop-prevention state. AsyncLocal so each logical flow
    // gets its own stack — avoids races with AllowConcurrentInvocation = true
    // and handles nested scopes (plan step inside a chat turn) without a shared field.
    private readonly AsyncLocal<InvocationScope?> _currentScope = new();
    private readonly long _defaultResultCharBudget;

    // Cache for deduplication - stores recent function calls and their results
    // Uses ConcurrentDictionary for thread safety with AllowConcurrentInvocation = true
    private readonly ConcurrentDictionary<string, (DateTime Time, object? Result)> _recentCalls = new();

    // Deduplication windows for different operation types
    private readonly TimeSpan _readDeduplicationWindow;
    private readonly TimeSpan _writeDeduplicationWindow;

    // Count of currently executing functions (for completion tracking)
    private int _pendingFunctionCount;
    private readonly object _pendingLock = new();

    // Max lines to show in content preview for new file writes
    private const int MaxPreviewLines = 10;

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

    public FunctionInvocationFilter(int defaultDeduplicationWindowSeconds, ProjectRootAccessor? projectRootAccessor = null, TokenTrackingService? tokenTracker = null, PlanHandoff? planHandoff = null, long resultCharBudget = 400_000)
    {
        // Read operations use shorter window (2s), writes use configured window
        _readDeduplicationWindow = TimeSpan.FromSeconds(2);
        _writeDeduplicationWindow = TimeSpan.FromSeconds(defaultDeduplicationWindowSeconds);
        _projectRootAccessor = projectRootAccessor;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        _defaultResultCharBudget = resultCharBudget;
    }

    /// <summary>
    /// Starts a new per-chat or per-step scope. Every tool call within the scope
    /// accrues against its read-dedup set and result-char budget. Scopes nest: an
    /// inner scope replaces the outer one and restores it on Dispose, so a plan
    /// step inside a chat turn gets its own budget.
    /// Returns the scope directly so callers can inspect <see cref="InvocationScope.BudgetExhausted"/>
    /// after the turn completes to decide whether to auto-continue.
    /// </summary>
    public InvocationScope BeginScope()
    {
        var previous = _currentScope.Value;
        var scope = new InvocationScope(_defaultResultCharBudget);
        _currentScope.Value = scope;
        scope.SetOnDispose(() => _currentScope.Value = previous);
        return scope;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Intercept propose_plan before any dedup/UI-event logic — it is handled
        // out-of-band via PlanHandoff instead of being invoked directly.
        if (context.Function.Name == "propose_plan" && _planHandoff != null)
        {
            // One plan per turn. PlanHandoff's recursion guard only covers a plan that's
            // STILL RUNNING — once it finishes, the door reopened and small models walked
            // straight back through it, planning more unrequested work. Hard-stop here.
            if (_currentScope.Value?.PlanAlreadyProcessed == true)
            {
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function,
                    "A plan was already proposed and handled for this request. Do NOT propose another plan " +
                    "or start new work. Respond to the user now with a brief summary of what was accomplished, then stop.");
                return;
            }

            var summary = await HandleProposePlanAsync(context);
            context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, summary);
            return;
        }

        // Circuit breakers — scope-aware. Skip when no scope is active
        // (e.g., direct tool invocation in tests).
        var scope = _currentScope.Value;
        if (scope != null)
        {
            // Budget circuit: if cumulative tool results have filled ~100k tokens,
            // refuse further calls so we bail before the model's context window overflows.
            if (scope.BudgetExhausted)
            {
                var msg = $"Tool-call budget of {scope.ResultCharBudget:N0} chars is exhausted for this turn. " +
                          "Stop calling tools and respond to the user directly with what you have so far. " +
                          "Ask the user to continue in a new message if more work is needed.";
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, msg);
                return;
            }

            // Duplicate-read circuit: the same read args twice, with no intervening
            // write to the same path, is almost always a stuck loop. The key includes
            // the line range, so paging through a large file is never mistaken for a
            // redundant re-read. Path is normalized so aliases of the same file share
            // one entry.
            if (context.Function.Name == "read_file_contents")
            {
                var path = context.Arguments.TryGetValue("relativePath", out var pObj) ? pObj?.ToString() ?? "" : "";
                var pathKey = NormalizePathKey(path);
                var readKey = BuildReadKey(pathKey, context.Arguments);
                if (!string.IsNullOrEmpty(path) && scope.IsRedundantRead(readKey, pathKey))
                {
                    var msg = $"You already read this range of '{path}' this turn and it hasn't changed. " +
                              "Use the content you already have — do NOT re-read the same range. " +
                              "To see a different part of the file, pass startLine/endLine.";
                    context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, msg);
                    return;
                }
            }

            // Shell-read circuit: when a file is too big for read_file_contents' 10K cap,
            // models tend to fall back to `type`/`cat`/`findstr`/`grep` via execute_command.
            // That sidesteps both the read-dedup AND the read-result cache, dumping fresh
            // file content into the history on every call. Steer them back to read_file_contents
            // before approval is shown, before result-chars are charged.
            if (context.Function.Name == "execute_command")
            {
                var cmd = context.Arguments.TryGetValue("command", out var cObj) ? cObj?.ToString() ?? "" : "";
                if (LooksLikeShellFileRead(cmd))
                {
                    var msg = "Refusing to read file contents via shell. " +
                              "Use read_file_contents instead — it's cached, dedup'd within a turn, and respects the tool-result budget. " +
                              "For large files, pass startLine/endLine to read a specific section (the truncation notice names the line to resume from). " +
                              "Shell-based reads (type/cat/head/tail/more/less/findstr/grep/sed/awk against a file) bloat the conversation history.";
                    context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, msg);
                    return;
                }
            }
        }

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

        // Track pending function. The matching decrement lives in the finally below —
        // everything past this point (the MCP gate, approval awaits, UI event handlers,
        // the invocation itself) runs under it, so an exception on any path still brings
        // the count back down. The count drives the stall watchdog's pause/resume: a
        // leaked increment pins the watchdog paused for the rest of the session and turns
        // any later model stall into a silent hang.
        lock (_pendingLock) _pendingFunctionCount++;
        OnFunctionStarted?.Invoke();

        try
        {
            await InvokeCoreAsync(context, next, functionName, description, callKey);
        }
        finally
        {
            lock (_pendingLock) _pendingFunctionCount--;
            OnFunctionFinished?.Invoke();
        }
    }

    /// <summary>
    /// Body of a tracked invocation: MCP gating, UI events, approval interception, and the
    /// call itself. Runs entirely inside the pending-count try/finally in
    /// <see cref="OnFunctionInvocationAsync"/> — early returns here must NOT decrement the
    /// count or raise <see cref="OnFunctionFinished"/> themselves; the caller's finally does.
    ///
    /// Approval awaits are wrapped in <c>WaitAsync(context.CancellationToken)</c>: a wedged
    /// or orphaned prompt otherwise blocks an await that observes no token, which defeats
    /// Esc, the stall watchdog, AND the request ceiling — the turn can never unwind and the
    /// input prompt never returns.
    /// </summary>
    private async Task InvokeCoreAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next,
        string functionName,
        string description,
        string callKey)
    {
        // MCP approval gate — any tool coming from an "mcp_<server>" plugin must be
        // approved by the user the first time it runs in a session. Runs inside the
        // pending-count lifecycle so the stall watchdog pauses while the user deliberates,
        // like every other approval prompt. Kept ahead of the UI-event emit so a denied
        // call never hits the event bus.
        if (McpApprovalGate != null &&
            !string.IsNullOrEmpty(context.Function.PluginName) &&
            context.Function.PluginName.StartsWith("mcp_", StringComparison.Ordinal))
        {
            var serverName = context.Function.PluginName.Substring("mcp_".Length);

            // Batch-deny: an earlier denial this turn auto-denies subsequent prompts.
            if (_currentScope.Value?.ApprovalsRevoked == true)
            {
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function,
                    $"User denied a previous tool in this batch — auto-denying MCP tool '{context.Function.Name}' from server '{serverName}'. Do not retry unless the user asks.");
                return;
            }

            var approval = await McpApprovalGate
                .RequestAsync(serverName, context.Function.Name, context.Function.Description)
                .WaitAsync(context.CancellationToken);

            if (approval.Response != DiffApprovalResponse.Approved &&
                approval.Response != DiffApprovalResponse.ApprovedNoAskAgain)
            {
                if (approval.Response == DiffApprovalResponse.CancelPlan)
                    _currentScope.Value?.RequestPlanCancellation();
                if (approval.Response == DiffApprovalResponse.Denied)
                    _currentScope.Value?.RevokeRemainingApprovals();

                var denial = approval.Response switch
                {
                    DiffApprovalResponse.Denied =>
                        $"User denied the MCP tool '{context.Function.Name}' from server '{serverName}'. Do not retry unless the user asks.",
                    DiffApprovalResponse.CancelPlan =>
                        $"User cancelled the plan while reviewing MCP tool '{context.Function.Name}'. Stop all further work.",
                    _ =>
                        $"User rejected the MCP tool call and provided new instructions: {approval.UserMessage}"
                };
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, denial);
                return;
            }
        }

        // Emit function call event before invocation
        var functionCall = new FunctionCall
        {
            FunctionName = functionName,
            Description = description,
            Arguments = context.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        OnFunctionInvoked?.Invoke(functionCall);

        // --- Capture pre-execution state for operation display ---
        string? capturedOldContent = null;
        bool capturedIsNewFile = false;
        bool approvalWasShown = false;

        // For write_file or edit_file: capture old content and new-file flag before any approval or execution
        if ((context.Function.Name == "write_file" || context.Function.Name == "edit_file") && !string.IsNullOrEmpty(ProjectRoot))
        {
            context.Arguments.TryGetValue("relativePath", out var pObj);
            var path = pObj?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                var fullPath = ResolveCapturePath(path);
                capturedIsNewFile = !File.Exists(fullPath);
                if (!capturedIsNewFile)
                {
                    try { capturedOldContent = await File.ReadAllTextAsync(fullPath); }
                    catch { /* treat as new file if unreadable */ }
                }
            }
        }

        // For delete_file or delete_folder: capture existing content before approval or execution
        if ((context.Function.Name == "delete_file" || context.Function.Name == "delete_folder") && !string.IsNullOrEmpty(ProjectRoot))
        {
            context.Arguments.TryGetValue("relativePath", out var pObj);
            var path = pObj?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                var fullPath = ResolveCapturePath(path);
                if (File.Exists(fullPath))
                {
                    try { capturedOldContent = await File.ReadAllTextAsync(fullPath); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to read file for diff capture: {ex.Message}"); }
                }
                else if (Directory.Exists(fullPath))
                {
                    // For folders, build a listing of contents as the "old content"
                    try
                    {
                        var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                        var listing = files
                            .Select(f => Path.GetRelativePath(fullPath, f).Replace('\\', '/'))
                            .OrderBy(f => f)
                            .ToList();
                        capturedOldContent = $"Folder: {path}/\nContents ({listing.Count} files):\n" +
                                             string.Join("\n", listing.Select(f => $"  {f}"));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Failed to list folder for diff capture: {ex.Message}"); }
                }
            }
        }

        // Intercept edit_file calls for diff approval — construct the new file content
        // from the find/replace to show a proper diff.
        // Must mirror FileSystemPlugin.EditFile's logic: single-occurrence only, with a
        // CRLF-normalization fallback. string.Replace replaces ALL occurrences, so using
        // it here shows the user a diff that wouldn't match what actually gets written.
        if (context.Function.Name == "edit_file" && OnWriteApprovalRequested != null && capturedOldContent != null)
        {
            context.Arguments.TryGetValue("old_text", out var oldTextObj);
            context.Arguments.TryGetValue("new_text", out var newTextObj);
            context.Arguments.TryGetValue("relativePath", out var editPathObj);
            var oldText = oldTextObj?.ToString() ?? "";
            var newText = newTextObj?.ToString() ?? "";
            var editPath = editPathObj?.ToString() ?? "";

            // Batch-deny: an earlier denial this turn auto-denies subsequent prompts.
            if (_currentScope.Value?.ApprovalsRevoked == true)
            {
                var autoDenyMsg = $"User denied a previous tool in this batch — auto-denying edit to '{editPath}'. Do not retry unless the user asks.";
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, autoDenyMsg);
                OnFunctionCompleted?.Invoke(new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = autoDenyMsg,
                    Success = true
                });
                return;
            }

            // Edit-failure circuit: if the model has already failed N edit attempts on this
            // exact path this turn (without an intervening write), it's in an exploratory
            // loop — every further attempt would just thrash. Refuse with a steer toward
            // tools that can actually unstick it (targeted re-read or full rewrite).
            // The key is normalized so failures under different aliases of the same file
            // ("Games/x.html" vs "src/.../net8.0/Games/x.html") accrue to ONE counter.
            var editKey = NormalizePathKey(editPath);
            var editScope = _currentScope.Value;
            if (editScope != null && editScope.GetEditFailureCount(editKey) >= InvocationScope.EditFailureCircuitThreshold)
            {
                var circuitMsg =
                    $"Edit-failure circuit tripped: {InvocationScope.EditFailureCircuitThreshold} consecutive edit_file " +
                    $"attempts on '{editPath}' have failed this turn. Stop calling edit_file on this file. " +
                    "Either (a) call read_file_contents (with startLine/endLine to reach the section you're " +
                    "editing if the file is large) to refresh your view, or " +
                    "(b) use write_file to replace the whole region you want to change.";
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, circuitMsg);
                OnFunctionCompleted?.Invoke(new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = circuitMsg,
                    Success = false
                });
                return;
            }

            var preview = BuildEditPreview(capturedOldContent, oldText, newText);
            if (preview.Error != null)
            {
                // Record the failure for this path. The composer attaches the content hint
                // on the FIRST failure per path and a short pointer on subsequent ones —
                // prevents the same 5K blob being shipped on every loop iteration.
                editScope?.RecordEditFailure(editKey);
                var msg = ComposeEditFailureMessage(preview.Error, editPath, editKey, capturedOldContent, editScope);
                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, msg);
                OnFunctionCompleted?.Invoke(new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = msg,
                    Success = false
                });
                return;
            }

            var newContent = preview.NewContent!;

            approvalWasShown = true;
            var editApproval = await OnWriteApprovalRequested(editPath, capturedOldContent, newContent)
                .WaitAsync(context.CancellationToken);

            if (editApproval.Response != DiffApprovalResponse.Approved &&
                editApproval.Response != DiffApprovalResponse.ApprovedNoAskAgain)
            {
                if (editApproval.Response == DiffApprovalResponse.CancelPlan)
                    _currentScope.Value?.RequestPlanCancellation();
                if (editApproval.Response == DiffApprovalResponse.Denied)
                    _currentScope.Value?.RevokeRemainingApprovals();

                var resultMsg = editApproval.Response switch
                {
                    DiffApprovalResponse.Denied => $"User denied the edit to '{editPath}'. Do not retry unless the user asks.",
                    DiffApprovalResponse.CancelPlan => $"User cancelled the plan while reviewing the edit to '{editPath}'. Stop all further work.",
                    _ => $"User rejected the edit to '{editPath}' and provided new instructions: {editApproval.UserMessage}"
                };

                context.Result = new Microsoft.SemanticKernel.FunctionResult(context.Function, resultMsg);
                OnFunctionCompleted?.Invoke(new FunctionExecutionResult
                {
                    FunctionName = functionName,
                    Result = resultMsg.Length > 200 ? resultMsg[..200] + "..." : resultMsg,
                    Success = true
                });
                return;
            }
        }

        // Intercept write_file calls for diff approval
        if (context.Function.Name == "write_file" && OnWriteApprovalRequested != null)
        {
            approvalWasShown = true;
            var approvalResult = await HandleWriteApprovalAsync(context, capturedOldContent);
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
                return;
            }
        }

        // Intercept delete_file/delete_folder calls for approval
        if ((context.Function.Name == "delete_file" || context.Function.Name == "delete_folder") && OnDeleteApprovalRequested != null)
        {
            approvalWasShown = true;
            var approvalResult = await HandleDeleteApprovalAsync(context, capturedOldContent);
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
                return;
            }
        }

        // Intercept execute_command calls for approval
        if (context.Function.Name == "execute_command" && OnCommandApprovalRequested != null)
        {
            approvalWasShown = true;
            var approvalResult = await HandleCommandApprovalAsync(context);
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

            // Estimate token costs for content-heavy file operations
            EstimateFileOperationTokens(context.Function.Name, context.Arguments, context.Result?.ToString());

            // Check if result indicates an error from the plugin itself
            var resultStr = context.Result?.ToString() ?? string.Empty;
            var isError = resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);

            // Update scope bookkeeping so later duplicate-read / budget checks see this call.
            UpdateScopeForCompletedCall(context, resultStr, isError);

            // Build operation display for known file operations
            OperationDisplayEvent? operationDisplay = null;
            if (!isError)
            {
                operationDisplay = BuildOperationDisplay(
                    context.Function.Name,
                    context.Arguments,
                    resultStr,
                    capturedOldContent,
                    capturedIsNewFile,
                    approvalWasShown
                );
            }

            var successResult = new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = TruncateResult(resultStr),
                Success = !isError,
                OperationDisplay = operationDisplay
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
    }

    /// <summary>
    /// Builds an OperationDisplayEvent for known file operations.
    /// Returns null for unknown operations.
    /// </summary>
    private OperationDisplayEvent? BuildOperationDisplay(
        string functionName,
        IReadOnlyDictionary<string, object?> arguments,
        string resultStr,
        string? capturedOldContent,
        bool capturedIsNewFile,
        bool approvalWasShown)
    {
        switch (functionName)
        {
            case "write_file":
                return BuildWriteDisplay(arguments, capturedOldContent, capturedIsNewFile, approvalWasShown);

            case "edit_file":
                return BuildEditDisplay(arguments, capturedOldContent, approvalWasShown);

            case "grep_files":
                arguments.TryGetValue("searchText", out var grepSearchText);
                return new OperationDisplayEvent
                {
                    OperationType = "Search",
                    FilePath = grepSearchText?.ToString() ?? ""
                };

            case "read_file_contents":
                return BuildReadDisplay(arguments, resultStr);

            case "delete_file":
                return BuildDeleteDisplay(arguments, capturedOldContent, approvalWasShown);

            case "delete_folder":
                arguments.TryGetValue("relativePath", out var delFolderPath);
                return new OperationDisplayEvent
                {
                    OperationType = "DeleteFolder",
                    FilePath = delFolderPath?.ToString() ?? "",
                    ApprovalWasShown = approvalWasShown
                };

            case "create_folder":
                arguments.TryGetValue("relativePath", out var folderPath);
                return new OperationDisplayEvent
                {
                    OperationType = "CreateFolder",
                    FilePath = folderPath?.ToString() ?? ""
                };

            case "list_all_project_files":
                return new OperationDisplayEvent
                {
                    OperationType = "List",
                    FilePath = ""
                };

            case "list_files_match_glob_pattern":
                arguments.TryGetValue("pattern", out var pattern);
                return new OperationDisplayEvent
                {
                    OperationType = "Glob",
                    FilePath = pattern?.ToString() ?? "*.*"
                };

            case "search_text_in_files":
                arguments.TryGetValue("searchText", out var searchText);
                return new OperationDisplayEvent
                {
                    OperationType = "Search",
                    FilePath = searchText?.ToString() ?? ""
                };

            case "execute_command":
                arguments.TryGetValue("command", out var cmdArg);
                var cmdStr = cmdArg?.ToString() ?? "";
                return new OperationDisplayEvent
                {
                    OperationType = "Command",
                    FilePath = cmdStr,
                    ContentPreview = resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr,
                    ApprovalWasShown = approvalWasShown
                };

            case "search_web":
                arguments.TryGetValue("query", out var webQuery);
                return new OperationDisplayEvent
                {
                    OperationType = "WebSearch",
                    FilePath = webQuery?.ToString() ?? "",
                    ContentPreview = resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr
                };

            case "fetch_webpage":
                arguments.TryGetValue("url", out var webUrl);
                return new OperationDisplayEvent
                {
                    OperationType = "WebFetch",
                    FilePath = webUrl?.ToString() ?? "",
                    ContentPreview = resultStr.Length > 500 ? resultStr[..500] + "..." : resultStr
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Builds display event for write operations (new file = Write, existing file = Update).
    /// </summary>
    private OperationDisplayEvent BuildWriteDisplay(
        IReadOnlyDictionary<string, object?> arguments,
        string? oldContent,
        bool isNewFile,
        bool approvalWasShown)
    {
        arguments.TryGetValue("relativePath", out var pathObj);
        arguments.TryGetValue("content", out var contentObj);
        var filePath = pathObj?.ToString() ?? "";
        var newContent = contentObj?.ToString() ?? "";

        var lines = newContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var lineCount = lines.Length;

        if (isNewFile)
        {
            // New file — show content preview
            var previewCount = Math.Min(MaxPreviewLines, lineCount);
            var preview = string.Join('\n', lines.Take(previewCount));

            return new OperationDisplayEvent
            {
                OperationType = "Write",
                FilePath = filePath,
                LineCount = lineCount,
                IsNewFile = true,
                ContentPreview = preview,
                RemainingLines = Math.Max(0, lineCount - previewCount),
                ApprovalWasShown = approvalWasShown
            };
        }
        else
        {
            // Existing file — compute inline diff
            var diffLines = DiffService.ComputeDiff(oldContent, newContent);
            var additions = diffLines.Count(l => l.LineType == DiffLineType.Added);
            var deletions = diffLines.Count(l => l.LineType == DiffLineType.Removed);
            var collapsedDiff = DiffService.CollapseContext(diffLines, 3);

            return new OperationDisplayEvent
            {
                OperationType = "Update",
                FilePath = filePath,
                LineCount = lineCount,
                IsNewFile = false,
                InlineDiff = collapsedDiff,
                Additions = additions,
                Deletions = deletions,
                ApprovalWasShown = approvalWasShown
            };
        }
    }

    /// <summary>
    /// Canonical key for per-scope path bookkeeping (read dedup, edit-failure counts,
    /// modified-since-read). Routes through the plugin's own path resolution so aliases
    /// of one file ("Games/x.html" vs "src/.../net8.0/Games/x.html") share a single
    /// entry — raw-string keys let a model's alias switch split circuit counts and dodge
    /// trip thresholds, observed live mid-plan. Falls back to the raw string when there's
    /// no project root or the path escapes it (the plugin will reject those calls anyway).
    /// </summary>
    private string NormalizePathKey(string path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ProjectRoot)) return path;
        try { return FileSystemPlugin.ResolvePath(ProjectRoot!, path); }
        catch { return path; }
    }

    /// <summary>
    /// Full path for pre-execution content capture (diffs, new-file detection). Same
    /// resolution as the plugin so aliased paths capture the REAL file's content —
    /// the old naive Combine missed aliased targets entirely, silently skipping the
    /// preview/approval for them. Falls back to the literal combine on failure.
    /// </summary>
    private string ResolveCapturePath(string path)
    {
        try { return FileSystemPlugin.ResolvePath(ProjectRoot!, path); }
        catch { return Path.GetFullPath(Path.Combine(ProjectRoot!, path)); }
    }

    /// <summary>
    /// Builds the per-scope dedup key for a read_file_contents call. Includes the line
    /// range so paging through a large file (startLine=401, then 801, ...) is never
    /// mistaken for a redundant re-read of the same content.
    /// </summary>
    private static string BuildReadKey(string path, IReadOnlyDictionary<string, object?> arguments)
    {
        var start = arguments.TryGetValue("startLine", out var s) ? s?.ToString() ?? "1" : "1";
        var end = arguments.TryGetValue("endLine", out var e) ? e?.ToString() ?? "0" : "0";
        return $"read_file_contents:{path}:{start}-{end}";
    }

    /// <summary>
    /// Builds display event for read operations.
    /// </summary>
    private OperationDisplayEvent BuildReadDisplay(IReadOnlyDictionary<string, object?> arguments, string resultStr)
    {
        arguments.TryGetValue("relativePath", out var pathObj);
        var filePath = pathObj?.ToString() ?? "";

        // Parse line count from the result header: "File: ... (42 lines)" for whole-file
        // reads, "File: ... (lines 401-800 of 1051)" for ranged reads.
        var lineCount = 0;
        var match = Regex.Match(resultStr, @"\((\d+) lines?\)");
        if (match.Success)
        {
            lineCount = int.Parse(match.Groups[1].Value);
        }
        else
        {
            var range = Regex.Match(resultStr, @"\(lines (\d+)-(\d+) of (\d+)\)");
            if (range.Success)
                lineCount = int.Parse(range.Groups[2].Value) - int.Parse(range.Groups[1].Value) + 1;
        }

        return new OperationDisplayEvent
        {
            OperationType = "Read",
            FilePath = filePath,
            LineCount = lineCount
        };
    }

    /// <summary>
    /// Builds display event for edit operations (find/replace).
    /// </summary>
    private OperationDisplayEvent BuildEditDisplay(
        IReadOnlyDictionary<string, object?> arguments,
        string? oldContent,
        bool approvalWasShown)
    {
        arguments.TryGetValue("relativePath", out var pathObj);
        arguments.TryGetValue("old_text", out var oldTextObj);
        arguments.TryGetValue("new_text", out var newTextObj);
        var filePath = pathObj?.ToString() ?? "";
        var oldText = oldTextObj?.ToString() ?? "";
        var newText = newTextObj?.ToString() ?? "";

        var addedLines = newText.Split('\n').Length;
        var removedLines = oldText.Split('\n').Length;

        return new OperationDisplayEvent
        {
            OperationType = "Update",
            FilePath = filePath,
            Additions = addedLines,
            Deletions = removedLines,
            ApprovalWasShown = approvalWasShown
        };
    }

    /// <summary>
    /// Builds display event for delete operations.
    /// </summary>
    private OperationDisplayEvent BuildDeleteDisplay(
        IReadOnlyDictionary<string, object?> arguments,
        string? oldContent,
        bool approvalWasShown)
    {
        arguments.TryGetValue("relativePath", out var pathObj);
        var filePath = pathObj?.ToString() ?? "";

        var lineCount = 0;
        if (!string.IsNullOrEmpty(oldContent))
            lineCount = oldContent.Split('\n').Length;

        return new OperationDisplayEvent
        {
            OperationType = "Delete",
            FilePath = filePath,
            LineCount = lineCount,
            Deletions = lineCount,
            ApprovalWasShown = approvalWasShown
        };
    }

    /// <summary>
    /// Mirrors <c>FileSystemPlugin.EditFile</c>'s replacement semantics for the approval
    /// preview: exact single-occurrence first, then a CRLF-normalized fallback, preserving
    /// the file's original line-ending style. Returns either the computed new content or
    /// a bare human-readable error reason. The error is INTENTIONALLY bare (no content
    /// hint) — attaching the current file content is the call site's job, so it can
    /// dedupe by scope/path. Otherwise an exploratory loop dumps the same 5K content
    /// blob on every failure and amplifies the bloat the hint was meant to prevent.
    /// </summary>
    private static (string? NewContent, string? Error) BuildEditPreview(string fileContent, string oldText, string newText)
    {
        // Exact path.
        var index = fileContent.IndexOf(oldText, StringComparison.Ordinal);
        if (index >= 0)
        {
            var second = fileContent.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal);
            if (second >= 0)
                return (null, "Found multiple occurrences of old_text. Provide a larger, more unique fragment.");

            return (fileContent[..index] + newText + fileContent[(index + oldText.Length)..], null);
        }

        // CRLF fallback — Windows files are CRLF; models typically emit LF-only old_text.
        var nContent = fileContent.Replace("\r\n", "\n").Replace("\r", "\n");
        var nOld = oldText.Replace("\r\n", "\n").Replace("\r", "\n");
        var nIndex = nContent.IndexOf(nOld, StringComparison.Ordinal);
        if (nIndex < 0)
            return (null, "Could not find old_text in the file. It may have been modified since the last read, or the whitespace differs.");

        var nSecond = nContent.IndexOf(nOld, nIndex + nOld.Length, StringComparison.Ordinal);
        if (nSecond >= 0)
            return (null, "Found multiple occurrences of old_text. Provide a larger, more unique fragment.");

        var nNew = newText.Replace("\r\n", "\n").Replace("\r", "\n");
        var normalizedUpdated = nContent[..nIndex] + nNew + nContent[(nIndex + nOld.Length)..];

        // Re-apply original line-ending style so the preview matches what gets written.
        var useCrlf = fileContent.Contains("\r\n");
        return (useCrlf ? normalizedUpdated.Replace("\n", "\r\n") : normalizedUpdated, null);
    }

    /// <summary>
    /// Builds a "here's what's currently in the file" hint to attach to the FIRST
    /// edit failure per (scope, path). Subsequent failures should emit a short
    /// pointer instead — see the dedup gate in the edit_file branch of
    /// OnFunctionInvocationAsync.
    /// Capped at 5000 chars — large enough for most files, small enough to bound bloat.
    /// </summary>
    private static string BuildCurrentContentHint(string fileContent)
    {
        const int cap = 5000;
        var lineCount = fileContent.Count(c => c == '\n') + 1;
        if (fileContent.Length <= cap)
            return $"Current file content ({lineCount} lines):\n{fileContent}";

        return $"Current file content ({lineCount} lines, showing first {cap} chars):\n" +
               fileContent[..cap] +
               "\n... [truncated — use read_file_contents to see the rest]";
    }

    /// <summary>
    /// Composes the full edit_file failure message: the bare reason from
    /// <see cref="BuildEditPreview"/> plus a per-scope-deduped content hint. The first
    /// failure for a path emits the full 5K hint; later failures for the same path get
    /// a one-line pointer back to the original. Cleared when the path is written.
    /// </summary>
    private static string ComposeEditFailureMessage(string bareReason, string editPath, string editKey, string currentContent, InvocationScope? scope)
    {
        var prefix = $"Error: {bareReason} (from '{editPath}')";

        // "Multiple occurrences" failures don't need the content hint — the model already
        // saw the relevant region (its own old_text matched too liberally). Only attach
        // the hint when the failure was "could not find," and only the first time per path.
        // Hint dedup keys on the NORMALIZED path (editKey) so alias switches can't re-earn
        // the full 5K hint; the display string stays as the model spelled it (editPath).
        if (!bareReason.StartsWith("Could not find", StringComparison.OrdinalIgnoreCase))
            return prefix;

        if (scope == null)
            return prefix + "\n" + BuildCurrentContentHint(currentContent);

        if (scope.HasEmittedEditHint(editKey))
            return prefix +
                   $"\nThe current content of '{editPath}' was attached to an earlier failure this turn — " +
                   "re-examine it instead of asking for it again. If you need to see it again, " +
                   "call read_file_contents.";

        scope.MarkEditHintEmitted(editKey);
        return prefix + "\n" + BuildCurrentContentHint(currentContent);
    }

    /// <summary>
    /// After a tool call completes, update the active scope so later circuit-breaker
    /// checks have the info they need: read-set membership, path-modification status,
    /// and cumulative result-size budget.
    /// </summary>
    private void UpdateScopeForCompletedCall(FunctionInvocationContext context, string resultStr, bool isError)
    {
        var scope = _currentScope.Value;
        if (scope == null) return;

        // Charge result chars against the budget — errors count too so a loop of
        // failing calls still trips the circuit.
        if (!string.IsNullOrEmpty(resultStr))
            scope.RecordResultChars(resultStr.Length);

        if (isError)
        {
            // Plugin-level edit failures must count toward the edit-failure circuit too.
            // The preview validates against a snapshot captured at interception; when an
            // earlier edit in the same batch lands in between, the plugin's re-validation
            // fails AFTER the preview passed. Those failures used to bypass the circuit
            // entirely — a model could thrash 9+ consecutive misses on one path without
            // ever tripping it.
            if (context.Function.Name == "edit_file")
            {
                var failPath = context.Arguments.TryGetValue("relativePath", out var fp) ? fp?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(failPath))
                    scope.RecordEditFailure(NormalizePathKey(failPath));
            }
            return; // Don't mark reads/writes for failed calls.
        }

        switch (context.Function.Name)
        {
            case "read_file_contents":
            {
                var path = context.Arguments.TryGetValue("relativePath", out var p) ? p?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(path))
                {
                    var pathKey = NormalizePathKey(path);
                    scope.RecordRead(BuildReadKey(pathKey, context.Arguments), pathKey);
                }
                break;
            }
            case "write_file":
            case "edit_file":
            case "delete_file":
            {
                var path = context.Arguments.TryGetValue("relativePath", out var p) ? p?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(path))
                    scope.RecordWrite(NormalizePathKey(path));
                break;
            }
        }
    }

    /// <summary>
    /// Handles a propose_plan tool call: pulls the goal + step proposals out of
    /// the model's arguments and hands them to <see cref="PlanHandoff"/>. Returns
    /// the summary string that the model will see as the tool result.
    /// </summary>
    private async Task<string> HandleProposePlanAsync(FunctionInvocationContext context)
    {
        if (_planHandoff == null)
            return "Planning is not available in this context.";

        var goal = context.Arguments.TryGetValue("goal", out var goalObj)
            ? goalObj?.ToString() ?? string.Empty
            : string.Empty;

        context.Arguments.TryGetValue("steps", out var stepsObj);
        var proposals = CoerceProposals(stepsObj);

        // Thread the kernel invocation's token through so a running plan is cancellable.
        // Without this the entire multi-step plan executed under CancellationToken.None —
        // Ctrl+C / Esc (which cancel the request token) and the request timeout were all
        // ignored once a plan started, leaving the user with no way out of a stalled step.
        var summary = await _planHandoff.ProcessAsync(goal, proposals, context.CancellationToken);

        // Mark only real proposals (≥1 step) as processed: a malformed/empty proposal
        // stays retryable, but once a genuine plan has been handled — executed, rejected,
        // or cancelled — any further propose_plan this turn is a runaway and the
        // interception above short-circuits it.
        if (proposals.Length > 0)
            _currentScope.Value?.MarkPlanProcessed();

        return summary;
    }

    /// <summary>
    /// Converts the raw steps argument into <see cref="PlanStepProposal"/>[].
    /// SK should already deserialize into the strong type, but local model
    /// connectors sometimes round-trip as JSON text — handle both cases.
    /// </summary>
    private static PlanStepProposal[] CoerceProposals(object? raw)
    {
        if (raw == null)
            return Array.Empty<PlanStepProposal>();

        if (raw is PlanStepProposal[] direct)
            return direct;

        if (raw is IEnumerable<PlanStepProposal> enumerable)
            return enumerable.ToArray();

        try
        {
            var json = raw is string s ? s : JsonSerializer.Serialize(raw);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var parsed = JsonSerializer.Deserialize<PlanStepProposal[]>(json, opts);
            return parsed ?? Array.Empty<PlanStepProposal>();
        }
        catch
        {
            return Array.Empty<PlanStepProposal>();
        }
    }

    /// <summary>
    /// Handles the write approval workflow. Returns a result message to use instead of writing,
    /// or null if the write should proceed normally.
    /// Uses pre-captured old content to avoid double file reads.
    /// </summary>
    private async Task<string?> HandleWriteApprovalAsync(FunctionInvocationContext context, string? oldContent)
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

        // Batch-deny: an earlier denial this turn auto-denies subsequent prompts.
        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying write to '{relativePath}'. Do not retry unless the user asks.";

        // Request approval from the UI (oldContent is pre-captured, null for new files).
        // WaitAsync: never let a wedged prompt block the turn beyond cancellation.
        var approval = await OnWriteApprovalRequested(relativePath, oldContent, newContent)
            .WaitAsync(context.CancellationToken);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null; // Proceed with the write

            case DiffApprovalResponse.Denied:
                _currentScope.Value?.RevokeRemainingApprovals();
                return $"User denied the file write to '{relativePath}'. Do not retry this write unless the user asks.";

            case DiffApprovalResponse.NewInstructions:
                return $"User rejected the file write to '{relativePath}' and provided new instructions: {approval.UserMessage}";

            case DiffApprovalResponse.CancelPlan:
                _currentScope.Value?.RequestPlanCancellation();
                return $"User cancelled the plan while reviewing the write to '{relativePath}'. Stop all further work.";

            default:
                return null;
        }
    }

    /// <summary>
    /// Handles the delete approval workflow. Returns a result message to use instead of deleting,
    /// or null if the delete should proceed normally.
    /// Uses pre-captured existing content to avoid double file reads.
    /// </summary>
    private async Task<string?> HandleDeleteApprovalAsync(FunctionInvocationContext context, string? existingContent)
    {
        if (OnDeleteApprovalRequested == null)
            return null;

        context.Arguments.TryGetValue("relativePath", out var pathObj);
        var relativePath = pathObj?.ToString();

        if (string.IsNullOrEmpty(relativePath))
            return null;

        // Batch-deny: an earlier denial this turn auto-denies subsequent prompts.
        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying deletion of '{relativePath}'. Do not retry unless the user asks.";

        var approval = await OnDeleteApprovalRequested(relativePath, existingContent)
            .WaitAsync(context.CancellationToken);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null; // Proceed with the delete

            case DiffApprovalResponse.Denied:
                _currentScope.Value?.RevokeRemainingApprovals();
                return $"User denied the deletion of '{relativePath}'. Do not retry unless the user asks.";

            case DiffApprovalResponse.NewInstructions:
                return $"User rejected the deletion of '{relativePath}' and provided new instructions: {approval.UserMessage}";

            case DiffApprovalResponse.CancelPlan:
                _currentScope.Value?.RequestPlanCancellation();
                return $"User cancelled the plan while reviewing the deletion of '{relativePath}'. Stop all further work.";

            default:
                return null;
        }
    }

    /// <summary>
    /// Handles the command approval workflow. Returns a result message to use instead of executing,
    /// or null if the command should proceed normally.
    /// </summary>
    private async Task<string?> HandleCommandApprovalAsync(FunctionInvocationContext context)
    {
        if (OnCommandApprovalRequested == null)
            return null;

        context.Arguments.TryGetValue("command", out var cmdObj);
        var command = cmdObj?.ToString();

        if (string.IsNullOrEmpty(command))
            return null;

        // Batch-deny: an earlier denial this turn auto-denies subsequent prompts.
        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying command '{command}'. Do not retry unless the user asks.";

        var approval = await OnCommandApprovalRequested(command)
            .WaitAsync(context.CancellationToken);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null; // Proceed with the command

            case DiffApprovalResponse.Denied:
                _currentScope.Value?.RevokeRemainingApprovals();
                return $"User denied the command '{command}'. Do not retry this command unless the user asks.";

            case DiffApprovalResponse.NewInstructions:
                return $"User rejected the command '{command}' and provided new instructions: {approval.UserMessage}";

            case DiffApprovalResponse.CancelPlan:
                _currentScope.Value?.RequestPlanCancellation();
                return $"User cancelled the plan while reviewing the command '{command}'. Stop all further work.";

            default:
                return null;
        }
    }

    // Verbs that read file content via the shell. Anchored regex with word boundary so
    // `typescript` doesn't match `type`, and `category` doesn't match `cat`. Covers cmd,
    // bash, and PowerShell variants. Used to steer execute_command callers back to
    // read_file_contents — see the shell-read circuit in OnFunctionInvocationAsync.
    private static readonly System.Text.RegularExpressions.Regex ShellFileReadVerbs =
        new(@"^\s*(?:type|cat|head|tail|more|less|nl|gc|Get-Content|findstr|grep|sls|Select-String|sed|awk)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Unwraps a `powershell -Command "..."` / `pwsh -c '...'` / `cmd /c ...` invocation and
    // captures the inner command (group 1/2/3 = double-quoted / single-quoted / unquoted), so a
    // wrapped file dump is re-tested against ShellFileReadVerbs. Without this the anchored ^ check
    // only sees the `powershell`/`cmd` token and waves the read through — the exact hole a stuck
    // model used to dump an 800-line file into context a dozen times via
    // `powershell -Command "Get-Content x | Select-Object -Skip N"`, defeating read_file_contents'
    // cache/dedup/budget circuits and bloating context until the local model stalled.
    private static readonly System.Text.RegularExpressions.Regex ShellReadWrapper =
        new(@"^\s*(?:powershell(?:\.exe)?|pwsh|cmd(?:\.exe)?)\b[^""']*?(?:-Command|-c|/c)\s+(?:""([^""]*)""|'([^']*)'|(\S.*))$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True if the command looks like a shell-based file-read (type/cat/head/findstr/grep/etc.),
    /// whether bare or wrapped in a powershell/pwsh/cmd invocation. Used to short-circuit
    /// execute_command calls that would otherwise dump file content into the conversation,
    /// defeating read_file_contents' cache and dedup circuit.
    /// Public so the classification is unit-testable without instantiating the full filter.
    /// </summary>
    public static bool LooksLikeShellFileRead(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (ShellFileReadVerbs.IsMatch(command)) return true;

        // A wrapped read — `powershell -Command "Get-Content x"`, `cmd /c type x` — only counts
        // if the INNER command leads with a read verb. This keeps legit filters like
        // `git status | grep x` (no wrapper) and `powershell -Command "dotnet build"` allowed.
        var wrapped = ShellReadWrapper.Match(command);
        if (wrapped.Success)
        {
            var inner = wrapped.Groups[1].Success ? wrapped.Groups[1].Value
                      : wrapped.Groups[2].Success ? wrapped.Groups[2].Value
                      : wrapped.Groups[3].Value;
            if (!string.IsNullOrWhiteSpace(inner) && ShellFileReadVerbs.IsMatch(inner))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Determines if a function is a write operation (modifies files/folders).
    /// </summary>
    private bool IsWriteOperation(string? functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return false;

        return functionName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
               functionName.Equals("execute_command", StringComparison.OrdinalIgnoreCase);
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

                case "delete_folder":
                    if (arguments.TryGetValue("relativePath", out var deleteFolderPath))
                        return $"Deleting folder {deleteFolderPath}";
                    return "Deleting folder";

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

                case "edit_file":
                    if (arguments.TryGetValue("relativePath", out var editPath))
                        return $"Editing {editPath}";
                    return "Editing file";

                case "grep_files":
                    if (arguments.TryGetValue("searchText", out var grepText))
                        return $"Searching all files for '{grepText}'";
                    return "Searching all files";

                case "search_text_in_files":
                    if (arguments.TryGetValue("searchText", out var searchText))
                        return $"Searching for '{searchText}'";
                    return "Searching files";

                case "get_absolute_path":
                    if (arguments.TryGetValue("relativePath", out var absPath))
                        return $"Getting absolute path for {absPath}";
                    return "Getting absolute path";

                case "execute_command":
                    if (arguments.TryGetValue("command", out var cmdDesc))
                        return $"Executing: {cmdDesc}";
                    return "Executing command";

                default:
                    return $"{functionName}";
            }
        }

        if (pluginName == "WebSearch")
        {
            switch (functionName)
            {
                case "search_web":
                    if (arguments.TryGetValue("query", out var searchQuery))
                        return $"Searching the web for \"{searchQuery}\"";
                    return "Searching the web";

                case "fetch_webpage":
                    if (arguments.TryGetValue("url", out var fetchUrl))
                        return $"Fetching {fetchUrl}";
                    return "Fetching webpage";

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
    /// Estimates token costs for content-heavy file operations and records them.
    /// </summary>
    private void EstimateFileOperationTokens(string functionName, IReadOnlyDictionary<string, object?> arguments, string? resultStr)
    {
        if (_tokenTracker == null)
            return;

        try
        {
            switch (functionName)
            {
                case "read_file_contents":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 0)
                    {
                        arguments.TryGetValue("relativePath", out var readPath);
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, $"Read {readPath}");
                    }
                    break;

                case "write_file":
                    if (arguments.TryGetValue("content", out var contentObj) && contentObj != null)
                    {
                        var content = contentObj.ToString() ?? "";
                        if (content.Length > 0)
                        {
                            arguments.TryGetValue("relativePath", out var writePath);
                            _tokenTracker.RecordEstimatedUsage(content.Length, $"Write {writePath}");
                        }
                    }
                    break;

                case "search_text_in_files":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                    {
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "Search");
                    }
                    break;

                case "list_all_project_files":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                    {
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "List");
                    }
                    break;

                case "search_web":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                    {
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "WebSearch");
                    }
                    break;

                case "fetch_webpage":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                    {
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "WebFetch");
                    }
                    break;
            }
        }
        catch
        {
            // Token estimation is non-critical
        }
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
            _recentCalls.TryRemove(key, out _);
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
