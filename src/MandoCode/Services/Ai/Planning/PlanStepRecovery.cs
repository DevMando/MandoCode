using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// The executor owns its acceptance checks; completion is not gated on a second model.
/// Only mechanical signals derived from tool history can fail a step here.
/// </summary>
public static class PlanStepRecovery
{
    public static async Task<string> RunAsync(
        TaskStep step,
        Func<string, CancellationToken, Task<PlanStepEvidence>> execute,
        Func<string, Task> activity,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var criteria = PlanAcceptance.Normalize(step.AcceptanceCriteria, step.Instruction).ToArray();
        if (!step.VerificationPending || step.Evidence?.Instruction != step.Instruction ||
            !(step.Evidence.AcceptanceCriteria ?? [step.Instruction]).SequenceEqual(criteria))
        {
            var repair = !string.IsNullOrWhiteSpace(step.ErrorMessage);
            await activity(repair ? $"Repairing step {step.StepNumber}: {step.ErrorMessage}" : $"Executing step {step.StepNumber}");
            var instruction = step.Instruction + "\n\n" + PlanAcceptance.Describe(step) +
                "\nFirst inspect existing work against every acceptance check. Work produced in earlier steps " +
                "counts when it satisfies this requirement. Preserve it and implement only missing behavior. " +
                "If everything already exists, validate it without rewriting files. Do not add new acceptance requirements.";
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
            if (PlanFinalQuality.IsQualityStep(step) && previous?.TestExitCodes?.Any(e => e.Value != 0) == true)
                instruction += "\nUnresolved checks to repair and rerun:\n" + PlanQualityOutcome.Failure(previous);
            var current = (await execute(instruction, ct)) with { Instruction = step.Instruction, AcceptanceCriteria = criteria };
            if (PlanFinalQuality.IsQualityStep(step)) current = PlanQualityOutcome.Merge(previous, current);
            step.Evidence = MergeEvidence(previous, current);
            step.VerificationPending = true;
        }

        var evidence = step.Evidence!;
        ct.ThrowIfCancellationRequested();

        // Save before advancing, including when resuming a previously pending attempt.
        await activity($"Saving step {step.StepNumber} result");
        step.VerificationPending = false;

        if (Failure(step, evidence) is string failure)
        {
            step.ErrorMessage = failure;
            throw new PlanStepReportedFailureException(failure);
        }
        step.ErrorMessage = null;
        return evidence.Response;
    }

    /// <summary>
    /// The step's mechanical completion gates, in order of how actionable the repair instruction is.
    /// Every signal comes from the executor's own report or from observed tool history — never from
    /// a judgement about whether the work was any good.
    /// </summary>
    private static string? Failure(TaskStep step, PlanStepEvidence evidence)
    {
        // The final phase carries the stricter rule: unresolved nonzero test exit codes.
        if (PlanFinalQuality.IsQualityStep(step) && PlanQualityOutcome.Failure(evidence) is string quality)
            return quality;

        if (!string.IsNullOrWhiteSpace(evidence.ReportedFailure))
            return evidence.FreshnessFailure == null
                ? evidence.ReportedFailure
                : evidence.ReportedFailure + "\nAlso rerun the acceptance checks after your final edit: " + evidence.FreshnessFailure;

        // A pass recorded before the last relevant edit does not describe the current files.
        // Derived from tool ordering, so a confident report cannot talk its way past it.
        if (evidence.FreshnessFailure != null) return evidence.FreshnessFailure;

        if (string.IsNullOrWhiteSpace(evidence.ToolEvidence))
            return "No tool evidence was captured. Inspect the deliverable and run its acceptance checks.";

        return null;
    }

    /// <summary>Keep established observations when the host confirms their files are unchanged.</summary>
    public static PlanStepEvidence MergeEvidence(PlanStepEvidence? previous, PlanStepEvidence current)
    {
        if (previous?.Instruction != current.Instruction ||
            !(previous.AcceptanceCriteria ?? []).SequenceEqual(current.AcceptanceCriteria ?? []) ||
            previous.FileVersions is not { Count: > 0 } ||
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
