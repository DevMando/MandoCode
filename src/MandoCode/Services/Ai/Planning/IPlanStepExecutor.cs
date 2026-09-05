using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// Executes a single plan step. The seam that lets the plan runner be tested without a live model.
/// </summary>
/// <remarks>
/// This exists before any workflow code is written, on purpose: with the runner bound directly to
/// <see cref="AIService"/>, every test of step ordering, cancellation, retry or checkpoint/resume
/// would need a running Ollama, and the migration's go/no-go gates would be unenforceable.
/// <para>
/// Implementations must route through <c>AgentFunctionMiddleware</c> — the ten guard circuits,
/// diff approvals and MCP gating all live there. A step executor that calls the model directly
/// would silently drop all of them.
/// </para>
/// </remarks>
public interface IPlanStepExecutor
{
    /// <summary>Runs one step to completion and returns the model's result text.</summary>
    /// <param name="stepInstruction">The step's detailed instruction (not its short UI description).</param>
    /// <param name="previousResults">
    /// Results of earlier steps, oldest first. Implementations may window this — the current one
    /// keeps only the most recent few, which is what keeps 8k-context local models viable.
    /// </param>
    Task<string> ExecuteStepAsync(
        string stepInstruction,
        List<string> previousResults,
        CancellationToken cancellationToken = default);

    Task<string> ExecuteAttemptAsync(TaskStep step, List<string> previousResults,
        Func<string, Task> activity, CancellationToken cancellationToken = default)
        => ExecuteStepAsync(step.Instruction, previousResults, cancellationToken);

    /// <summary>
    /// Waits for tool calls still in flight from the step just finished to settle, so the next
    /// step doesn't start while the previous one is still writing.
    /// </summary>
    /// <remarks>
    /// Part of the seam rather than left on <see cref="AIService"/>, so the runner has no reason
    /// to hold an <see cref="AIService"/> reference at all and stays testable without one.
    /// </remarks>
    Task WaitForQuiescenceAsync(TimeSpan timeout);
}

/// <summary>
/// Adapts <see cref="AIService.ExecutePlanStepAsync"/> to <see cref="IPlanStepExecutor"/>.
/// Deliberately trivial: all step semantics stay in <see cref="AIService"/>, so this migration
/// does not quietly fork them.
/// </summary>
public sealed class AiServicePlanStepExecutor(AIService aiService) : IPlanStepExecutor
{
    // Not null-guarded on purpose. TaskPlannerService has always accepted a null AIService, and
    // several tests rely on it to exercise RequiresPlanning without standing up a model. Throwing
    // here would turn that into a constructor failure — a behavior change this phase must not make.
    // A null service still fails at the point of use, exactly as it did before.
    private readonly AIService _aiService = aiService;

    public Task<string> ExecuteStepAsync(
        string stepInstruction,
        List<string> previousResults,
        CancellationToken cancellationToken = default)
        => _aiService.ExecutePlanStepAsync(stepInstruction, previousResults, cancellationToken);

    public Task WaitForQuiescenceAsync(TimeSpan timeout)
        => _aiService.CompletionTracker.WaitForAllCompletionsAsync(timeout);

    public Task<string> ExecuteAttemptAsync(TaskStep step, List<string> previousResults,
        Func<string, Task> activity, CancellationToken cancellationToken = default)
        => _aiService.ExecutePlanAttemptAsync(step, previousResults, activity, cancellationToken);
}
