using MandoCode.Models;
using MandoCode.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

/// <summary>
/// MAF (Microsoft Agent Framework) function-calling-middleware equivalent of the retired
/// SK-side <c>FunctionInvocationFilter</c> (deleted in the migration's final cleanup) — see
/// feat/agent-framework-migration, Phase 3 (memory agent-framework-migration.md).
///
/// Design decision (deliberately NOT what Phase 2's comments anticipated): gating happens
/// INLINE inside this middleware, awaiting the same <c>OnWriteApprovalRequested</c>/etc.
/// callbacks MandoCode's UI already provides — not via <c>ApprovalRequiredAIFunction</c>/
/// <c>ToolApprovalRequestContent</c>. That framework-level pause-and-resume mechanism exists
/// for scenarios where approval needs to cross a process boundary or survive a restart;
/// MandoCode's approval UI is already an in-process awaited callback, so it doesn't need it —
/// and inline gating preserves today's exact per-call behavior (a blocked write doesn't hold
/// back a sibling read in the same turn), avoiding the turn-batching quirk the Phase 0 spike
/// found specifically in <c>ApprovalRequiredAIFunction</c>.
///
/// Ported faithfully: propose_plan interception, all InvocationScope-backed circuit breakers,
/// the time-windowed dedup cache, the MCP approval gate, and the write/edit/delete/command
/// approval flows (including edit-preview construction and the edit-failure circuit) — these
/// are the actual safety-relevant behavior. Deliberately NOT ported: the rich
/// <c>OperationDisplayEvent</c> UI construction (diff line counts, additions/deletions,
/// inline-diff rendering) that the old filter's <c>BuildOperationDisplay</c> and its
/// eight Build*Display helpers produced — that's cosmetic terminal-UI polish with no bearing on
/// gating correctness, and porting sixteen near-identical display branches is better done
/// alongside the actual live cutover (Phase 4/5) than speculatively now while _agent still
/// isn't wired to the live chat path. <see cref="OnFunctionCompleted"/> still fires so the
/// event itself is wired, just with <c>OperationDisplay = null</c> for now.
/// </summary>
public class AgentFunctionMiddleware
{
    public event Action<FunctionCall>? OnFunctionInvoked;
    public event Action<FunctionExecutionResult>? OnFunctionCompleted;
    public event Action? OnFunctionStarted;
    public event Action? OnFunctionFinished;

    public Func<string, string?, string, Task<DiffApprovalResult>>? OnWriteApprovalRequested { get; set; }
    public Func<string, string?, Task<DiffApprovalResult>>? OnDeleteApprovalRequested { get; set; }
    public Func<string, Task<DiffApprovalResult>>? OnCommandApprovalRequested { get; set; }
    public McpApprovalGate? McpApprovalGate { get; set; }

    /// <summary>
    /// Resolves a tool name to its MCP server name, or null if it isn't an MCP tool. MAF has no
    /// plugin-name concept to check a "mcp_" prefix against the way SK's PluginName did, so the
    /// caller supplies this instead. Defaults to "nothing is an MCP tool" until Phase 4/5 wires
    /// MCP tools into _agent's tool list — see AIService._mcpAgentToolsByServer.
    /// </summary>
    public Func<string, string?> McpServerNameResolver { get; set; } = _ => null;

    private readonly ProjectRootAccessor? _projectRootAccessor;
    private string? ProjectRoot => _projectRootAccessor?.ProjectRoot;
    private readonly TokenTrackingService? _tokenTracker;
    private readonly PlanHandoff? _planHandoff;

    private readonly AsyncLocal<InvocationScope?> _currentScope = new();
    private readonly long _defaultResultCharBudget;

    private readonly ConcurrentDictionary<string, (DateTime Time, object? Result)> _recentCalls = new();
    private readonly TimeSpan _readDeduplicationWindow;
    private readonly TimeSpan _writeDeduplicationWindow;

    private int _pendingFunctionCount;
    private readonly object _pendingLock = new();

    public int PendingFunctionCount
    {
        get { lock (_pendingLock) return _pendingFunctionCount; }
    }

