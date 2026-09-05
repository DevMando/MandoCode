using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>Execution is never repeated to recover from a verifier transport or format failure.</summary>
public static class PlanStepRecovery
{
    public static async Task<string> RunAsync(
        TaskStep step,
        Func<string, CancellationToken, Task<PlanStepEvidence>> execute,
        Func<PlanStepEvidence, CancellationToken, Task<PlanVerificationResult>> verify,
        Func<string, Task> activity,
        CancellationToken ct = default)
    {
        if (!step.VerificationPending || step.Evidence?.Instruction != step.Instruction)
        {
            var repair = !string.IsNullOrWhiteSpace(step.ErrorMessage);
            await activity(repair ? $"Repairing step {step.StepNumber}: {step.ErrorMessage}" : $"Executing step {step.StepNumber}");
            var instruction = step.Instruction;
            if (repair)
            {
                instruction += "\n\nTargeted repair of the previous attempt. Preserve working code and acceptance tests. " +
                    "Fix only the blocker below, then rerun the same acceptance checks after your final relevant edit. " +
                    "Do not weaken or delete checks to obtain a pass.\nFailure diagnosis:\n" + step.ErrorMessage;
                if (step.Evidence != null)
                    instruction += "\nPrevious attempt's observed tool results (historical, not current proof):\n" + step.Evidence.ToolEvidence;
            }
            step.VerificationPending = false;
            var previous = step.Evidence;
            var current = (await execute(instruction, ct)) with { Instruction = step.Instruction };
            step.Evidence = MergeEvidence(previous, current);
            step.VerificationPending = true;
        }

        var evidence = step.Evidence!;
        // The host persists evidence when this activity is raised, before any verifier call.
        await activity($"Verifying step {step.StepNumber}");
        PlanVerificationResult result;
        var failure = evidence.FreshnessFailure ?? evidence.ReportedFailure;
        if (failure != null)
            result = new(PlanVerificationStatus.Failed, failure);
        else if (string.IsNullOrWhiteSpace(evidence.ToolEvidence))
            result = new(PlanVerificationStatus.Failed, "No tool evidence was captured. Inspect the deliverable and run its acceptance checks.");
        else
            result = await verify(evidence, ct);

        if (result.Status == PlanVerificationStatus.Unavailable)
            throw new PlanVerificationUnavailableException(result.Reason);

        step.VerificationPending = false;
        if (result.Status == PlanVerificationStatus.Failed)
        {
            step.ErrorMessage = result.Reason;
            throw new PlanStepReportedFailureException(result.Reason);
        }
        step.ErrorMessage = null;
        return evidence.Response;
    }

    /// <summary>Keep established observations when the host confirms their files are unchanged.</summary>
    public static PlanStepEvidence MergeEvidence(PlanStepEvidence? previous, PlanStepEvidence current)
    {
        if (previous?.Instruction != current.Instruction || previous.FileVersions is not { Count: > 0 } ||
            current.FileVersions == null || previous.FileVersions.Any(file =>
                !current.FileVersions.TryGetValue(file.Key, out var version) || version != file.Value))
            return current;

        // Earlier failed checks remain visible, in order, so later checks can supersede them.
        // Neither an old failure verdict nor its freshness failure is carried into the new attempt.
        return current with { ToolEvidence = PlanRepositoryContext.Clip(
            "Earlier observations; observed files are unchanged (host verified hashes):\n" + previous.ToolEvidence +
            "\n\nLatest repair observations:\n" + current.ToolEvidence, 24000) };
    }
}

public sealed class PlanVerificationUnavailableException(string message) : Exception(message);
