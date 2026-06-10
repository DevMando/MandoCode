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
/// Bridge between FunctionInvocationFilter (where propose_plan is intercepted)
/// and App.razor (which drives the approval UI and plan execution).
///
/// The filter calls <see cref="ProcessAsync"/> and awaits the summary string.
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
    /// (success, rejection, or throw). The whole plan runs inside a single outer model call
    /// (the propose_plan tool), so the outer call's stall watchdog would otherwise fire mid-plan
    /// on a slow step and surface as a bogus "Cancelled by user." AIService subscribes to pause
    /// that outer watchdog for the plan's duration — the plan's own per-step watchdogs cover stalls.
    /// </summary>
    public event Action? ExecutionStarted;
    public event Action? ExecutionFinished;

    /// <summary>
    /// Called by FunctionInvocationFilter when the model invokes propose_plan.
    /// Guards against recursive planning (the model calling propose_plan while a
    /// previous plan is still running) by returning a short-circuit message.
    /// </summary>
    public async Task<string> ProcessAsync(
        string goal,
        PlanStepProposal[] proposals,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_isExecuting)
                return "A plan is already executing. Continue the current step instead of proposing a new plan.";
            _isExecuting = true;
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
                OriginalRequest = goal,
                Steps = steps,
                Status = TaskPlanStatus.Pending
            };

            ExecutionStarted?.Invoke();
            try
            {
                return await OnPlanRequested(plan, ct);
            }
            finally
            {
                ExecutionFinished?.Invoke();
            }
        }
        finally
        {
            lock (_lock) _isExecuting = false;
        }
    }
}
