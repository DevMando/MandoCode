using System.Text.RegularExpressions;
using MandoCode.Models;
using MandoCode.Plugins;

namespace MandoCode.Services;

/// <summary>
/// Service for executing multi-step plans. Plans are proposed by the model via
/// the propose_plan tool (see <see cref="PlanningPlugin"/>) and materialised by
/// <see cref="FromProposals"/>. A slim deterministic heuristic (<see cref="RequiresPlanning"/>)
/// lets the host route high-confidence multi-step requests without relying on a tool call.
/// </summary>
public class TaskPlannerService : IPlanRunner
{
    public readonly record struct PlanningDecision(bool Required, int Score, string? Reason)
    {
        public static PlanningDecision No => new(false, 0, null);
    }

    private readonly IPlanStepExecutor _stepExecutor;
    private readonly MandoCodeConfig _config;
    private readonly object _planStatusLock = new();

    /// <summary>
    /// Preferred constructor. Taking the step executor rather than <see cref="AIService"/> is what
    /// lets plan sequencing, cancellation and skip/fail handling be tested without a live model.
    /// </summary>
    public TaskPlannerService(IPlanStepExecutor stepExecutor, MandoCodeConfig config)
    {
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _config = config;
    }

    /// <summary>
    /// Delegating overload kept so existing callers — including the Desktop app, which constructs
    /// this by hand — compile unchanged against this commit.
    /// </summary>
    public TaskPlannerService(AIService aiService, MandoCodeConfig config)
        : this(new AiServicePlanStepExecutor(aiService), config)
    {
    }

    /// <summary>
    /// Backward-compatible boolean view of the explainable planning decision.
    /// </summary>
    public bool RequiresPlanning(string userMessage) => GetPlanningDecision(userMessage).Required;

    public PlanningDecision GetPlanningDecision(string userMessage)
    {
        if (!_config.EnableTaskPlanning)
            return PlanningDecision.No;

        if (string.IsNullOrWhiteSpace(userMessage))
            return PlanningDecision.No;

        var trimmed = userMessage.Trim();

        // Questions and read-only investigations remain conversational even when formatted as a
        // numbered list. A concrete mutation verb opts back into the scored task path below.
        const RegexOptions decisionOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var startsReadOnly = Regex.IsMatch(trimmed,
            @"^\s*(what|why|how|explain|review|inspect|research|summarize|analyse|analyze|compare|find|locate|show|list)\b",
            decisionOptions);
        var containsMutation = Regex.IsMatch(trimmed,
            @"\b(build|create|implement|add|update|fix|refactor|migrate|replace|remove|delete|scaffold|set\s+up|convert|integrate)\b",
            decisionOptions);
        if (startsReadOnly && !containsMutation)
            return PlanningDecision.No;

        // Signal 1: Explicit multi-step intent — 3+ numbered items.
        var numberedItems = Regex
            .Matches(trimmed, @"^\s*\d+[\.\)]\s+", RegexOptions.Multiline)
            .Count;
        if (numberedItems >= 3)
            return new PlanningDecision(true, 5, "three or more requested steps");

        // Other requests use task shape rather than raw message length.
        return ScorePlanningRequest(trimmed);
    }

    private static PlanningDecision ScorePlanningRequest(string request)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var mutationMatches = Regex.Matches(request,
            @"\b(build|create|implement|add|update|fix|refactor|migrate|replace|remove|delete|scaffold|set\s+up|convert|integrate)\b",
            options).Count;
        var readOnlyIntent = Regex.IsMatch(request,
            @"^\s*(what|why|how|explain|review|inspect|research|summarize|analyse|analyze|compare|find|locate|show|list)\b",
            options);
        if (readOnlyIntent && mutationMatches == 0)
            return PlanningDecision.No;

        if (Regex.IsMatch(request,
            @"\b(make|create|write|give me|propose)\s+(a\s+)?plan\b|\bbreak\s+(this|it)\s+(down\s+)?into\s+steps\b",
            options))
            return new PlanningDecision(true, 5, "an explicit request for a plan");

        var broadScope = mutationMatches > 0 && Regex.IsMatch(request,
            @"\b(complete|entire|whole|end[- ]to[- ]end|from scratch|application|app|game|service|system|project)\b",
            options);
        var crossCutting = mutationMatches > 0 && Regex.IsMatch(request,
            @"\b(across|throughout|multiple|several|both)\b|\b(API|CLI|Desktop|database|frontend|backend)\b.*\b(and|plus)\b.*\b(API|CLI|Desktop|database|frontend|backend)\b",
            options);

