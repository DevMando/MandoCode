using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Runs an approved <see cref="TaskPlan"/> to completion, reporting progress as it goes.
/// </summary>
/// <remarks>
/// <para>
/// The member signatures deliberately match what <see cref="TaskPlannerService"/> already exposes
/// and what both front-ends already consume, so the workflow engine can be swapped in behind this
/// interface without either UI changing.
/// </para>
/// <para>
/// Note the current consumer contract, which the workflow implementation must either honour or
/// deliberately replace: <see cref="ExecutePlanAsync"/> hands control to the consumer <i>during</i>
/// its <c>yield return</c>, and a consumer handling a failed step is expected to mutate
/// <see cref="TaskPlan.Status"/> synchronously before returning. A consumer that doesn't (any
/// non-interactive caller) silently gets every failure downgraded to "skipped". Removing that
/// hazard — by making progress read-only and routing decisions back through a request port — is
/// the point of the workflow rebuild.
/// </para>
/// </remarks>
public interface IPlanRunner
{
    /// <summary>Executes the plan step by step, yielding progress as each step starts and settles.</summary>
    IAsyncEnumerable<TaskProgressEvent> ExecutePlanAsync(
        TaskPlan plan,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a step skipped so execution moves on to the next one.</summary>
    void SkipStep(TaskPlan plan, TaskStep step);

    /// <summary>Marks the whole plan cancelled; the runner stops at the next boundary.</summary>
    void CancelPlan(TaskPlan plan);
}
