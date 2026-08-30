using MandoCode.Plugins;

namespace MandoCode.Models;

/// <summary>
/// A plan produced by the model in proposal-only mode. No tools other than
/// <c>propose_plan</c> are available while this value is generated.
/// </summary>
public sealed record GeneratedPlan(string Goal, PlanStepProposal[] Steps);
