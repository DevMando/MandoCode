using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Resolves which plan engine to use, re-reading the <c>planner</c> config key on every access.
/// </summary>
/// <remarks>
/// Not a plain DI registration because <c>planner</c> is <c>KernelRebuild</c>-scoped: it is meant to
/// take effect on the next message without losing history, so a singleton captured at startup would
/// silently ignore <c>/config set planner workflow</c>. Re-reading also makes an A/B practical —
/// same session, same history, flip the key, re-run the same prompt.
/// <para>
/// The workflow runner is cached once created; it holds no per-run state (each run builds its own
/// graph over its own context).
/// </para>
/// </remarks>
public sealed class PlanRunnerSelector(
    MandoCodeConfig config,
    TaskPlannerService legacyRunner,
    IPlanStepExecutor stepExecutor)
{
    private WorkflowPlanRunner? _workflowRunner;

    /// <summary>True when the workflow engine is currently selected.</summary>
    public bool UsingWorkflowEngine => string.Equals(
        config.PlannerEngine, MandoCodeConfig.PlannerEngineWorkflow, StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine to run the next plan with.</summary>
    public IPlanRunner Current => UsingWorkflowEngine
        ? _workflowRunner ??= new WorkflowPlanRunner(stepExecutor)
        : legacyRunner;
}
