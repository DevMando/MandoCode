using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace MandoCode.Plugins;

/// <summary>
/// Exposes the propose_plan function so the model can self-classify multi-step
/// requests mid-conversation. The call is intercepted by FunctionInvocationFilter
/// and handed off to the UI via PlanHandoff — the body here is never the real
/// execution path and just returns a sentinel if interception is bypassed.
/// </summary>
public class PlanningPlugin
{
    [KernelFunction("propose_plan")]
    [Description(
        "Propose a multi-step plan for the user's request. Call this ONLY when the request " +
        "clearly requires multiple distinct file or code operations that depend on each other " +
        "(e.g., 'build a feature', 'refactor across files', 'set up a new service'). " +
        "Do NOT call for questions, single-file edits, lookups, or one-shot operations. " +
        "Each item in 'steps' MUST include a non-empty 'description' (short UI label) AND a " +
        "non-empty 'instruction' (the actual task the assistant will execute for that step). " +
        "The user will review and approve the plan before any step executes.")]
    public Task<string> ProposePlan(
        [Description("One-sentence summary of the overall goal.")]
            string goal,
        [Description("Ordered list of steps. Each step needs 'description' (short, <=60 chars) and 'instruction' (detailed task to execute).")]
            PlanStepProposal[] steps)
    {
        return Task.FromResult("__PLAN_INTERCEPTED__");
    }
}

/// <summary>
/// Parameters are camelCase to match the JSON the model emits — PascalCase here caused
/// silent deserialization failures (empty strings) with local Ollama tool calls.
/// </summary>
public record PlanStepProposal(
    [property: Description("Short description for UI (<=60 chars)")] string description,
    [property: Description("Detailed instruction the AI will execute for this step")] string instruction);
