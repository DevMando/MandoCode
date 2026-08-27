using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// An <see cref="IPlanStepExecutor"/> that returns canned results and records what it was asked to
/// do. This is the seam that lets plan sequencing, cancellation, skip/fail handling and (later)
/// checkpoint/resume be tested deterministically, with no Ollama and no HTTP.
/// </summary>
public sealed class ScriptedPlanStepExecutor : IPlanStepExecutor
{
    private readonly Func<string, int, string> _respond;

    /// <param name="respond">
    /// Maps (instruction, zero-based call index) to the step's result. Throw from here to simulate
    /// a step failing.
    /// </param>
    public ScriptedPlanStepExecutor(Func<string, int, string>? respond = null)
        => _respond = respond ?? ((instruction, i) => $"done:{i}:{instruction}");

    /// <summary>Instructions received, in the order they were executed.</summary>
    public List<string> Executed { get; } = [];

    /// <summary>Snapshot of previousResults as each step saw it — proves context carry-forward.</summary>
    public List<IReadOnlyList<string>> PreviousResultsSeen { get; } = [];

    public int QuiescenceWaits { get; private set; }

    public Task<string> ExecuteStepAsync(
        string stepInstruction,
        List<string> previousResults,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var index = Executed.Count;
        Executed.Add(stepInstruction);
        PreviousResultsSeen.Add([.. previousResults]);

        return Task.FromResult(_respond(stepInstruction, index));
    }

    public Task WaitForQuiescenceAsync(TimeSpan timeout)
    {
        QuiescenceWaits++;
        return Task.CompletedTask;
    }
}
