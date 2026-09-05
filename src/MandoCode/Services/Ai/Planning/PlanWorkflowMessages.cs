namespace MandoCode.Services;

/// <summary>
/// Messages passed between the plan workflow's executors.
/// </summary>
/// <remarks>
/// Records, and deliberately small: everything here has to survive JSON round-tripping once
/// checkpointing lands, so nothing may carry a delegate, a stream, or a live service reference.
/// The step cursor travels in these messages and in the workflow's shared state — never in the
/// graph's shape, because resume requires byte-identical topology and a per-step node layout would
/// differ between a 3-step and a 12-step plan.
/// </remarks>
internal static class PlanWorkflowMessages
{
    /// <summary>Shared state scope. State written without a scope name is executor-private.</summary>
    public const string StateScope = "mandocode.plan";

    /// <summary>Key under <see cref="StateScope"/> holding the zero-based index of the next step.</summary>
    public const string CursorKey = "cursor";

    /// <summary>
    /// Key under <see cref="StateScope"/> holding the whole <see cref="PlanRunState"/>.
    /// </summary>
    /// <remarks>
    /// This is what makes the run resumable: MAF captures shared state at every superstep boundary,
    /// so anything here survives a checkpoint. Anything held only in <see cref="PlanRunContext"/>
    /// does not — that object carries live delegates and cannot be serialized.
    /// </remarks>
    public const string StateKey = "state";
}

/// <summary>Kicks off a run. Carries nothing: the plan itself is owned by the run context.</summary>
internal sealed record StartPlanRun;

/// <summary>Instructs the step runner to execute the step at <paramref name="StepIndex"/>.</summary>
internal sealed record RunPlanStep(int StepIndex);

/// <summary>What happened to one step. Triage is the only thing that acts on this.</summary>
internal sealed record PlanStepOutcome(
    int StepIndex,
    PlanStepOutcomeKind Kind,
    string? Result,
    string? Error);

internal enum PlanStepOutcomeKind
{
    /// <summary>Step finished and produced a result.</summary>
    Completed,

    /// <summary>Step threw. Whether this skips the step or ends the plan is the consumer's call.</summary>
    Failed,

    VerificationUnavailable,

    /// <summary>Cancellation token tripped, or the user cancelled the plan from a diff prompt.</summary>
    Cancelled,
}

/// <summary>Terminal message; the finalizer turns this into the workflow's output.</summary>
internal sealed record PlanRunFinished(string Summary);
