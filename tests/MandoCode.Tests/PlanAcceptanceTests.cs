using System.Text.Json;
using MandoCode.Models;
using MandoCode.Services;
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
    public async Task ExistingWorkIsValidatedAgainstTheAgreedCriteriaRatherThanRewritten()
    {
        var step = new TaskStep { Instruction = "Add input", AcceptanceCriteria = ["WASD maps to four directions"] };
        string? instructionSeen = null;
        await PlanStepRecovery.RunAsync(step, (instruction, _) =>
        {
            instructionSeen = instruction;
            return Task.FromResult(new PlanStepEvidence(instruction, "Already implemented; no edits", "read full KEYMAP; input check passed"));
        }, _ => Task.CompletedTask);
        Assert.Contains("WASD maps to four directions", instructionSeen);
        Assert.Contains("without rewriting files", instructionSeen);
        Assert.Equal(step.AcceptanceCriteria, step.Evidence!.AcceptanceCriteria);
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
