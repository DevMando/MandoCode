using System.Text.Json;
using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PlanAcceptanceTests
{
    [Fact]
    public void CriteriaSurviveProposalCheckpointAndRevision()
    {
        var steps = TaskPlannerService.FromProposals([new("Movement", "Implement movement", ["Position changes on input", "Walls block movement"])]);
        var plan = new TaskPlan { Steps = steps };
        var state = JsonSerializer.Deserialize<PlanRunState>(JsonSerializer.Serialize(PlanRunState.From(plan, 0, [], [])))!;
        var restored = PlanCheckpointStore.ToPlan(state);
        Assert.Equal(steps[0].AcceptanceCriteria, restored.Steps[0].AcceptanceCriteria);
        var candidate = PlanRevision.CreateFollowingCandidate(restored, 1, new("goal", [new("Next", "Next task", ["Next check"])]));
        Assert.Equal(steps[0].AcceptanceCriteria, candidate.Steps[0].AcceptanceCriteria);
        Assert.Equal("Next check", candidate.Steps[1].AcceptanceCriteria.Single());
    }

    [Fact]
    public async Task ExistingWorkUsesSameCriteriaForExecutorAndVerifier()
    {
        var step = new TaskStep { Instruction = "Add input", AcceptanceCriteria = ["WASD maps to four directions"] };
        string? instructionSeen = null;
        await PlanStepRecovery.RunAsync(step, (instruction, _) =>
        {
            instructionSeen = instruction;
            return Task.FromResult(new PlanStepEvidence(instruction, "Already implemented; no edits", "read full KEYMAP; input check passed"));
        }, (evidence, _) =>
        {
            Assert.Equal(step.AcceptanceCriteria, evidence.AcceptanceCriteria);
            return Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Passed, "Existing input satisfies the requirement"));
        }, _ => Task.CompletedTask);
        Assert.Contains("WASD maps to four directions", instructionSeen);
        Assert.Contains("without rewriting files", instructionSeen);
    }

    [Fact]
    public async Task EvidenceGapGetsExactlyOneFollowupWithoutRerunningImplementation()
    {
        var step = new TaskStep { Instruction = "Add input" };
        var executions = 0;
        var followups = 0;
        var verifications = 0;
        var result = await PlanStepRecovery.RunAsync(step, (instruction, _) =>
        {
            executions++;
            return Task.FromResult(new PlanStepEvidence(instruction, "Done", "partial read"));
        }, (_, _) => Task.FromResult(++verifications == 1
            ? new PlanVerificationResult(PlanVerificationStatus.Failed, "Read final KEYMAP", true)
            : new PlanVerificationResult(PlanVerificationStatus.Passed, "All keys confirmed")), _ => Task.CompletedTask,
            gatherEvidence: (instruction, _) =>
            {
                followups++;
                return Task.FromResult(new PlanStepEvidence(instruction, "Confirmed", "full KEYMAP"));
            });
        Assert.Equal("Confirmed", result);
        Assert.Equal(1, executions);
        Assert.Equal(1, followups);
        Assert.True(step.EvidenceFollowupUsed);
    }

    [Fact]
    public async Task RepeatedGapPausesForUserInsteadOfAutomaticLoop()
    {
        var step = new TaskStep { Instruction = "task" };
        var followups = 0;
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(step,
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "done", "evidence")),
            (_, _) => Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Failed, "Need more evidence", true)),
            _ => Task.CompletedTask, gatherEvidence: (instruction, _) =>
            {
                followups++;
                return Task.FromResult(new PlanStepEvidence(instruction, "partial", "evidence"));
            }));
        Assert.Equal(1, followups);
        var state = PlanRunState.From(new TaskPlan { Steps = [step] }, 0, [], []);
        Assert.True(PlanCheckpointStore.ToPlan(state).Steps[0].EvidenceFollowupUsed);
    }

    [Fact]
    public async Task BrokenBehaviorDoesNotTriggerAutomaticFollowup()
    {
        var followups = 0;
        await Assert.ThrowsAsync<PlanStepReportedFailureException>(() => PlanStepRecovery.RunAsync(new() { Instruction = "task" },
            (instruction, _) => Task.FromResult(new PlanStepEvidence(instruction, "done", "test failed")),
            (_, _) => Task.FromResult(new PlanVerificationResult(PlanVerificationStatus.Failed, "Ghosts do not move")),
            _ => Task.CompletedTask, gatherEvidence: (instruction, _) =>
            {
                followups++;
                return Task.FromResult(new PlanStepEvidence(instruction, "", ""));
            }));
        Assert.Equal(0, followups);
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("propose_plan")]
    [InlineData("mcp_unknown_tool")]
    public async Task EvidenceScopeBlocksChangesAtMiddleware(string name)
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        using var scope = middleware.BeginScope();
        scope.EvidenceOnly = true;
        var fn = AIFunctionFactory.Create(() => { invoked = true; return "changed"; }, name);
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments());
        Assert.False(invoked);
    }

    [Theory]
    [InlineData("node acceptance_test.js", true)]
    [InlineData("node --check script.js", true)]
    [InlineData("node -e writeFiles()", false)]
    [InlineData("node acceptance_test.js && del script.js", false)]
    [InlineData("npm install", false)]
    public void FollowupOnlyRerunsRecognizedPreviouslyObservedChecks(string command, bool allowed)
    {
        Assert.Equal(allowed, PlanEvidenceFollowup.Allows("execute_command",
            new Dictionary<string, object?> { ["command"] = command }, new HashSet<string> { command }));
        Assert.False(PlanEvidenceFollowup.Allows("execute_command",
            new Dictionary<string, object?> { ["command"] = command }, new HashSet<string>()));
    }

    [Fact]
    public void AmbiguousEditProvidesActualMatchLocationsImmediately()
    {
        var hint = AmbiguousEditGuidance.Describe("first\nrepeat\nsecond\nrepeat\nlast", "repeat");
        Assert.Contains("Match at line 2", hint);
        Assert.Contains("Match at line 4", hint);
        Assert.Contains("first", hint);
        Assert.Contains("last", hint);
        Assert.Contains("Do not repeat", hint);
    }
}
