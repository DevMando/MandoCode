using MandoCode.Models;
using Microsoft.Agents.AI.Workflows;

namespace MandoCode.Services;

/// <summary>
/// Mutable state shared by the plan workflow's executors and the runner driving it.
/// </summary>
/// <remarks>
/// <para>
/// Holds the live <see cref="TaskPlan"/> because the current consumer contract requires it: on a
/// failed step the consumer decides skip-vs-cancel by mutating <c>plan.Status</c>, which is only
/// visible once it comes back for the next event. <see cref="RaiseAndAwaitDecisionAsync"/> is what
/// preserves that; every other event is fire-and-forget so rendering never holds up the model.
/// </para>
/// <para>
/// Phase 4 note: this object holds live delegates, so a workflow built around it is NOT
/// checkpointable. Moving the plan and the accumulated results into the workflow's own shared state
/// is a prerequisite for resume — the step cursor already lives there to establish the pattern.
/// </para>
/// </remarks>
internal sealed class PlanRunContext(
    TaskPlan plan,
    IPlanStepExecutor stepExecutor,
    Func<TaskProgressEvent, bool, CancellationToken, Task> raise,
    CancellationToken cancellationToken)
{
    public TaskPlan Plan { get; } = plan;
    public IPlanStepExecutor StepExecutor { get; } = stepExecutor;
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Step results accumulated so far, in the format each step's context expects.</summary>
    public List<string> PreviousResults { get; } = [];

    /// <summary>
    /// Publishes a progress event without waiting for the consumer to render it.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget is safe because the channel behind this is FIFO with a single reader: events
    /// can never arrive out of order, the display simply trails the work. That matters — the legacy
    /// runner blocked on every event because each was a <c>yield return</c>, and reproducing that
    /// made the workflow wait on markdown rendering before it could start the next step, which was
    /// visibly slower on a fast model.
    /// <para>
    /// Use <see cref="RaiseAndAwaitDecisionAsync"/> for the one event that genuinely needs the
    /// consumer to answer.
    /// </para>
    /// </remarks>
    public Task RaiseAsync(TaskProgressEvent evt)
        => raise(evt, false, CancellationToken);

    /// <summary>
    /// Publishes a progress event and waits until the consumer has handled it and come back for the
    /// next one — the point at which any change it made to the plan is visible.
    /// </summary>
    /// <remarks>
    /// Only a failed step needs this. The consumer decides skip-vs-cancel by mutating
    /// <see cref="TaskPlan.Status"/>, and reading that before it has decided is precisely the bug
    /// the legacy runner documents: deciding before the yield silently downgraded "Cancel the plan"
    /// to "skip".
    /// </remarks>
    public Task RaiseAndAwaitDecisionAsync(TaskProgressEvent evt)
        => raise(evt, true, CancellationToken);

    /// <summary>
    /// Index of the next step that still needs running at or after <paramref name="from"/>, or -1.
    /// Steps already Completed or Skipped are stepped over rather than re-run — a resumed or
    /// partially-skipped plan must not redo work.
    /// </summary>
    public int NextRunnableIndex(int from)
    {
        for (var i = Math.Max(0, from); i < Plan.Steps.Count; i++)
        {
            var status = Plan.Steps[i].Status;
            if (status != TaskStepStatus.Completed && status != TaskStepStatus.Skipped)
                return i;
        }
        return -1;
    }
}

/// <summary>Seeds the run and dispatches the first runnable step.</summary>
[SendsMessage(typeof(RunPlanStep))]
[SendsMessage(typeof(PlanRunFinished))]
internal sealed class PlanIntakeExecutor(PlanRunContext ctx)
    : Executor<StartPlanRun>(PlanExecutorIds.Intake)
{
    public override async ValueTask HandleAsync(
        StartPlanRun message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ctx.Plan.Status = TaskPlanStatus.InProgress;
        await ctx.RaiseAsync(TaskProgressEvent.PlanCreated(ctx.Plan));

        var first = ctx.NextRunnableIndex(0);
        await context.QueueStateUpdateAsync(
            PlanWorkflowMessages.CursorKey, Math.Max(first, 0), PlanWorkflowMessages.StateScope, cancellationToken);

        if (first < 0)
        {
            await context.SendMessageAsync(
                new PlanRunFinished("Nothing to run."), PlanExecutorIds.Finalizer, cancellationToken);
            return;
        }

        await context.SendMessageAsync(new RunPlanStep(first), PlanExecutorIds.StepRunner, cancellationToken);
    }
}

