using System.Text.RegularExpressions;
using MandoCode.Models;
using MandoCode.Plugins;

namespace MandoCode.Services;

/// <summary>
/// Service for executing multi-step plans. Plans are proposed by the model via
/// the propose_plan tool (see <see cref="PlanningPlugin"/>) and materialised by
/// <see cref="FromProposals"/>. A slim deterministic heuristic (<see cref="RequiresPlanning"/>)
/// only exists for local models that don't reliably self-invoke the tool.
/// </summary>
public class TaskPlannerService
{
    private readonly AIService _aiService;
    private readonly MandoCodeConfig _config;
    private readonly object _planStatusLock = new();

    public TaskPlannerService(AIService aiService, MandoCodeConfig config)
    {
        _aiService = aiService;
        _config = config;
    }

    /// <summary>
    /// Deterministic planning signal for models that can't be trusted to self-invoke
    /// propose_plan. Only fires on near-zero-false-positive signals; everything else
    /// defers to the model's judgement.
    /// </summary>
    public bool RequiresPlanning(string userMessage)
    {
        if (!_config.EnableTaskPlanning)
            return false;

        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var trimmed = userMessage.Trim();

        // Signal 1: Explicit multi-step intent — 3+ numbered items.
        var numberedItems = Regex
            .Matches(trimmed, @"^\s*\d+[\.\)]\s+", RegexOptions.Multiline)
            .Count;
        if (numberedItems >= 3)
            return true;

        // Signal 2: Very long requests — users don't write 400+ chars for lookups.
        if (trimmed.Length > 400)
            return true;

        return false;
    }

    /// <summary>
    /// Materialises a list of <see cref="TaskStep"/> from the model's typed tool-call
    /// arguments. Replaces the old 5-parser soup used when plans arrived as free text.
    /// </summary>
    public static List<TaskStep> FromProposals(PlanStepProposal[] proposals)
    {
        if (proposals == null)
            return new List<TaskStep>();

        // Drop any fully-empty proposals (both fields missing) so a casing mismatch in
        // the model's tool call doesn't silently produce a plan of empty steps.
        var filtered = proposals.Where(p =>
            !string.IsNullOrWhiteSpace(p.description) ||
            !string.IsNullOrWhiteSpace(p.instruction));

        return filtered.Select((p, i) =>
        {
            var desc = p.description ?? string.Empty;
            var instr = p.instruction ?? string.Empty;

            // If only one of the two is populated, reuse it for the other so the step is still runnable.
            if (string.IsNullOrWhiteSpace(desc)) desc = instr;
            if (string.IsNullOrWhiteSpace(instr)) instr = desc;

            return new TaskStep
            {
                StepNumber = i + 1,
                Description = desc.Length > 60 ? desc[..57] + "..." : desc,
                Instruction = instr,
                Status = TaskStepStatus.Pending
            };
        }).ToList();
    }

    /// <summary>
    /// Executes a task plan step by step, yielding progress events.
    /// </summary>
    public async IAsyncEnumerable<TaskProgressEvent> ExecutePlanAsync(TaskPlan plan, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        plan.Status = TaskPlanStatus.InProgress;
        var previousResults = new List<string>();

        yield return TaskProgressEvent.PlanCreated(plan);

        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelPlan(plan);
                yield return TaskProgressEvent.PlanCancelled(plan);
                yield break;
            }

            if (step.Status == TaskStepStatus.Completed || step.Status == TaskStepStatus.Skipped)
                continue;

            step.Status = TaskStepStatus.InProgress;
            yield return TaskProgressEvent.StepStarted(plan, step);

            TaskProgressEvent? stepEvent = null;
            bool shouldCancel = false;

            try
            {
                var result = await _aiService.ExecutePlanStepAsync(step.Instruction, previousResults, cancellationToken);

                step.Result = result;
                step.Status = TaskStepStatus.Completed;
                previousResults.Add($"Step {step.StepNumber} ({step.Description}): {result}");

                stepEvent = TaskProgressEvent.StepCompleted(plan, step, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = "Cancelled by user.";
                shouldCancel = true;
                CancelPlan(plan);
                stepEvent = TaskProgressEvent.StepFailed(plan, step, "Cancelled by user.");
            }
            catch (PlanCancellationRequestedException)
            {
                // User chose "Cancel plan" from a diff-approval prompt mid-step.
                // Distinct from token cancellation — the step hadn't finished, but the
                // user's intent is unambiguous: stop the whole plan, not just this step.
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = "Plan cancelled by user from diff approval.";
                shouldCancel = true;
                CancelPlan(plan);
                stepEvent = TaskProgressEvent.StepFailed(plan, step, "Plan cancelled by user.");
            }
            catch (Exception ex)
            {
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = ex.Message;
                stepEvent = TaskProgressEvent.StepFailed(plan, step, ex.Message);

                lock (_planStatusLock)
                {
                    if (plan.Status == TaskPlanStatus.Cancelled)
                    {
                        shouldCancel = true;
                    }
                    else
                    {
                        step.Status = TaskStepStatus.Skipped;
                    }
                }
            }

            await _aiService.CompletionTracker.WaitForAllCompletionsAsync(TimeSpan.FromSeconds(5));

            if (stepEvent != null)
            {
                yield return stepEvent;
            }

            if (shouldCancel)
            {
                yield return TaskProgressEvent.PlanCancelled(plan);
                yield break;
            }
        }

        var allCompleted = plan.Steps.All(s =>
            s.Status == TaskStepStatus.Completed || s.Status == TaskStepStatus.Skipped);

        var anyFailed = plan.Steps.Any(s => s.Status == TaskStepStatus.Failed);

        if (allCompleted && !anyFailed)
        {
            plan.Status = TaskPlanStatus.Completed;
            plan.ExecutionSummary = $"Successfully completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps.";
            yield return TaskProgressEvent.PlanCompleted(plan);
        }
        else if (plan.Status != TaskPlanStatus.Cancelled)
        {
            plan.Status = TaskPlanStatus.Failed;
            plan.ExecutionSummary = $"Completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps with some failures.";
        }
    }

    public void SkipStep(TaskPlan plan, TaskStep step)
    {
        step.Status = TaskStepStatus.Skipped;
    }

    public void CancelPlan(TaskPlan plan)
    {
        lock (_planStatusLock)
        {
            plan.Status = TaskPlanStatus.Cancelled;
            plan.ExecutionSummary = "Plan cancelled by user.";
        }
    }
}
