using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Owns the workflow plan runner and records a running plan's progress so it can be resumed after
/// an interruption.
/// </summary>
/// <remarks>
/// The workflow runner is cached once created; it holds no per-run state (each run builds its own
/// graph over its own context).
/// </remarks>
public sealed class PlanRunnerSelector(
    MandoCodeConfig config,
    IPlanStepExecutor stepExecutor,
    PlanHandoff? planHandoff = null,
    ProjectRootAccessor? projectRoot = null,
    string? checkpointId = null)
{
    private WorkflowPlanRunner? _workflowRunner;

    /// <summary>The workflow planner is the only supported plan runner.</summary>
    public bool UsingWorkflowEngine => true;

    /// <summary>
    /// True when progress is recorded for resume. Only the workflow engine reports its state, so
    /// only it can be resumed — the legacy runner keeps the plan in a local variable.
    /// </summary>
    public bool SupportsResume => UsingWorkflowEngine && projectRoot != null;

    /// <summary>The engine to run the next plan with.</summary>
    public IPlanRunner Current => _workflowRunner ??= new WorkflowPlanRunner(stepExecutor, planHandoff, RecordProgress);

    /// <summary>The plan recorded for this project that could be resumed, or <c>null</c>.</summary>
    /// <param name="refusal">
    /// Set when a record exists but must not be resumed — a different model, a different build.
    /// Worth showing: silently offering nothing looks identical to having lost the plan.
    /// </param>
    public PlanRunState? FindResumable(out string? refusal)
    {
        refusal = null;
        if (projectRoot == null) return null;
        return PlanCheckpointStore.Load(
            projectRoot.ProjectRoot,
            config.GetEffectiveModelName(),
            out refusal,
            checkpointId);
    }

    /// <summary>Forgets any recorded plan for this project.</summary>
    public void DiscardResumable()
    {
        if (projectRoot != null) PlanCheckpointStore.Delete(projectRoot.ProjectRoot, checkpointId);
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
            PlanCheckpointStore.Delete(projectRoot.ProjectRoot, checkpointId);
            return;
        }

        PlanCheckpointStore.Save(
            projectRoot.ProjectRoot,
            state,
            config.GetEffectiveModelName(),
            planId: checkpointId ?? PlanCheckpointEnvelope.HashProjectRoot(projectRoot.ProjectRoot),
            checkpointId: checkpointId);
    }
}