    public AgentFunctionMiddleware(
        int defaultDeduplicationWindowSeconds,
        ProjectRootAccessor? projectRootAccessor = null,
        TokenTrackingService? tokenTracker = null,
        PlanHandoff? planHandoff = null,
        long resultCharBudget = 400_000)
    {
        _readDeduplicationWindow = TimeSpan.FromSeconds(2);
        _writeDeduplicationWindow = TimeSpan.FromSeconds(defaultDeduplicationWindowSeconds);
        _projectRootAccessor = projectRootAccessor;
        _tokenTracker = tokenTracker;
        _planHandoff = planHandoff;
        _defaultResultCharBudget = resultCharBudget;
    }

    /// <summary>Same nesting semantics as the retired SK-side FunctionInvocationFilter.BeginScope.</summary>
    public InvocationScope BeginScope()
    {
        var previous = _currentScope.Value;
        var scope = new InvocationScope(_defaultResultCharBudget);
        _currentScope.Value = scope;
        scope.SetOnDispose(() => _currentScope.Value = previous);
        return scope;
    }

    /// <summary>
    /// The function-calling-middleware delegate itself — register via
    /// <c>agent.AsBuilder().Use(middleware.InterceptAsync).Build()</c>. Signature and semantics
    /// per Microsoft Learn's Agent Middleware docs: returning without calling <paramref
    /// name="next"/> short-circuits with the returned value AS the function's result (MEAI's
    /// equivalent of SK's <c>context.Result = ...; return;</c> — no separate result-context
    /// setter needed here).
    /// </summary>
    public async ValueTask<object?> InterceptAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        if (context.Function.Name == "propose_plan" && _planHandoff != null)
        {
            return HandleProposePlan(context);
        }

        var scope = _currentScope.Value;
        if (scope != null)
        {
            if (scope.PlanCancellationRequested)
            {
                return "The user cancelled the plan. All further tool calls are refused. " +
                       "Stop immediately — do not call tools, write files, or continue the work.";
            }

            // A plan is queued for the moment this turn ends, so the model must not also do the
            // work itself — it would race the plan and duplicate it. Mechanical, not a prose
            // request: the refusal string is a courtesy, the refusal is the enforcement.
            //
            // This replaces the old post-plan mutation gate, which had to keep refusing mutations
            // for the REST of the turn because the plan had already run inside the tool call and
            // the model, never having seen the steps execute, would redo the work. With execution
            // deferred there is no post-plan turn to guard, so the window shrinks to "between
            // propose_plan and the end of this reply".
            if (scope.ProposalPending && IsMutatingFunction(context.Function.Name))
            {
                return "A plan is queued and will run as soon as you finish this reply — it will " +
                       "make these changes for you. Do NOT make them yourself. Reply now with one " +
                       "short sentence telling the user their plan is ready to review.";
            }

            if (scope.BudgetExhausted)
            {
                return $"Tool-call budget of {scope.ResultCharBudget:N0} chars is exhausted for this turn. " +
                       "Stop calling tools and respond to the user directly with what you have so far. " +
                       "Ask the user to continue in a new message if more work is needed.";
            }

            if (context.Function.Name == "read_file_contents")
            {
                var path = GetArg(context, "relativePath");
                var pathKey = NormalizePathKey(path);

                if (scope.ReadLoopTripped)
                {
                    return $"You've made {scope.ReadsSinceMutation} reads in a row without writing or editing anything — " +
                           "this is a no-progress loop. STOP reading and act now: write the file or make the edit the task " +
                           "needs, using the content you already have. If you're certain you need more files, you've hit a " +
                           "safety limit — produce your best output from what you've read.";
                }

                var readKey = BuildReadKey(pathKey, context.Arguments);
                var reqStart = TryGetIntArg(context.Arguments, "startLine") ?? 1;
                var reqEnd = TryGetIntArg(context.Arguments, "endLine");
                if (!string.IsNullOrEmpty(path) &&
                    (scope.IsRedundantRead(readKey, pathKey) || scope.IsReadRangeCovered(pathKey, reqStart, reqEnd)))
                {
                    return $"You already read this part of '{path}' this turn and it hasn't changed. " +
                           "Use the content you already have — do NOT re-read lines you've already seen. " +
                           "To read further, pass a startLine past the last line you've read.";
                }
            }

            if (context.Function.Name == "execute_command")
            {
                var cmd = GetArg(context, "command") ?? "";
                if (LooksLikeShellFileRead(cmd))
                {
                    return "Refusing to read file contents via shell. " +
                           "Use read_file_contents instead — it's cached, dedup'd within a turn, and respects the tool-result budget. " +
                           "For large files, pass startLine/endLine to read a specific section (the truncation notice names the line to resume from). " +
                           "Shell-based reads (type/cat/head/tail/more/less/findstr/grep/sed/awk against a file) bloat the conversation history.";
                }

                if (LooksLikeLongRunningCommand(cmd))
                {
                    return "Refusing to start a long-running server or watcher. " +
                           "MandoCode runs each command to completion and cannot keep a process alive across tool calls — " +
                           "this command would just block until it's killed for being idle, so backgrounding it (&, start, > log) won't help either. " +
                           "Do NOT start a server to test your work. Instead, tell the user the exact command and URL to run themselves " +
                           "(e.g. \"run `python -m http.server` in the StarFox folder, then open http://localhost:8000\"). " +
                           "One-shot commands that exit on their own — builds, tests, installs, git, linters — are fine.";
                }
            }

            if (context.Function.Name == "search_web" || context.Function.Name == "fetch_webpage")
            {
                var webKey = BuildWebCallKey(context.Function.Name, context.Arguments);
                if (webKey != null && scope.IsDuplicateWebCall(webKey))
                {
                    var what = context.Function.Name == "search_web" ? "search" : "page fetch";
                    return $"You already ran this exact web {what} earlier this turn — the results are " +
                           "in the conversation above. Use them instead of repeating the call. If you need " +
                           "different information, change the query or URL; otherwise continue with the task.";
                }
            }
        }