/// <summary>
/// Runs exactly one step and reports the outcome. Holds no plan state — triage owns all of it.
/// </summary>
[SendsMessage(typeof(PlanStepOutcome))]
internal sealed class PlanStepRunnerExecutor(PlanRunContext ctx)
    : Executor<RunPlanStep>(PlanExecutorIds.StepRunner)
{
    public override async ValueTask HandleAsync(
        RunPlanStep message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var step = ctx.Plan.Steps[message.StepIndex];

        // Two cancellation signals, same as the legacy runner: the token, and a plan the consumer
        // cancelled between steps.
        if (ctx.CancellationToken.IsCancellationRequested || ctx.Plan.Status == TaskPlanStatus.Cancelled)
        {
            await Report(context, new PlanStepOutcome(
                message.StepIndex, PlanStepOutcomeKind.Cancelled, null, "Cancelled by user."), cancellationToken);
            return;
        }

        step.Status = TaskStepStatus.InProgress;

        // Does not wait for the UI: the model should not be held up by rendering. Ordering is
        // still guaranteed by the FIFO channel, and the spinner no longer contends because the
        // consumer owns it outright during a plan (see ExecutePlanStepAsync).
        await ctx.RaiseAsync(TaskProgressEvent.StepStarted(ctx.Plan, step));

        PlanStepOutcome outcome;
        try
        {
            var result = await ctx.StepExecutor.ExecuteStepAsync(
                step.Instruction, ctx.PreviousResults, ctx.CancellationToken);
            outcome = new PlanStepOutcome(message.StepIndex, PlanStepOutcomeKind.Completed, result, null);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            outcome = new PlanStepOutcome(message.StepIndex, PlanStepOutcomeKind.Cancelled, null, "Cancelled by user.");
        }
        catch (PlanCancellationRequestedException)
        {
            // "Cancel plan" chosen at a diff-approval prompt mid-step. Unambiguous: stop everything.
            outcome = new PlanStepOutcome(
                message.StepIndex, PlanStepOutcomeKind.Cancelled, null, "Plan cancelled by user from diff approval.");
        }
        catch (Exception ex)
        {
            outcome = new PlanStepOutcome(message.StepIndex, PlanStepOutcomeKind.Failed, null, ex.Message);
        }

        await ctx.StepExecutor.WaitForQuiescenceAsync(TimeSpan.FromSeconds(5));
        await Report(context, outcome, cancellationToken);
    }

    private static ValueTask Report(IWorkflowContext context, PlanStepOutcome outcome, CancellationToken ct)
        => context.SendMessageAsync(outcome, PlanExecutorIds.Triage, ct);
}

