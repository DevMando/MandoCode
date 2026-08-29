using System.Text;
using MandoCode.Models;
using MandoCode.Plugins;

namespace MandoCode.Services;

/// <summary>
/// Thrown by <see cref="AIService.ExecutePlanStepAsync"/> when the user chose "Cancel plan"
/// from a diff-approval prompt mid-step. <see cref="TaskPlannerService.ExecutePlanAsync"/>
/// catches this and terminates the plan cleanly.
/// </summary>
public sealed class PlanCancellationRequestedException : Exception
{
    public PlanCancellationRequestedException()
        : base("User cancelled the plan from a diff-approval prompt.") { }
}

/// <summary>
/// Bridge between AgentFunctionMiddleware (where propose_plan is intercepted)
/// and App.razor (which drives the approval UI and plan execution).
///
/// The middleware calls <see cref="ProcessAsync"/> and awaits the summary string.
/// The UI subscribes to <see cref="OnPlanRequested"/>, handles approval, runs
/// <c>ExecutePlanAsync</c>, and returns a recap that the model sees as the tool result.
/// </summary>
public class PlanHandoff
{
    private readonly object _lock = new();
    private bool _isExecuting;

    /// <summary>
    /// True while a plan is being approved/executed. Read-only view so UI components
    /// (like <see cref="DiffApprovalHandler"/>) can conditionally offer "Cancel plan" —
    /// outside of a plan the option has no meaning.
    /// </summary>
    public bool IsExecuting
    {
        get { lock (_lock) return _isExecuting; }
    }

    /// <summary>
    /// UI callback. Receives the proposed plan, returns a summary string that the
    /// model will see as the tool result once the user has approved/rejected and
    /// any execution has finished.
    /// </summary>
    public Func<TaskPlan, CancellationToken, Task<string>>? OnPlanRequested { get; set; }

    /// <summary>
    /// Raised immediately before plan approval + execution begins, and again when it ends
    /// (success, rejection, or throw).
    /// </summary>
    /// <remarks>
    /// AIService used to subscribe to these to suspend the outer stall watchdog and the
    /// request-timeout ceiling, because the whole plan ran inside the propose_plan tool call and a
    /// slow step would otherwise trip a timer and surface as a bogus "Cancelled by user." Plans now
    /// run after the turn unwinds, so no timer is running to suspend and that subscription is gone.
    /// The events remain as a UI extension point — they're multicast, unlike
    /// <see cref="OnPlanRequested"/>, so a host can observe plan activity without owning it.
    /// </remarks>
    public event Action? ExecutionStarted;
    public event Action? ExecutionFinished;

    // File operations recorded by AgentFunctionMiddleware while a plan executes.
    // These are EVIDENCE from the choke point (the call actually ran and succeeded),
    // not model self-reports — they feed the manifest the outer model receives.
    private readonly List<(string Operation, string Path)> _fileOperations = new();

    /// <summary>
    /// True when the most recent <see cref="ProcessAsync"/> actually executed work
    /// (at least one step completed). The middleware reads this to arm the post-plan
    /// mutation gate — a rejected or never-started plan leaves it false, because the
    /// model is then expected to do the work directly.
    /// </summary>
    public bool LastPlanExecutedWork { get; private set; }

    /// <summary>
    /// Files written, edited or deleted so far by the plan currently executing, oldest first.
    /// </summary>
    /// <remarks>
    /// Evidence recorded at the middleware choke point — the call actually ran and succeeded — not
    /// the model's self-report. Captured into the workflow's durable state so a resumed run knows
    /// what already exists on disk; without it, resuming would have no way to distinguish work that
    /// completed from work that never started.
    /// </remarks>
    public IReadOnlyList<(string Operation, string Path)> FileOperations
    {
        get { lock (_lock) return [.. _fileOperations]; }
    }

    /// <summary>
    /// Called by AgentFunctionMiddleware after a successful filesystem-mutating call.
    /// No-ops outside plan execution so ordinary chat-turn writes don't pollute the
    /// next plan's manifest.
    /// </summary>
    public void RecordFileOperation(string operation, string relativePath)
    {
        lock (_lock)
        {
            if (!_isExecuting) return;
            _fileOperations.Add((operation, relativePath));
        }
    }

