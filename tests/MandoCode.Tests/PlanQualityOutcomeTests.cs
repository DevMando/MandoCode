using System.Text.Json;
using Microsoft.Extensions.AI;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class PlanQualityOutcomeTests
{
    [Fact]
    public async Task StrictReadOnlyFollowupPreservesCompletedQualityOutcome()
    {
        var calls = 0;
        var result = await PlanStepRecovery.RunAsync(Quality(),
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "Passed tests", "test exit 0", ExplicitSuccess: true)),
            (_, _) => Task.FromResult(++calls == 1
                ? new PlanVerificationResult(PlanVerificationStatus.Failed, "Read HUD code", true)
                : new PlanVerificationResult(PlanVerificationStatus.Passed, "Confirmed")),
            _ => Task.CompletedTask, gatherEvidence: (instruction, _) =>
                Task.FromResult(new PlanStepEvidence(instruction, "HUD inspected", "read HUD")));
        Assert.Equal("HUD inspected", result);
        Assert.Equal(2, calls);
    }

    private static TaskStep Quality() => PlanFinalQuality.Ensure(new TaskPlan { Steps =
        [new TaskStep { StepNumber = 1, Instruction = "Implement game" }] }).Steps[^1];

    [Fact]
    public async Task UnfinishedQualityResponseCannotCompleteButNormalStepsStillCan()
    {
        async Task<string> Run(TaskStep step) => await PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "A defect exists. I'll investigate.", "read file")),
            (_, _) => throw new Exception("No verifier"), _ => Task.CompletedTask, strictVerification: false);
        var quality = Quality();
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => Run(quality));
        Assert.NotNull(quality.Evidence);
        Assert.Contains("explicit successful outcome", quality.ErrorMessage);
        Assert.False(quality.VerificationPending);
        Assert.NotEmpty(await Run(new TaskStep { Instruction = "Implement game" }));
    }

    [Fact]
    public async Task ExplicitQualitySuccessCompletesWithoutVerifier()
    {
        var result = await PlanStepRecovery.RunAsync(Quality(),
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "28 checks passed", "test exit 0", ExplicitSuccess: true)),
            (_, _) => throw new Exception("No verifier"), _ => Task.CompletedTask, strictVerification: false);
        Assert.Equal("28 checks passed", result);
    }

    [Fact]
    public void FailedCheckSurvivesCheckpointAndUnrelatedSuccessUntilSameCheckPasses()
    {
        var previous = new PlanStepEvidence("quality", "failed", "", TestExitCodes:
            new Dictionary<string, int> { ["node verify_maze.js"] = 1 });
        previous = JsonSerializer.Deserialize<PlanStepEvidence>(JsonSerializer.Serialize(previous))!;
        var unrelated = new PlanStepEvidence("quality", "SUCCESS", "", ExplicitSuccess: true,
            TestExitCodes: new Dictionary<string, int> { ["node --check game.js"] = 0 });
        var merged = PlanQualityOutcome.Merge(previous, unrelated);
        Assert.Contains("verify_maze.js", PlanQualityOutcome.Failure(merged));
        var passing = unrelated with { TestExitCodes = new Dictionary<string, int> { ["node verify_maze.js"] = 0 } };
        Assert.Null(PlanQualityOutcome.Failure(PlanQualityOutcome.Merge(merged, passing)));
    }

    [Fact]
    public async Task FailedTestOverridesExplicitSuccessInRecovery()
    {
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(Quality(),
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "Everything passed", "", ExplicitSuccess: true,
                TestExitCodes: new Dictionary<string, int> { ["node smoke-test.js"] = 1 })),
            (_, _) => throw new Exception("No verifier"), _ => Task.CompletedTask, strictVerification: false));
    }

    [Theory]
    [InlineData("node verify_maze.js", "Exit code: 1\nBad width", true)]
    [InlineData("node --check game.js && node verify_maze.js", "Exit code: 1\nBad width", true)]
    [InlineData("node smoke-test.js", "Exit code: 0\nFailed appears in a test label", true)]
    [InlineData("node smoke-test.js", "28 passed, 0 failed", false)]
    [InlineData("node --version", "Exit code: 1\nnot installed", false)]
    public void CapturesOnlyIdentifiedChecksWithActualExitStatus(string command, string output, bool tracked)
    {
        ChatMessage[] messages = [new(ChatRole.Assistant, [new FunctionCallContent("1", "execute_command",
            new Dictionary<string, object?> { ["command"] = command })]),
            new(ChatRole.Tool, [new FunctionResultContent("1", output)])];
        Assert.Equal(tracked, PlanQualityOutcome.CaptureTests(messages).ContainsKey(command));
    }

    [Fact]
    public void ExistingFinalTestPhaseIsConsolidatedAndChecksPreserved()
    {
        var plan = new TaskPlan { Steps = [new() { StepNumber = 1, Instruction = "Implement Asteroids" },
            new() { StepNumber = 2, Description = "Test and repair the finished result", Instruction = "Exercise ship controls",
                AcceptanceCriteria = ["Ship thrust works"] }] };
        PlanFinalQuality.Ensure(plan);
        PlanFinalQuality.Ensure(plan);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains("Exercise ship controls", plan.Steps[^1].Instruction);
        Assert.Contains("Ship thrust works", plan.Steps[^1].AcceptanceCriteria);
        Assert.True(PlanFinalQuality.IsQualityStep(plan.Steps[^1]));
    }
}