/// <summary>
/// Sole owner and sole writer of plan state. Decides what happens after each step and advances the
/// cursor, so no other executor and no consumer needs to.
/// </summary>
[SendsMessage(typeof(RunPlanStep))]
[SendsMessage(typeof(PlanRunFinished))]
internal sealed class PlanTriageExecutor(PlanRunContext ctx)
    : Executor<PlanStepOutcome>(PlanExecutorIds.Triage)
{
    public override async ValueTask HandleAsync(
        PlanStepOutcome message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var plan = ctx.Plan;
        var step = plan.Steps[message.StepIndex];

        switch (message.Kind)
        {
            case PlanStepOutcomeKind.Completed:
                step.Result = message.Result;
                step.Status = TaskStepStatus.Completed;
                ctx.PreviousResults.Add($"Step {step.StepNumber} ({step.Description}): {message.Result}");
                await ctx.RaiseAsync(TaskProgressEvent.StepCompleted(plan, step, message.Result));
                break;

            case PlanStepOutcomeKind.Cancelled:
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = message.Error;
                plan.Status = TaskPlanStatus.Cancelled;
                await ctx.RaiseAndAwaitDecisionAsync(TaskProgressEvent.StepFailed(plan, step, message.Error ?? "Cancelled."));
                await Finish(context, cancellationToken);
                return;

            case PlanStepOutcomeKind.Failed:
                step.Status = TaskStepStatus.Failed;
                step.ErrorMessage = message.Error;

                // Defer skip-vs-cancel to the consumer, then reconcile — matching the legacy runner,
                // where deciding before the yield silently downgraded "Cancel the plan" to "skip".
                await ctx.RaiseAndAwaitDecisionAsync(TaskProgressEvent.StepFailed(plan, step, message.Error ?? "Step failed."));

                if (plan.Status == TaskPlanStatus.Cancelled)
                {
                    await Finish(context, cancellationToken);
                    return;
                }

                // Either the consumer skipped it, or there was no interactive consumer at all. Both
                // mean "move past it" — the step must not be re-run.
                if (step.Status == TaskStepStatus.Failed)
                    step.Status = TaskStepStatus.Skipped;
                break;
        }

        if (plan.Status == TaskPlanStatus.Cancelled || ctx.CancellationToken.IsCancellationRequested)
        {
            plan.Status = TaskPlanStatus.Cancelled;
            await Finish(context, cancellationToken);
            return;
        }

        var next = ctx.NextRunnableIndex(message.StepIndex + 1);
        await context.QueueStateUpdateAsync(
            PlanWorkflowMessages.CursorKey, Math.Max(next, plan.Steps.Count), PlanWorkflowMessages.StateScope, cancellationToken);

        if (next < 0)
        {
            await Finish(context, cancellationToken);
            return;
        }

        await context.SendMessageAsync(new RunPlanStep(next), PlanExecutorIds.StepRunner, cancellationToken);
    }

    private static ValueTask Finish(IWorkflowContext context, CancellationToken ct)
        => context.SendMessageAsync(new PlanRunFinished("done"), PlanExecutorIds.Finalizer, ct);
}

/// <summary>Classifies the terminal state and emits the closing progress event.</summary>
[YieldsOutput(typeof(string))]
internal sealed class PlanFinalizerExecutor(PlanRunContext ctx)
    : Executor<PlanRunFinished>(PlanExecutorIds.Finalizer)
{
    public override async ValueTask HandleAsync(
        PlanRunFinished message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var plan = ctx.Plan;

        if (plan.Status == TaskPlanStatus.Cancelled)
        {
            await ctx.RaiseAsync(TaskProgressEvent.PlanCancelled(plan));
            await context.YieldOutputAsync(TaskPlanStatus.Cancelled.ToString(), cancellationToken);
            return;
        }

        // Classification copied deliberately from the legacy runner, including its quirk that a
        // plan whose steps were all skipped after failures still reports Completed. Changing it
        // here would make the two engines disagree while both are selectable; it belongs in the
        // phase that retires the legacy runner.
        var allCompleted = plan.Steps.All(s =>
            s.Status == TaskStepStatus.Completed || s.Status == TaskStepStatus.Skipped);
        var anyFailed = plan.Steps.Any(s => s.Status == TaskStepStatus.Failed);

        if (allCompleted && !anyFailed)
        {
            plan.Status = TaskPlanStatus.Completed;
            plan.ExecutionSummary = $"Successfully completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps.";
            await ctx.RaiseAsync(TaskProgressEvent.PlanCompleted(plan));
        }
        else
        {
            plan.Status = TaskPlanStatus.Failed;
            plan.ExecutionSummary = $"Completed {plan.CompletedStepsCount} of {plan.Steps.Count} steps with some failures.";
        }

        await context.YieldOutputAsync(plan.Status.ToString(), cancellationToken);
    }
}