        var deliverableGroups = 0;
        if (mutationMatches > 0) deliverableGroups++;
        if (Regex.IsMatch(request, @"\b(test|tests|testing|coverage)\b", options)) deliverableGroups++;
        if (Regex.IsMatch(request, @"\b(document|documentation|docs|README)\b", options)) deliverableGroups++;
        if (Regex.IsMatch(request, @"\b(deploy|deployment|CI|pipeline|release)\b", options)) deliverableGroups++;
        if (Regex.IsMatch(request, @"\b(UI|frontend|API|database|authentication|authorization|saving|menus?)\b", options)) deliverableGroups++;

        var narrowTarget = Regex.IsMatch(request,
            @"\b(typo|single file|one file|one method|this method|one property|this property|rename)\b",
            options);
        var score = (broadScope ? 2 : 0)
                  + (crossCutting ? 2 : 0)
                  + (deliverableGroups >= 3 ? 2 : 0)
                  + (mutationMatches >= 3 ? 1 : 0)
                  - (narrowTarget ? 2 : 0);
        var hasScopeSignal = broadScope || crossCutting || deliverableGroups >= 3;
        if (score < 4 || !hasScopeSignal)
            return new PlanningDecision(false, score, null);

        var reason = crossCutting
            ? "cross-cutting work across multiple areas"
            : deliverableGroups >= 3
                ? "multiple deliverables were requested"
                : "a broad multi-part implementation";
        return new PlanningDecision(true, score, reason);
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
            // Two cancellation signals to watch:
            //   1. CancellationToken — set by Ctrl+C or external abort.
            //   2. plan.Status == Cancelled — set by the StepFailed handler in App.razor
            //      after the user picks "Cancel the plan." This one happens *between*
            //      iterations of this loop and was previously ignored entirely.
            if (cancellationToken.IsCancellationRequested || plan.Status == TaskPlanStatus.Cancelled)
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
            bool wasGenericFailure = false;

            try
            {
                var result = await _stepExecutor.ExecuteStepAsync(step.Instruction, previousResults, cancellationToken);

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
                // Defer the skip-vs-cancel decision: the user hasn't chosen yet. The
                // StepFailed handler in App.razor will yield the prompt, mutate plan.Status
                // if they pick "Cancel the plan," and only THEN should we decide. Earlier
                // versions of this catch pre-decided `shouldCancel` here, before the yield,
                // so a "Cancel the plan" pick was silently downgraded to "skip."
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = ex.Message;
                wasGenericFailure = true;
                stepEvent = TaskProgressEvent.StepFailed(plan, step, ex.Message);
            }

            await _stepExecutor.WaitForQuiescenceAsync(TimeSpan.FromSeconds(5));

            if (stepEvent != null)
            {
                // Hand control to the consumer (App.razor). Their StepFailed handler may
                // show the "Skip / Cancel the plan" prompt and mutate plan.Status before
                // returning here. Any decision based on plan.Status MUST happen after this.
                yield return stepEvent;
            }

            // Post-yield reconciliation for the generic-failure path:
            //   • If the user picked "Cancel the plan" → CancelPlan(plan) ran, plan.Status
            //     is now Cancelled, and we should bail.
            //   • Otherwise → SkipStep ran (sets step.Status = Skipped) OR neither handler
            //     ran (programmatic caller). In both cases, mark the step Skipped so the
            //     loop continues past it without re-running.
            if (wasGenericFailure)
            {
                lock (_planStatusLock)
                {
                    if (plan.Status == TaskPlanStatus.Cancelled)
                        shouldCancel = true;
                    else if (step.Status == TaskStepStatus.Failed)
                        step.Status = TaskStepStatus.Skipped;
                }
            }

            if (shouldCancel)
            {
                yield return TaskProgressEvent.PlanCancelled(plan);
                yield break;
            }
        }

        var allSettled = plan.Steps.All(s =>
            s.Status == TaskStepStatus.Completed || s.Status == TaskStepStatus.Skipped);
        var anySkipped = plan.Steps.Any(s => s.Status == TaskStepStatus.Skipped);
        var anyFailed = plan.Steps.Any(s => s.Status == TaskStepStatus.Failed);

        if (allSettled && !anyFailed)
        {
            plan.Status = anySkipped
                ? TaskPlanStatus.CompletedWithIssues
                : TaskPlanStatus.Completed;
            plan.ExecutionSummary = anySkipped
                ? $"Completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps; " +
                  $"{plan.Steps.Count(s => s.Status == TaskStepStatus.Skipped)} step(s) were skipped after failure."
                : $"Successfully completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps.";
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
