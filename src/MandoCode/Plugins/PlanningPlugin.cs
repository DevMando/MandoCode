using System.ComponentModel;

namespace MandoCode.Plugins;

/// <summary>
/// Exposes the propose_plan function so the model can self-classify multi-step
/// requests mid-conversation. The call is intercepted by AgentFunctionMiddleware
/// and handed off to the UI via PlanHandoff — the body here is never the real
/// execution path and just returns a sentinel if interception is bypassed.
/// </summary>
public class PlanningPlugin
{
    [Description(
        "Propose a multi-step plan for the user's request. Call this ONLY when the request " +
        "clearly requires multiple distinct file or code operations that depend on each other " +
        "(e.g., 'build a feature', 'refactor across files', 'set up a new service'). " +
        "Do NOT call for questions, single-file edits, lookups, or one-shot operations. " +
        "Each item in 'steps' MUST include a non-empty 'description' (short UI label) AND a " +
        "non-empty 'instruction' (the actual task the assistant will execute for that step), and " +
        "'acceptanceCriteria' (2-5 concrete, observable checks defining completion). " +
        "Avoid overlapping steps. Existing work may satisfy a later step; inspect and implement only gaps. " +
        "Every check must be achievable by the end of its step; put prerequisite implementation in earlier steps. " +
        "For discovery, verified absence is a valid finding: identify existing files/tooling OR document that none exist. " +
        "Never require an existing framework in an empty project. If already known empty, start with project setup. " +
        "The user will review and approve the plan before any step executes.")]
    public Task<string> ProposePlan(
        [Description("One-sentence summary of the overall goal.")]
            string goal,
        [Description("Ordered list of steps. Each step needs 'description' (short, <=60 chars), 'instruction' (detailed task), and 'acceptanceCriteria' (2-5 observable checks achievable within that step).")]
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
    [property: Description("Detailed instruction the AI will execute for this step")] string instruction,
    [property: Description("Concrete completion checks shared by executor and verifier; runtime claims need runtime checks")]
    string[]? acceptanceCriteria = null);
