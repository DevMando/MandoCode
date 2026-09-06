using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class PlanFinalQualityTests
{
    [Theory]
    [InlineData("Inspect the repository. Do not modify files.")]
    [InlineData("Read-only review of the build and update scripts.")]
    [InlineData("Inspect files; never create or modify anything.")]
    public void NegativeInstructionsDoNotAddImplementationPhase(string instruction)
    {
        var plan = new TaskPlan { Steps = [new() { StepNumber = 1, Instruction = instruction }] };
        Assert.Single(PlanFinalQuality.Ensure(plan).Steps);
    }

    [Fact]
    public void PhaseMetadataSurvivesEditedInstructionAndCheckpoint()
    {
        var plan = PlanFinalQuality.Ensure(Implementation());
        plan.Steps[^1].Instruction = "Run focused project tests";
        var state = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(PlanRunState.From(plan, 1, [], [])))!;
        var restored = PlanCheckpointStore.ToPlan(state);
        Assert.True(PlanFinalQuality.IsQualityStep(restored.Steps[^1]));
        Assert.Equal(2, PlanFinalQuality.Ensure(restored).Steps.Count);
    }

    [Fact]
    public async Task HandoffShowsQualityPhaseBeforeApproval()
    {
        var handoff = new PlanHandoff();
        handoff.OnPlanRequested = (plan, _) =>
        {
            Assert.Equal(2, plan.Steps.Count);
            Assert.True(PlanFinalQuality.IsQualityStep(plan.Steps[^1]));
            Assert.All(plan.Steps, s => Assert.Equal(TaskStepStatus.Pending, s.Status));
            return Task.FromResult("reviewed");
        };
        await handoff.ProcessAsync("Build game", [new("Implement game", "Implement movement")]);
    }

    private static TaskPlan Implementation() => new() { OriginalRequest = "Build game", Steps = [new() {
        StepNumber = 1, Description = "Implement game", Instruction = "Implement movement and restart" }] };

    [Fact]
    public void AutomaticallyAddsOneFinalStepAndIsIdempotent()
    {
        var plan = Implementation();
        PlanFinalQuality.Ensure(plan);
        PlanFinalQuality.Ensure(plan);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(2, plan.Steps[1].StepNumber);
        Assert.Contains("Test and repair", plan.Steps[1].Description);
        Assert.Contains("existing relevant tests first", plan.Steps[1].Instruction);
    }

    [Fact]
    public void ReadOnlyInvestigationDoesNotGetImplementationTests()
    {
        var plan = new TaskPlan { Steps = [new() { StepNumber = 1, Instruction = "Inspect the repository and summarize findings" }] };
        PlanFinalQuality.Ensure(plan);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void CheckpointKeepsCompletedImplementationAndPendingQualityPhase()
    {
        var plan = PlanFinalQuality.Ensure(Implementation());
        plan.Steps[0].Status = TaskStepStatus.Completed;
        var state = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(PlanRunState.From(plan, 1, ["game built"], [])))!;
        var restored = PlanCheckpointStore.ToPlan(state);
        PlanFinalQuality.Ensure(restored);
        Assert.Equal(2, restored.Steps.Count);
        Assert.Equal(TaskStepStatus.Completed, restored.Steps[0].Status);
        Assert.Equal(TaskStepStatus.Pending, restored.Steps[1].Status);
        Assert.Equal(plan.Steps[1].Instruction, restored.Steps[1].Instruction);
    }

    [Fact]
    public void ReplanningPreservesFinalQualityPhase()
    {
        var plan = PlanFinalQuality.Ensure(Implementation());
        var revised = PlanRevision.CreateCandidate(plan, 1, new("Build game", [new("Implement better game", "Implement game") ]));
        Assert.Equal(2, revised.Steps.Count);
        Assert.Contains("Test and repair", revised.Steps[^1].Description);
    }

    [Fact]
    public async Task QualityFailureUsesTargetedRecovery()
    {
        var step = PlanFinalQuality.Ensure(Implementation()).Steps[^1];
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "Restart test failed", "exit 1", ReportedFailure: "Restart loses pellets")),
            _ => Task.CompletedTask));
        Assert.Equal("Restart loses pellets", step.ErrorMessage);
        Assert.NotNull(step.Evidence);
        Assert.False(step.VerificationPending);
    }
}
