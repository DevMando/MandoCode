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
        CancellationToken ct = default,
        Func<string, CancellationToken, Task<PlanStepEvidence>>? gatherEvidence = null,
        bool strictVerification = true)
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
        if (PlanFinalQuality.IsQualityStep(step) && PlanQualityOutcome.Failure(evidence) is string qualityFailure)
        {
            await activity($"Saving incomplete quality phase {step.StepNumber}");
            step.VerificationPending = false;
            step.ErrorMessage = qualityFailure;
            throw new PlanStepReportedFailureException(qualityFailure);
        }
        if (!strictVerification)
        {
            // Save before advancing, including when resuming a previously pending verification.
            // The executor owns acceptance checks; a second model is not a completion gate.
            await activity($"Saving step {step.StepNumber} result");
            step.VerificationPending = false;
            if (!string.IsNullOrWhiteSpace(evidence.ReportedFailure))
            {
                step.ErrorMessage = evidence.ReportedFailure;
                throw new PlanStepReportedFailureException(evidence.ReportedFailure);
            }
            step.ErrorMessage = null;
            return evidence.Response;
        }
        // The host persists evidence when this activity is raised, before any verifier call.
        await activity($"Verifying step {step.StepNumber}");
        PlanVerificationResult result;
        var failure = evidence.FreshnessFailure ?? evidence.ReportedFailure;
        if (failure != null)
            result = new(PlanVerificationStatus.Failed, failure,
                evidence.FreshnessFailure != null && evidence.CheckCommands is { Length: > 0 });
        else if (string.IsNullOrWhiteSpace(evidence.ToolEvidence))
            result = new(PlanVerificationStatus.Failed, "No tool evidence was captured. Inspect the deliverable and run its acceptance checks.");
        else
            result = await verify(evidence, ct);

        if (result.Status == PlanVerificationStatus.Unavailable)
            throw new PlanVerificationUnavailableException(result.Reason);

        if (result.Status == PlanVerificationStatus.Failed && result.CanGatherEvidence &&
            !step.EvidenceFollowupUsed && gatherEvidence != null)
        {
            step.EvidenceFollowupUsed = true;
            await activity($"Checking missing evidence for step {step.StepNumber} (one follow-up)");
            var followup = (await gatherEvidence(step.Instruction + "\n\n" + PlanAcceptance.Describe(step) +
                "\nCollect only the missing evidence below. Do not change source, tests, dependencies, or scope. " +
                "Use file reads or a previously observed check command. If changes or a user decision are needed, " +
                "report the blocker instead of acting.\nMissing evidence:\n" + result.Reason, ct))
                with { Instruction = step.Instruction, AcceptanceCriteria = criteria };
            if (PlanFinalQuality.IsQualityStep(step))
            {
                // Read-only verification follow-ups do not replace the executor's completed outcome.
                followup = PlanQualityOutcome.Merge(evidence, followup) with
                { ExplicitSuccess = followup.ExplicitSuccess ?? evidence.ExplicitSuccess };
            }
            step.Evidence = MergeEvidence(evidence, followup);
            step.VerificationPending = true;
            return await RunAsync(step, execute, verify, activity, ct, gatherEvidence, strictVerification);
        }

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
        if (previous?.Instruction != current.Instruction ||
            !(previous.AcceptanceCriteria ?? []).SequenceEqual(current.AcceptanceCriteria ?? []) ||
            previous.FileVersions is not { Count: > 0 } ||
            current.FileVersions == null || previous.FileVersions.Any(file =>
                !current.FileVersions.TryGetValue(file.Key, out var version) || version != file.Value))
            return current;

        // Earlier failed checks remain visible, in order, so later checks can supersede them.
        // Neither an old failure verdict nor its freshness failure is carried into the new attempt.
        return current with { CheckCommands = (previous.CheckCommands ?? []).Concat(current.CheckCommands ?? []).Distinct().ToArray(),
            ToolEvidence = PlanRepositoryContext.Clip(
            "Earlier observations; observed files are unchanged (host verified hashes):\n" + previous.ToolEvidence +
            "\n\nLatest repair observations:\n" + current.ToolEvidence, 24000) };
    }
}

public sealed class PlanVerificationUnavailableException(string message) : Exception(message);
