using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class StraightforwardPlanTests
{
    [Fact]
    public void DefaultAndLegacyConfigsUseStraightforwardModeAndStrictIsOptIn()
    {
        var config = JsonSerializer.Deserialize<MandoCodeConfig>("{}")!;
        Assert.False(config.StrictPlanVerification);
        Assert.True(ConfigKeySetter.TrySet(config, "strictPlanVerification", "true").Ok);
        Assert.True(JsonSerializer.Deserialize<MandoCodeConfig>(JsonSerializer.Serialize(config))!.StrictPlanVerification);
        Assert.False(ConfigKeySetter.TrySet(config, "strictPlanVerification", "maybe").Ok);
        Assert.True(config.StrictPlanVerification);
        Assert.True(ConfigKeySetter.TrySet(config, "strictPlanVerification", "false").Ok);
        Assert.False(config.StrictPlanVerification);
    }

    [Fact]
    public async Task NormalReturnAdvancesWithoutIndependentVerifierOrEvidenceFollowup()
    {
        var step = new TaskStep { Instruction = "Implement task" };
        var result = await PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "Done", "", FreshnessFailure: "No independent proof")),
            (_, _) => throw new Exception("Verifier must not run"), _ => Task.CompletedTask,
            gatherEvidence: (_, _) => throw new Exception("Followup must not run"), strictVerification: false);
        Assert.Equal("Done", result);
        Assert.NotNull(step.Evidence);
        Assert.False(step.VerificationPending);
    }

    [Fact]
    public async Task ReportedFailureEntersTargetedRepairAndPreservesPriorEvidence()
    {
        var step = new TaskStep { Instruction = "Implement task" };
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "Test failed", "test exit 1", ReportedFailure: "Collision broken")),
            (_, _) => throw new Exception("Verifier must not run"), _ => Task.CompletedTask, strictVerification: false));
        Assert.Equal("Collision broken", step.ErrorMessage);
        Assert.False(step.VerificationPending);
        await PlanStepRecovery.RunAsync(step, (instruction, _) =>
        {
            Assert.Contains("Targeted repair", instruction);
            Assert.Contains("Collision broken", instruction);
            Assert.Contains("test exit 1", instruction);
            return Task.FromResult(new PlanStepEvidence(instruction, "Fixed", "test exit 0"));
        }, (_, _) => throw new Exception("Verifier must not run"), _ => Task.CompletedTask, strictVerification: false);
        Assert.Null(step.ErrorMessage);
    }

    [Fact]
    public async Task PendingVerificationCheckpointResumesWithoutRepeatingImplementation()
    {
        var plan = new TaskPlan { Steps = [new TaskStep { Instruction = "task", VerificationPending = true,
            Evidence = new PlanStepEvidence("task", "Saved work", "check passed") }] };
        var state = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(PlanRunState.From(plan, 0, [], [])))!;
        var step = PlanCheckpointStore.ToPlan(state).Steps[0];
        var result = await PlanStepRecovery.RunAsync(step,
            (_, _) => throw new Exception("Implementation must not repeat"),
            (_, _) => throw new Exception("Verifier must not run"), _ => Task.CompletedTask, strictVerification: false);
        Assert.Equal("Saved work", result);
        Assert.False(step.VerificationPending);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeSuccessfulCompletion()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanStepRecovery.RunAsync(new() { Instruction = "task" },
            (_, ct) => Task.FromCanceled<PlanStepEvidence>(ct), (_, _) => throw new Exception("Verifier must not run"),
            _ => Task.CompletedTask, cts.Token, strictVerification: false));
    }
}
