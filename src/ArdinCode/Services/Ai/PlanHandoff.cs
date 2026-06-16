using System.Text;
using ArdinCode.Models;
using ArdinCode.Plugins;

namespace ArdinCode.Services;

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

    // File operations recorded by FunctionInvocationFilter while a plan executes.
    // These are EVIDENCE from the choke point (the call actually ran and succeeded),
    // not model self-reports — they feed the manifest the outer model receives.
    private readonly List<(string Operation, string Path)> _fileOperations = new();

    /// <summary>
    /// True when the most recent <see cref="ProcessAsync"/> actually executed work
    /// (at least one step completed). The filter reads this to arm the post-plan
    /// mutation gate — a rejected or never-started plan leaves it false, because the
    /// model is then expected to do the work directly.
    /// </summary>
    public bool LastPlanExecutedWork { get; private set; }

    /// <summary>
    /// Called by FunctionInvocationFilter after a successful filesystem-mutating call.
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
                OriginalRequest = goal,
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
