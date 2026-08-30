using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public sealed class PlanRevisionTests
{
    [Fact]
    public void ApplyApproved_PreservesSettledPrefixAndFailedStepIdentity()
    {
        var plan = new TaskPlan
        {
            OriginalRequest = "ship it",
            Status = TaskPlanStatus.InProgress,
            Steps =
            [
                Step(1, "done", TaskStepStatus.Completed, "result"),
                Step(2, "failed", TaskStepStatus.Failed),
                Step(3, "obsolete", TaskStepStatus.Pending)
            ]
        };
        var liveFailed = plan.Steps[1];
        var revision = new GeneratedPlan("revised", [
            new PlanStepProposal("repair", "repair carefully"),
            new PlanStepProposal("verify", "run focused tests")
        ]);

        var candidate = PlanRevision.CreateCandidate(plan, 2, revision);
        PlanRevision.ApplyApproved(plan, 2, candidate);

        Assert.Same(liveFailed, plan.Steps[1]);
        Assert.Equal(TaskStepStatus.Completed, plan.Steps[0].Status);
        Assert.Equal("result", plan.Steps[0].Result);
        Assert.Equal("repair", plan.Steps[1].Description);
        Assert.Equal(TaskStepStatus.Pending, plan.Steps[1].Status);
        Assert.Equal("verify", plan.Steps[2].Description);
        Assert.Equal([1, 2, 3], plan.Steps.Select(step => step.StepNumber));
    }

    [Fact]
    public void ApplyFollowing_PreservesEditedPrefixAndReplacesOnlyDependentSuffix()
    {
        var plan = new TaskPlan
        {
            OriginalRequest = "create then verify",
            Steps =
            [
                Step(1, "create beta", TaskStepStatus.Pending),
                Step(2, "verify alpha", TaskStepStatus.Pending),
                Step(3, "report alpha", TaskStepStatus.Pending)
            ]
        };
        var edited = plan.Steps[0];
        var revision = new GeneratedPlan("updated", [
            new PlanStepProposal("verify beta", "read and verify beta"),
            new PlanStepProposal("report beta", "report the beta result")
        ]);

        var candidate = PlanRevision.CreateFollowingCandidate(plan, 1, revision);
        PlanRevision.ApplyFollowing(plan, 1, candidate);

        Assert.Same(edited, plan.Steps[0]);
        Assert.Equal(["create beta", "verify beta", "report beta"],
            plan.Steps.Select(step => step.Description));
        Assert.Equal([1, 2, 3], plan.Steps.Select(step => step.StepNumber));
    }

    private static TaskStep Step(int number, string description, TaskStepStatus status, string? result = null) => new()
    {
        StepNumber = number,
        Description = description,
        Instruction = description + " instruction",
        Status = status,
        Result = result
    };
}
