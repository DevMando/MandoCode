using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>Pure plan-suffix replacement shared by Desktop and CLI replan flows.</summary>
public static class PlanRevision
{
    public static TaskPlan CreateCandidate(TaskPlan current, int failedStepNumber, GeneratedPlan revision)
    {
        var failedIndex = current.Steps.FindIndex(step => step.StepNumber == failedStepNumber);
        if (failedIndex < 0) throw new ArgumentOutOfRangeException(nameof(failedStepNumber));

        var prefix = current.Steps.Take(failedIndex).Select(Clone).ToList();
        var replacement = TaskPlannerService.FromProposals(revision.Steps);
        for (var i = 0; i < replacement.Count; i++) replacement[i].StepNumber = failedIndex + i + 1;

        return new TaskPlan
        {
            OriginalRequest = current.OriginalRequest,
            Status = TaskPlanStatus.Pending,
            Steps = [.. prefix, .. replacement]
        };
    }

    /// <summary>Builds a full review candidate while preserving every step through the edited one.</summary>
    public static TaskPlan CreateFollowingCandidate(TaskPlan current, int editedStepNumber, GeneratedPlan revision)
    {
        var editedIndex = current.Steps.FindIndex(step => step.StepNumber == editedStepNumber);
        if (editedIndex < 0) throw new ArgumentOutOfRangeException(nameof(editedStepNumber));

        var prefix = current.Steps.Take(editedIndex + 1).Select(Clone).ToList();
        var replacement = TaskPlannerService.FromProposals(revision.Steps);
        for (var i = 0; i < replacement.Count; i++) replacement[i].StepNumber = editedIndex + i + 2;

        return new TaskPlan
        {
            OriginalRequest = current.OriginalRequest,
            Status = TaskPlanStatus.Pending,
            Steps = [.. prefix, .. replacement]
        };
    }

    /// <summary>
    /// Applies an approved candidate while preserving the failed step object's identity. The
    /// workflow triage node still holds that object across the UI await and reads its Pending
    /// status as the signal to dispatch the same cursor again.
    /// </summary>
    public static void ApplyApproved(TaskPlan current, int failedStepNumber, TaskPlan candidate)
    {
        var failedIndex = current.Steps.FindIndex(step => step.StepNumber == failedStepNumber);
        if (failedIndex < 0 || candidate.Steps.Count <= failedIndex)
            throw new InvalidOperationException("The revised plan has no replacement for the failed step.");

        var liveFailedStep = current.Steps[failedIndex];
        var firstReplacement = candidate.Steps[failedIndex];
        Copy(firstReplacement, liveFailedStep);
        liveFailedStep.Status = TaskStepStatus.Pending;
        liveFailedStep.Result = null;
        liveFailedStep.ErrorMessage = null;

        current.Steps.RemoveRange(failedIndex + 1, current.Steps.Count - failedIndex - 1);
        foreach (var step in candidate.Steps.Skip(failedIndex + 1))
            current.Steps.Add(Clone(step));
        current.Status = TaskPlanStatus.InProgress;
        current.ExecutionSummary = null;
    }

    /// <summary>Replaces only the steps after an edited, not-yet-executed step.</summary>
    public static void ApplyFollowing(TaskPlan current, int editedStepNumber, TaskPlan candidate)
    {
        var editedIndex = current.Steps.FindIndex(step => step.StepNumber == editedStepNumber);
        if (editedIndex < 0) throw new ArgumentOutOfRangeException(nameof(editedStepNumber));

        current.Steps.RemoveRange(editedIndex + 1, current.Steps.Count - editedIndex - 1);
        foreach (var step in candidate.Steps.Skip(editedIndex + 1))
            current.Steps.Add(Clone(step));
    }

    private static TaskStep Clone(TaskStep source)
    {
        var clone = new TaskStep();
        Copy(source, clone);
        return clone;
    }

    private static void Copy(TaskStep source, TaskStep target)
    {
        target.StepNumber = source.StepNumber;
        target.Description = source.Description;
        target.Instruction = source.Instruction;
        target.Status = source.Status;
        target.Result = source.Result;
        target.ErrorMessage = source.ErrorMessage;
    }
}