    // Single-slot holder for a plan the model proposed during the current turn. The plan is NOT
    // run here: propose_plan returns a receipt immediately and the host runs the plan after the
    // turn unwinds (see RunPendingPlanAsync). Last write wins — a model that proposes twice in one
    // turn simply replaces its own proposal, which is strictly better than the old behavior of
    // refusing the second one with a prose directive it could ignore anyway.
    private (string Goal, string? OriginalRequest, PlanStepProposal[] Steps)? _pendingProposal;
    private string? _currentRequest;

    /// <summary>True when the model proposed a plan this turn that hasn't been run yet.</summary>
    public bool HasPendingProposal
    {
        get { lock (_lock) return _pendingProposal != null; }
    }

    /// <summary>
    /// Supplies the request that opened the current model turn. A later proposal captures this
    /// value so checkpoints retain the request's authoritative paths rather than only the model's
    /// shortened goal.
    /// </summary>
    public void SetRequestContext(string? request)
    {
        lock (_lock)
        {
            _currentRequest = string.IsNullOrWhiteSpace(request) ? null : request;
            if (_pendingProposal is { } pending)
                _pendingProposal = (pending.Goal, _currentRequest, pending.Steps);
        }
    }

    /// <summary>Records a proposal for the host to run once the current turn finishes.</summary>
    public void SetPendingProposal(string goal, PlanStepProposal[] steps)
    {
        lock (_lock) _pendingProposal = (goal, _currentRequest, steps);
    }

    /// <summary>Drops any pending proposal — used when a turn ends without running one.</summary>
    public void ClearPendingProposal()
    {
        lock (_lock) _pendingProposal = null;
    }

    /// <summary>
    /// Runs the proposal recorded during the turn that just ended, if there is one, and returns the
    /// manifest the caller should place into chat history. Returns <c>null</c> when no plan was
    /// proposed.
    /// </summary>
    /// <remarks>
    /// This is the entry point hosts call after their chat turn has fully drained. It exists so the
    /// plan is a <i>peer</i> of the chat turn rather than a child of a tool call — the change that
    /// removes the need for the outer watchdog pause, the prompt-gate release dance, and the
    /// post-plan mutation gate.
    /// <para>
    /// Hosts that previously relied on the plan running inside <c>propose_plan</c> must call this;
    /// without it a proposed plan is simply never executed.
    /// </para>
    /// </remarks>
    public async Task<string?> RunPendingPlanAsync(CancellationToken ct = default)
    {
        (string Goal, string? OriginalRequest, PlanStepProposal[] Steps)? pending;
        lock (_lock)
        {
            pending = _pendingProposal;
            _pendingProposal = null;
        }

        if (pending == null) return null;

        return await ProcessAsync(
            pending.Value.Goal,
            pending.Value.Steps,
            ct,
            pending.Value.OriginalRequest);
    }

    /// <summary>
    /// Runs an approved plan end to end and returns the manifest describing what happened.
    /// Guards against recursive planning (a plan step proposing another plan) by returning a
    /// short-circuit message.
    /// </summary>
    public async Task<string> ProcessAsync(
        string goal,
        PlanStepProposal[] proposals,
        CancellationToken ct = default,
        string? originalRequest = null)
    {
        lock (_lock)
        {
            if (_isExecuting)
                return "A plan is already executing. Continue the current step instead of proposing a new plan.";
            _isExecuting = true;
            _fileOperations.Clear();
            LastPlanExecutedWork = false;
        }

        try
        {
            if (OnPlanRequested == null)
                return "Planning UI is not wired up. Proceeding without a plan.";

            var steps = TaskPlannerService.FromProposals(proposals);
            if (steps.Count == 0)
                return "Proposed plan had no steps. Proceed without a plan.";

            var plan = new TaskPlan
            {
                OriginalRequest = string.IsNullOrWhiteSpace(originalRequest) ? goal : originalRequest,
                Steps = steps,
                Status = TaskPlanStatus.Pending
            };

            ExecutionStarted?.Invoke();
            string summary;
            try
            {
                summary = await OnPlanRequested(plan, ct);
            }
            finally
            {
                ExecutionFinished?.Invoke();
            }

            // Rejected / cancelled-before-start plans pass the UI's summary through
            // unchanged — no work happened, so there's nothing to manifest and the
            // model must stay free to act. Once steps DID run, the outer model needs
            // evidence of the work (it never saw the steps execute — they run in their
            // own chat histories), or it redoes the task from scratch. Observed live:
            // a completed build was overwritten by a fresh skeleton under auto-approve.
            if (plan.CompletedStepsCount == 0)
                return summary;

            LastPlanExecutedWork = true;

            List<(string Operation, string Path)> ops;
            lock (_lock) ops = new(_fileOperations);
            return BuildManifest(plan, ops);
        }
        finally
        {
            lock (_lock) _isExecuting = false;
        }
    }

