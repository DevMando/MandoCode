using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Resolves which plan engine to use, re-reading the <c>planner</c> config key on every access, and
/// records a running plan's progress so it can be resumed after an interruption.
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
    IPlanStepExecutor stepExecutor,
    PlanHandoff? planHandoff = null,
    ProjectRootAccessor? projectRoot = null)
{
    private WorkflowPlanRunner? _workflowRunner;

    /// <summary>True when the workflow engine is currently selected.</summary>
    public bool UsingWorkflowEngine => string.Equals(
        config.PlannerEngine, MandoCodeConfig.PlannerEngineWorkflow, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when progress is recorded for resume. Only the workflow engine reports its state, so
    /// only it can be resumed — the legacy runner keeps the plan in a local variable.
    /// </summary>
    public bool SupportsResume => UsingWorkflowEngine && projectRoot != null;

    /// <summary>The engine to run the next plan with.</summary>
    public IPlanRunner Current => UsingWorkflowEngine
        ? _workflowRunner ??= new WorkflowPlanRunner(stepExecutor, planHandoff, RecordProgress)
        : legacyRunner;

    /// <summary>The plan recorded for this project that could be resumed, or <c>null</c>.</summary>
    /// <param name="refusal">
    /// Set when a record exists but must not be resumed — a different model, a different build.
    /// Worth showing: silently offering nothing looks identical to having lost the plan.
    /// </param>
    public PlanRunState? FindResumable(out string? refusal)
    {
        refusal = null;
        if (projectRoot == null) return null;
        return PlanCheckpointStore.Load(projectRoot.ProjectRoot, config.GetEffectiveModelName(), out refusal);
    }

    /// <summary>Forgets any recorded plan for this project.</summary>
    public void DiscardResumable()
    {
        if (projectRoot != null) PlanCheckpointStore.Delete(projectRoot.ProjectRoot);
    }

    /// <summary>
    /// Called by the workflow runner each time the plan advances. Clears the record once nothing is
    /// outstanding, so a finished plan is never offered for resume.
    /// </summary>
    private void RecordProgress(PlanRunState state)
    {
        if (projectRoot == null) return;

        if (PlanCheckpointStore.OutstandingSteps(state) == 0)
        {
            PlanCheckpointStore.Delete(projectRoot.ProjectRoot);
            return;
        }

        PlanCheckpointStore.Save(
            projectRoot.ProjectRoot,
            state,
            config.GetEffectiveModelName(),
            planId: PlanCheckpointEnvelope.HashProjectRoot(projectRoot.ProjectRoot));
    }
}
