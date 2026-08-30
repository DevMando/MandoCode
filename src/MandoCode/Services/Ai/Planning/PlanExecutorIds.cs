namespace MandoCode.Services;

/// <summary>
/// Stable identities for the plan workflow's agents and executors.
/// </summary>
/// <remarks>
/// <para>
/// MAF matches checkpointed state back to executors by identity, and for agent-backed executors
/// that identity derives from <b>both</b> <c>ChatClientAgentOptions.Id</c> and <c>Name</c>. A
/// checkpoint written under one identity cannot be resumed under another, and there is no repair
/// path — so every value here is a literal <c>const string</c> fixed before the first checkpoint
/// is ever written to disk.
/// </para>
/// <para>
/// Rules, enforced by <c>PlanExecutorIdsTests</c>:
/// <list type="bullet">
/// <item>Literal constants only — no interpolation, no <c>nameof</c>, no <c>Guid.NewGuid()</c>.</item>
/// <item>Nothing volatile may appear in an id: not the model name, not the temperature, not the
/// project path, not the step number. <see cref="AIService.BuildAgent"/> re-runs on every MCP
/// reconcile and every <c>KernelRebuild</c>-scoped <c>/config set</c>; an id derived from any of
/// those would change mid-session and orphan a live plan's checkpoints with no error message.</item>
/// <item>The step agent must not share a name with the generalist agent — two differently-purposed
/// agents under one name is exactly the collision that routes restored state to the wrong executor.</item>
/// <item>Adding, removing or renaming a node is a topology change: bump <see cref="TopologyVersion"/>
/// and refuse older checkpoints loudly rather than resuming onto a mismatched graph.</item>
/// </list>
/// </para>
/// </remarks>
public static class PlanExecutorIds
{
    /// <summary>
    /// Graph shape version. Bump on ANY change to the executor set or the edges between them.
    /// Checkpoints recording a different value are refused, never best-effort resumed — a
    /// mismatched topology could re-run a step whose <c>write_file</c> already succeeded.
    /// </summary>
    public const string TopologyVersion = "1";

    /// <summary>Logical id of the single generalist agent, pinned at <see cref="AIService.BuildAgent"/>.</summary>
    public const string GeneralistAgentId = "mandocode.agent.generalist";

    /// <summary>
    /// Display name of the generalist agent. Deliberately unchanged from the pre-workflow value —
    /// it is user-visible, and changing it would invalidate identity for no benefit.
    /// </summary>
    public const string GeneralistAgentName = "MandoCode";

    /// <summary>Id of the agent that executes a single plan step. Distinct from the generalist pair.</summary>
    public const string StepAgentId = "mandocode.plan.v1.step-agent";

    /// <summary>Name of the step agent. Must differ from <see cref="GeneralistAgentName"/>.</summary>
    public const string StepAgentName = "mandocode-plan-step";

    /// <summary>Normalizes the proposal into a plan and emits the approval request.</summary>
    public const string Intake = "mandocode.plan.v1.intake";

    /// <summary>Request port for plan sign-off; re-entered after a revision.</summary>
    public const string ApprovalPort = "mandocode.plan.v1.approval";

    /// <summary>Routes the approval verdict to execution, rejection or cancellation.</summary>
    public const string Gate = "mandocode.plan.v1.gate";

    /// <summary>Runs one step. Invoked once per step via the loop-back edge from triage.</summary>
    public const string StepRunner = "mandocode.plan.v1.step-runner";

    /// <summary>Sole owner and sole writer of plan state; decides what happens after each step.</summary>
    public const string Triage = "mandocode.plan.v1.triage";

    /// <summary>Request port for per-step recovery decisions (retry / skip / edit / replan / cancel).</summary>
    public const string DecisionPort = "mandocode.plan.v1.step-decision";

    /// <summary>Produces a revised plan, which then re-enters <see cref="ApprovalPort"/>.</summary>
    public const string Replanner = "mandocode.plan.v1.replanner";

    /// <summary>Builds the closing manifest and yields the workflow output.</summary>
    public const string Finalizer = "mandocode.plan.v1.finalizer";

    /// <summary>
    /// Every executor id in the graph. Exposed so a golden-list test can fail loudly on a
    /// careless rename — that test is the regression net protecting checkpoint compatibility.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Intake,
        ApprovalPort,
        Gate,
        StepRunner,
        Triage,
        DecisionPort,
        Replanner,
        Finalizer,
    ];
}