    /// <summary>
    /// Marks a reconstructed plan as active and restores file-operation evidence from its saved
    /// state. The returned scope must cover the whole resumed run so nested planning stays blocked
    /// and new successful writes are appended to the restored evidence.
    /// </summary>
    public IDisposable BeginResumedExecution(IReadOnlyList<PlanFileOperation> savedFileOperations)
    {
        lock (_lock)
        {
            if (_isExecuting)
                throw new InvalidOperationException("A plan is already executing.");

            _isExecuting = true;
            LastPlanExecutedWork = false;
            _fileOperations.Clear();
            foreach (var operation in savedFileOperations)
                _fileOperations.Add((operation.Operation, operation.Path));
        }

        try
        {
            ExecutionStarted?.Invoke();
            return new ResumedExecutionScope(this);
        }
        catch
        {
            lock (_lock) _isExecuting = false;
            throw;
        }
    }

    private void EndResumedExecution()
    {
        try
        {
            ExecutionFinished?.Invoke();
        }
        finally
        {
            lock (_lock)
            {
                _isExecuting = false;
            }
        }
    }

    private sealed class ResumedExecutionScope(PlanHandoff owner) : IDisposable
    {
        private PlanHandoff? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndResumedExecution();
    }

    /// <summary>
    /// Builds the tool result the outer model sees after a plan executed: per-step
    /// statuses with capped result digests, the file operations recorded at the
    /// invocation-filter choke point, and an explicit stop directive. Evidence over
    /// verdicts — a bare "completed 4 of 4 steps" was observed live being treated as
    /// "not started yet". Capped (~500 chars/step) because this string lives in the
    /// outer chat history for the rest of the session, where small local context
    /// windows are precious.
    /// </summary>
    public static string BuildManifest(TaskPlan plan, IReadOnlyList<(string Operation, string Path)> fileOperations)
    {
        const int MaxStepResultChars = 500;

        var sb = new StringBuilder();
        sb.AppendLine($"Plan \"{plan.OriginalRequest}\" executed — {plan.CompletedStepsCount} of {plan.Steps.Count} steps completed.");

        foreach (var step in plan.Steps)
        {
            var marker = step.Status switch
            {
                TaskStepStatus.Completed => "[done]",
                TaskStepStatus.Failed => "[FAILED]",
                TaskStepStatus.Skipped => "[skipped]",
                _ => "[not run]"
            };
            sb.AppendLine();
            sb.AppendLine($"{marker} Step {step.StepNumber} — {step.Description}");

            var detail = step.Status == TaskStepStatus.Failed ? step.ErrorMessage : step.Result;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var capped = detail.Trim();
                if (capped.Length > MaxStepResultChars)
                    capped = capped[..MaxStepResultChars] + "…";
                sb.AppendLine("  " + capped.Replace("\n", "\n  "));
            }
        }

        if (fileOperations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Files touched during execution (these exist on disk NOW):");
            foreach (var group in fileOperations.GroupBy(o => o.Path, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {group.Key} ({string.Join(", ", group.Select(g => g.Operation).Distinct())})");
        }

        sb.AppendLine();
        sb.Append("IMPORTANT: All work above is ALREADY DONE — the files exist on disk. " +
                  "Do NOT recreate, rewrite, or re-verify them with tool calls. Respond to the " +
                  "user now with a brief summary of the outcome. If they want changes, they will " +
                  "ask in a follow-up message.");
        return sb.ToString();
    }
}