        var functionName = context.Function.Name;
        var description = GetFunctionDescription(functionName, context.Arguments);
        var isWriteOperation = IsWriteOperation(functionName);
        var deduplicationWindow = isWriteOperation ? _writeDeduplicationWindow : _readDeduplicationWindow;
        var callKey = CreateCallKey(functionName, context.Arguments, isWriteOperation);

        if (_recentCalls.TryGetValue(callKey, out var cached) &&
            DateTime.UtcNow - cached.Time < deduplicationWindow)
        {
            return cached.Result ?? "Operation already completed.";
        }

        lock (_pendingLock) _pendingFunctionCount++;
        OnFunctionStarted?.Invoke();

        try
        {
            return await InvokeCoreAsync(agent, context, next, cancellationToken, functionName, description, callKey);
        }
        finally
        {
            lock (_pendingLock) _pendingFunctionCount--;
            OnFunctionFinished?.Invoke();
        }
    }

    private async ValueTask<object?> InvokeCoreAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken,
        string functionName,
        string description,
        string callKey)
    {
        var mcpServerName = McpServerNameResolver(context.Function.Name);
        if (McpApprovalGate != null && mcpServerName != null)
        {
            if (_currentScope.Value?.ApprovalsRevoked == true)
            {
                return $"User denied a previous tool in this batch — auto-denying MCP tool '{context.Function.Name}' from server '{mcpServerName}'. Do not retry unless the user asks.";
            }

            var approval = await McpApprovalGate
                .RequestAsync(mcpServerName, context.Function.Name, context.Function.Description)
                .WaitAsync(cancellationToken);

            if (approval.Response != DiffApprovalResponse.Approved &&
                approval.Response != DiffApprovalResponse.ApprovedNoAskAgain)
            {
                if (approval.Response == DiffApprovalResponse.CancelPlan)
                    _currentScope.Value?.RequestPlanCancellation();
                if (approval.Response == DiffApprovalResponse.Denied)
                    _currentScope.Value?.RevokeRemainingApprovals();

                return approval.Response switch
                {
                    DiffApprovalResponse.Denied =>
                        $"User denied the MCP tool '{context.Function.Name}' from server '{mcpServerName}'. Do not retry unless the user asks.",
                    DiffApprovalResponse.CancelPlan =>
                        $"User cancelled the plan while reviewing MCP tool '{context.Function.Name}'. Stop all further work.",
                    _ =>
                        $"User rejected the MCP tool call and provided new instructions: {approval.UserMessage}"
                };
            }
        }

        OnFunctionInvoked?.Invoke(new FunctionCall
        {
            FunctionName = functionName,
            Description = description,
            Arguments = ToArgDictionary(context.Arguments)
        });

        string? capturedOldContent = null;

        if ((functionName == "write_file" || functionName == "edit_file") && !string.IsNullOrEmpty(ProjectRoot))
        {
            var path = GetArg(context, "relativePath");
            if (!string.IsNullOrEmpty(path))
            {
                var fullPath = ResolveCapturePath(path);
                if (File.Exists(fullPath))
                {
                    try { capturedOldContent = await File.ReadAllTextAsync(fullPath); }
                    catch { /* treat as new file if unreadable */ }
                }
            }
        }

        if ((functionName == "delete_file" || functionName == "delete_folder") && !string.IsNullOrEmpty(ProjectRoot))
        {
            var path = GetArg(context, "relativePath");
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

        if (functionName == "edit_file" && OnWriteApprovalRequested != null && capturedOldContent != null)
        {
            var oldText = GetArg(context, "old_text") ?? "";
            var newText = GetArg(context, "new_text") ?? "";
            var editPath = GetArg(context, "relativePath") ?? "";

            if (_currentScope.Value?.ApprovalsRevoked == true)
            {
                var autoDenyMsg = $"User denied a previous tool in this batch — auto-denying edit to '{editPath}'. Do not retry unless the user asks.";
                CompleteWith(functionName, autoDenyMsg, success: true);
                return autoDenyMsg;
            }

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
                CompleteWith(functionName, circuitMsg, success: false);
                return circuitMsg;
            }

            var preview = BuildEditPreview(capturedOldContent, oldText, newText);
            if (preview.Error != null)
            {
                editScope?.RecordEditFailure(editKey);
                var msg = ComposeEditFailureMessage(preview.Error, editPath, editKey, capturedOldContent, editScope);
                CompleteWith(functionName, msg, success: false);
                return msg;
            }

            var newContent = preview.NewContent!;
            var editApproval = await OnWriteApprovalRequested(editPath, capturedOldContent, newContent)
                .WaitAsync(cancellationToken);

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
                CompleteWith(functionName, resultMsg, success: true);
                return resultMsg;
            }
        }

        if (functionName == "write_file" && OnWriteApprovalRequested != null)
        {
            var approvalResult = await HandleWriteApprovalAsync(context, capturedOldContent, cancellationToken);
            if (approvalResult != null)
            {
                CompleteWith(functionName, approvalResult, success: true);
                return approvalResult;
            }
        }

        if ((functionName == "delete_file" || functionName == "delete_folder") && OnDeleteApprovalRequested != null)
        {
            var approvalResult = await HandleDeleteApprovalAsync(context, capturedOldContent, cancellationToken);
            if (approvalResult != null)
            {
                CompleteWith(functionName, approvalResult, success: true);
                return approvalResult;
            }
        }

        if (functionName == "execute_command" && OnCommandApprovalRequested != null)
        {
            var approvalResult = await HandleCommandApprovalAsync(context, cancellationToken);
            if (approvalResult != null)
            {
                CompleteWith(functionName, approvalResult, success: true);
                return approvalResult;
            }
        }

        try
        {
            var result = await next(context, cancellationToken);
            var resultStr = result switch
            {
                null => string.Empty,
                string s => s,
                _ => result.ToString() ?? string.Empty
            };

            // The budget used to be checked only before the NEXT tool call. A single enormous
            // result (most visibly a recursive file listing from a broad project root) could
            // therefore enter chat history whole and overflow the provider before the circuit
            // had another chance to run. Bound the result before it is cached, counted, or
            // returned to MAF so oversized payloads never reach the model.
            var deliveredResult = result;
            var activeScope = _currentScope.Value;
            if (activeScope != null && resultStr.Length > activeScope.RemainingResultChars)
            {
                resultStr = TruncateToRemainingBudget(resultStr, activeScope.RemainingResultChars);
                deliveredResult = resultStr;
            }

            _recentCalls[callKey] = (DateTime.UtcNow, deliveredResult);
            CleanupOldEntries();

            EstimateFileOperationTokens(functionName, context.Arguments, resultStr);

            var isError = resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
            UpdateScopeForCompletedCall(context, functionName, resultStr, isError);

            CompleteWith(functionName, resultStr, success: !isError);
            return deliveredResult;
        }
        catch (Exception ex)
        {
            var errorMsg = $"Function failed: {ex.Message}";
            CompleteWith(functionName, $"Error: {ex.Message}", success: false);
            return errorMsg;
        }
    }

    private static string TruncateToRemainingBudget(string result, long remainingChars)
    {
        if (remainingChars <= 0) return string.Empty;
        if (result.Length <= remainingChars) return result;

        var limit = (int)Math.Min(int.MaxValue, remainingChars);
        const string marker = "\n... [tool result truncated before delivery because this turn's tool-result budget was reached]";
        if (limit <= marker.Length)
            return marker[..limit];

        return result[..(limit - marker.Length)] + marker;
    }

    private void CompleteWith(string functionName, string result, bool success)
    {
        OnFunctionCompleted?.Invoke(new FunctionExecutionResult
        {
            FunctionName = functionName,
            Result = TruncateResult(result),
            Success = success
            // OperationDisplay intentionally omitted — see class doc comment.
        });
    }

    private static Dictionary<string, object?> ToArgDictionary(AIFunctionArguments arguments)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in arguments) dict[kvp.Key] = kvp.Value;
        return dict;
    }

    private static string? GetArg(FunctionInvocationContext context, string name) =>
        context.Arguments.TryGetValue(name, out var v) ? v?.ToString() : null;

    private async Task<string?> HandleWriteApprovalAsync(FunctionInvocationContext context, string? oldContent, CancellationToken cancellationToken)
    {
        if (OnWriteApprovalRequested == null) return null;

        var relativePath = GetArg(context, "relativePath");
        var newContent = GetArg(context, "content");
        if (string.IsNullOrEmpty(relativePath) || newContent == null) return null;

        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying write to '{relativePath}'. Do not retry unless the user asks.";

        var approval = await OnWriteApprovalRequested(relativePath, oldContent, newContent).WaitAsync(cancellationToken);

        return approval.Response switch
        {
            DiffApprovalResponse.Approved or DiffApprovalResponse.ApprovedNoAskAgain => null,
            DiffApprovalResponse.Denied => DenyWrite(relativePath),
            DiffApprovalResponse.NewInstructions => $"User rejected the file write to '{relativePath}' and provided new instructions: {approval.UserMessage}",
            DiffApprovalResponse.CancelPlan => CancelPlanFromWrite(relativePath),
            _ => null
        };

        string DenyWrite(string path)
        {
            _currentScope.Value?.RevokeRemainingApprovals();
            return $"User denied the file write to '{path}'. Do not retry this write unless the user asks.";
        }

        string CancelPlanFromWrite(string path)
        {
            _currentScope.Value?.RequestPlanCancellation();
            return $"User cancelled the plan while reviewing the write to '{path}'. Stop all further work.";
        }
    }

    private async Task<string?> HandleDeleteApprovalAsync(FunctionInvocationContext context, string? existingContent, CancellationToken cancellationToken)
    {
        if (OnDeleteApprovalRequested == null) return null;

        var relativePath = GetArg(context, "relativePath");
        if (string.IsNullOrEmpty(relativePath)) return null;

        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying deletion of '{relativePath}'. Do not retry unless the user asks.";

        var approval = await OnDeleteApprovalRequested(relativePath, existingContent).WaitAsync(cancellationToken);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null;
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

    private async Task<string?> HandleCommandApprovalAsync(FunctionInvocationContext context, CancellationToken cancellationToken)
    {
        if (OnCommandApprovalRequested == null) return null;

        var command = GetArg(context, "command");
        if (string.IsNullOrEmpty(command)) return null;

        if (_currentScope.Value?.ApprovalsRevoked == true)
            return $"User denied a previous tool in this batch — auto-denying command '{command}'. Do not retry unless the user asks.";

        var approval = await OnCommandApprovalRequested(command).WaitAsync(cancellationToken);

        switch (approval.Response)
        {
            case DiffApprovalResponse.Approved:
            case DiffApprovalResponse.ApprovedNoAskAgain:
                return null;
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

    /// <summary>
    /// Records the proposal and returns immediately. The plan itself runs after this turn unwinds
    /// (see <see cref="PlanHandoff.RunPendingPlanAsync"/>).
    /// </summary>
    /// <remarks>
    /// This used to await the entire plan — approval, every step, every nested tool call and diff
    /// prompt — inside this one tool call, and almost every oddity in the planner descended from
    /// that: the outer stall watchdog had to be paused or it killed the plan, the prompt gate had
    /// to be released early or step 1 deadlocked, and because the outer model's turn was still open
    /// it treated the returned summary as "not started yet" and redid the work.
    /// </remarks>
    private string HandleProposePlan(FunctionInvocationContext context)
    {
        if (_planHandoff == null)
            return "Planning is not available in this context.";

        // A step's own model call can reach propose_plan. Nested planning is always a runaway.
        if (_planHandoff.IsExecuting)
            return "A plan is already executing. Continue the current step instead of proposing a new plan.";

        var goal = GetArg(context, "goal") ?? string.Empty;
        context.Arguments.TryGetValue("steps", out var stepsObj);
        var proposals = CoerceProposals(stepsObj);

        // Malformed or empty args are common from local models; fall through to direct work rather
        // than queueing a plan of empty steps.
        if (proposals.Length == 0)
            return "Proposed plan had no steps. Proceed without a plan.";

        _planHandoff.SetPendingProposal(goal, proposals);
        _currentScope.Value?.MarkProposalPending();

        return $"Plan received with {proposals.Length} step(s). It will be shown to the user for "
             + "approval as soon as you finish this reply, and the approved steps will be executed "
             + "for you. Do NOT start the work yourself and do NOT call propose_plan again. Reply "
             + "now with one short sentence telling the user their plan is ready to review.";
    }

    private static PlanStepProposal[] CoerceProposals(object? raw)
    {
        if (raw == null) return [];
        if (raw is PlanStepProposal[] direct) return direct;
        if (raw is IEnumerable<PlanStepProposal> enumerable) return enumerable.ToArray();

        try
        {
            var json = raw is string s ? s : JsonSerializer.Serialize(raw);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<PlanStepProposal[]>(json, opts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void UpdateScopeForCompletedCall(FunctionInvocationContext context, string functionName, string resultStr, bool isError)
    {
        var scope = _currentScope.Value;
        if (scope == null) return;

        if (!string.IsNullOrEmpty(resultStr))
            scope.RecordResultChars(resultStr.Length);

        if (isError)
        {
            if (functionName == "edit_file")
            {
                var failPath = GetArg(context, "relativePath") ?? "";
                if (!string.IsNullOrEmpty(failPath))
                    scope.RecordEditFailure(NormalizePathKey(failPath));
            }
            return;
        }

        switch (functionName)
        {
            case "read_file_contents":
            {
                var path = GetArg(context, "relativePath") ?? "";
                if (!string.IsNullOrEmpty(path))
                {
                    var pathKey = NormalizePathKey(path);
                    scope.RecordRead(BuildReadKey(pathKey, context.Arguments), pathKey);
                    if (TryParseDeliveredRange(resultStr, out var ds, out var de, out var dt))
                        scope.RecordReadRange(pathKey, ds, de, dt);
                }
                break;
            }
            case "write_file":
            case "edit_file":
            case "delete_file":
            {
                var path = GetArg(context, "relativePath") ?? "";
                if (!string.IsNullOrEmpty(path))
                {
                    scope.RecordWrite(NormalizePathKey(path));
                    _planHandoff?.RecordFileOperation(functionName, path);
                }
                break;
            }
            case "create_folder":
            case "delete_folder":
            {
                var path = GetArg(context, "relativePath") ?? "";
                if (!string.IsNullOrEmpty(path))
                    _planHandoff?.RecordFileOperation(functionName, path);
                break;
            }
            case "search_web":
            case "fetch_webpage":
            {
                var webKey = BuildWebCallKey(functionName, context.Arguments);
                if (webKey != null) scope.RecordWebCall(webKey);
                break;
            }
        }
    }

    private string NormalizePathKey(string? path)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ProjectRoot)) return path ?? "";
        try { return FileSystemPlugin.ResolvePath(ProjectRoot!, path); }
        catch { return path; }
    }

    private string ResolveCapturePath(string path)
    {
        try { return FileSystemPlugin.ResolvePath(ProjectRoot!, path); }
        catch { return Path.GetFullPath(Path.Combine(ProjectRoot!, path)); }
    }

    private static string BuildReadKey(string path, AIFunctionArguments arguments)
    {
        var start = arguments.TryGetValue("startLine", out var s) ? s?.ToString() ?? "1" : "1";
        var end = arguments.TryGetValue("endLine", out var e) ? e?.ToString() ?? "0" : "0";
        return $"read_file_contents:{path}:{start}-{end}";
    }

    private static int? TryGetIntArg(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is null) return null;
        if (raw is int i) return i;
        if (raw is long l) return (int)l;
        return int.TryParse(raw.ToString(), out var parsed) ? parsed : null;
    }

    private static bool TryParseDeliveredRange(string resultStr, out int start, out int end, out int total)
    {
        start = end = total = 0;
        if (string.IsNullOrEmpty(resultStr)) return false;

        var ranged = Regex.Match(resultStr, @"\(lines (\d+)-(\d+) of (\d+)\)");
        if (ranged.Success)
        {
            start = int.Parse(ranged.Groups[1].Value);
            end = int.Parse(ranged.Groups[2].Value);
            total = int.Parse(ranged.Groups[3].Value);
            return true;
        }

        var whole = Regex.Match(resultStr, @"\((\d+) lines?\)");
        if (whole.Success)
        {
            start = 1;
            end = total = int.Parse(whole.Groups[1].Value);
            return true;
        }
        return false;
    }

    internal static string? BuildWebCallKey(string functionName, AIFunctionArguments arguments)
    {
        if (functionName == "search_web")
        {
            var query = arguments.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(query)) return null;
            var max = arguments.TryGetValue("maxResults", out var m) ? m?.ToString() ?? "5" : "5";
            return $"search_web:{NormalizeWebArg(query)}:{max}";
        }

        if (functionName == "fetch_webpage")
        {
            var url = arguments.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(url)) return null;
            var max = arguments.TryGetValue("maxCharacters", out var m) ? m?.ToString() ?? "5000" : "5000";
            return $"fetch_webpage:{NormalizeWebArg(url)}:{max}";
        }

        return null;
    }

    private static string NormalizeWebArg(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static (string? NewContent, string? Error) BuildEditPreview(string fileContent, string oldText, string newText)
    {
        var index = fileContent.IndexOf(oldText, StringComparison.Ordinal);
        if (index >= 0)
        {
            var second = fileContent.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal);
            if (second >= 0)
                return (null, "Found multiple occurrences of old_text. Provide a larger, more unique fragment.");

            return (fileContent[..index] + newText + fileContent[(index + oldText.Length)..], null);
        }

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

        var useCrlf = fileContent.Contains("\r\n");
        return (useCrlf ? normalizedUpdated.Replace("\n", "\r\n") : normalizedUpdated, null);
    }

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

    private static string ComposeEditFailureMessage(string bareReason, string editPath, string editKey, string currentContent, InvocationScope? scope)
    {
        var prefix = $"Error: {bareReason} (from '{editPath}')";

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

    private static bool IsWriteOperation(string? functionName)
    {
        if (string.IsNullOrEmpty(functionName)) return false;
        return functionName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
               functionName.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
               functionName.Equals("execute_command", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMutatingFunction(string? functionName) =>
        functionName is "write_file" or "edit_file" or "delete_file" or "delete_folder" or "create_folder";

    private static readonly Regex ShellFileReadVerbs =
        new(@"^\s*(?:type|cat|head|tail|more|less|nl|gc|Get-Content|findstr|grep|sls|Select-String|sed|awk)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShellReadWrapper =
        new(@"^\s*(?:powershell(?:\.exe)?|pwsh|cmd(?:\.exe)?)\b[^""']*?(?:-Command|-c|/c)\s+(?:""([^""]*)""|'([^']*)'|(\S.*))$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LongRunningServerCommand =
        new(@"python[0-9.]*\s+-m\s+http\.server" +
            @"|\bmanage\.py\s+runserver\b" +
            @"|\bhttp-server\b|\blive-server\b" +
            @"|\b(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?(?:dev|serve|start|watch)\b" +
            @"|\bnpx\s+serve\b" +
            @"|\bvite\b(?!\s+build)" +
            @"|\b(?:next|nuxt|astro|remix)\s+dev\b" +
            @"|\bng\s+serve\b" +
            @"|\bflask\s+run\b|\bphp\s+-S\b" +
            @"|\bdotnet\s+watch\b" +
            @"|\bwebpack(?:-dev-server)?\s+serve\b|\bwebpack-dev-server\b" +
            @"|\brails\s+(?:server|s)\b|\bjekyll\s+serve\b|\bhugo\s+server\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeLongRunningCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (LongRunningServerCommand.IsMatch(command)) return true;

        var wrapped = ShellReadWrapper.Match(command);
        if (wrapped.Success)
        {
            var inner = wrapped.Groups[1].Success ? wrapped.Groups[1].Value
                      : wrapped.Groups[2].Success ? wrapped.Groups[2].Value
                      : wrapped.Groups[3].Value;
            if (!string.IsNullOrWhiteSpace(inner) && LongRunningServerCommand.IsMatch(inner))
                return true;
        }
        return false;
    }

    public static bool LooksLikeShellFileRead(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (ShellFileReadVerbs.IsMatch(command)) return true;

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

    private static string GetFunctionDescription(string functionName, AIFunctionArguments arguments)
    {
        switch (functionName)
        {
            case "create_folder":
                return arguments.TryGetValue("relativePath", out var folderPath) ? $"Creating folder {folderPath}" : "Creating folder";
            case "write_file":
                return arguments.TryGetValue("relativePath", out var writePath) ? $"Writing to {writePath}" : "Writing file";
            case "delete_file":
                return arguments.TryGetValue("relativePath", out var deletePath) ? $"Deleting {deletePath}" : "Deleting file";
            case "delete_folder":
                return arguments.TryGetValue("relativePath", out var deleteFolderPath) ? $"Deleting folder {deleteFolderPath}" : "Deleting folder";
            case "read_file_contents":
                return arguments.TryGetValue("relativePath", out var readPath) ? $"Reading {readPath}" : "Reading file";
            case "list_all_project_files":
                return arguments.TryGetValue("relativeDirectory", out var directory) &&
                       !string.IsNullOrWhiteSpace(directory?.ToString())
                    ? $"Listing files under {directory}"
                    : "Listing all project files";
            case "list_files_match_glob_pattern":
                return arguments.TryGetValue("pattern", out var pattern) ? $"Finding files matching '{pattern}'" : "Listing files";
            case "edit_file":
                return arguments.TryGetValue("relativePath", out var editPath) ? $"Editing {editPath}" : "Editing file";
            case "grep_files":
                return arguments.TryGetValue("searchText", out var grepText) ? $"Searching all files for '{grepText}'" : "Searching all files";
            case "search_text_in_files":
                return arguments.TryGetValue("searchText", out var searchText) ? $"Searching for '{searchText}'" : "Searching files";
            case "get_absolute_path":
                return arguments.TryGetValue("relativePath", out var absPath) ? $"Getting absolute path for {absPath}" : "Getting absolute path";
            case "execute_command":
                return arguments.TryGetValue("command", out var cmdDesc) ? $"Executing: {cmdDesc}" : "Executing command";
            case "search_web":
                return arguments.TryGetValue("query", out var searchQuery) ? $"Searching the web for \"{searchQuery}\"" : "Searching the web";
            case "fetch_webpage":
                return arguments.TryGetValue("url", out var fetchUrl) ? $"Fetching {fetchUrl}" : "Fetching webpage";
            default:
                return functionName;
        }
    }

    private static string TruncateResult(string result, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(result)) return "[empty]";
        return result.Length <= maxLength ? result : result[..maxLength] + "... [truncated]";
    }

    private static string CreateCallKey(string functionName, AIFunctionArguments arguments, bool isWriteOperation)
    {
        if (isWriteOperation && functionName.Contains("write_file"))
        {
            var keyBuilder = new StringBuilder(functionName);
            if (arguments.TryGetValue("relativePath", out var path))
                keyBuilder.Append(':').Append(path);
            if (arguments.TryGetValue("content", out var content) && content != null)
                keyBuilder.Append(':').Append(ComputeContentHash(content.ToString() ?? ""));
            return keyBuilder.ToString();
        }

        var argsJson = JsonSerializer.Serialize(
            arguments.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value?.ToString() ?? ""));
        return $"{functionName}:{argsJson}";
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes.AsSpan(0, 8));
    }

    private void EstimateFileOperationTokens(string functionName, AIFunctionArguments arguments, string? resultStr)
    {
        if (_tokenTracker == null) return;

        try
        {
            switch (functionName)
            {
                case "read_file_contents":
                    if (!string.IsNullOrEmpty(resultStr))
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
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "Search");
                    break;
                case "list_all_project_files":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "List");
                    break;
                case "search_web":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "WebSearch");
                    break;
                case "fetch_webpage":
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > 100)
                        _tokenTracker.RecordEstimatedUsage(resultStr.Length, "WebFetch");
                    break;
            }
        }
        catch
        {
            // Token estimation is non-critical
        }
    }

    private void CleanupOldEntries()
    {
        var cutoff = DateTime.UtcNow - _writeDeduplicationWindow;
        var keysToRemove = _recentCalls
            .Where(kvp => kvp.Value.Time < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
            _recentCalls.TryRemove(key, out _);
    }

    public void ClearCache() => _recentCalls.Clear();
}
