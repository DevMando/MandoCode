using Xunit;
using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for the trimmed TaskPlannerService:
///  - RequiresPlanning is now just two objective signals (3+ numbered items or >400 chars).
///  - FromProposals materialises typed tool-call args into TaskSteps (no text parsing).
/// </summary>
public class TaskPlannerServiceTests
{
    // RequiresPlanning does not touch _aiService, so null is safe here.
    private static TaskPlannerService MakePlanner(bool enabled = true)
    {
        var config = new MandoCodeConfig { EnableTaskPlanning = enabled };
        return new TaskPlannerService(aiService: null!, config);
    }

    // ──────────────────────────────────────────────
    //  FromProposals
    // ──────────────────────────────────────────────

    [Fact]
    public void FromProposals_MapsStepsInOrder()
    {
        var proposals = new[]
        {
            new PlanStepProposal("Scaffold project", "Run dotnet new console"),
            new PlanStepProposal("Add dependency", "Add System.Text.Json package"),
            new PlanStepProposal("Write entry point", "Edit Program.cs")
        };

        var steps = TaskPlannerService.FromProposals(proposals);

        Assert.Equal(3, steps.Count);
        Assert.Equal(1, steps[0].StepNumber);
        Assert.Equal(2, steps[1].StepNumber);
        Assert.Equal(3, steps[2].StepNumber);
        Assert.Equal("Scaffold project", steps[0].Description);
        Assert.Equal("Run dotnet new console", steps[0].Instruction);
        Assert.All(steps, s => Assert.Equal(TaskStepStatus.Pending, s.Status));
    }

    [Fact]
    public void FromProposals_TruncatesLongDescriptions()
    {
        var longDesc = new string('x', 120);
        var proposals = new[]
        {
            new PlanStepProposal(longDesc, "instruction body"),
        };

        var steps = TaskPlannerService.FromProposals(proposals);

        Assert.Single(steps);
        Assert.Equal(60, steps[0].Description.Length);
        Assert.EndsWith("...", steps[0].Description);
        // Instruction is NOT truncated — only the UI description is.
        Assert.Equal("instruction body", steps[0].Instruction);
    }

    [Fact]
    public void FromProposals_NullInput_ReturnsEmptyList()
    {
        var steps = TaskPlannerService.FromProposals(null!);
        Assert.Empty(steps);
    }

    [Fact]
    public void FromProposals_EmptyArray_ReturnsEmptyList()
    {
        var steps = TaskPlannerService.FromProposals(Array.Empty<PlanStepProposal>());
        Assert.Empty(steps);
    }

    [Fact]
    public void FromProposals_DropsFullyEmptyProposals()
    {
        // Simulates a casing mismatch where SK deserializes 3 items but every
        // string field comes through empty. We must not produce 3 empty TaskSteps.
        var proposals = new[]
        {
            new PlanStepProposal("", ""),
            new PlanStepProposal("", ""),
            new PlanStepProposal("", ""),
        };

        var steps = TaskPlannerService.FromProposals(proposals);

        Assert.Empty(steps);
    }

    [Fact]
    public void FromProposals_OnlyInstruction_PopulatesDescriptionToo()
    {
        var proposals = new[]
        {
            new PlanStepProposal("", "Run dotnet restore and wait"),
        };

        var steps = TaskPlannerService.FromProposals(proposals);

        Assert.Single(steps);
        Assert.False(string.IsNullOrWhiteSpace(steps[0].Description));
        Assert.Equal("Run dotnet restore and wait", steps[0].Instruction);
    }

    // ──────────────────────────────────────────────
    //  RequiresPlanning
    // ──────────────────────────────────────────────

    [Fact]
    public void RequiresPlanning_NumberedList_ReturnsTrue()
    {
        var planner = MakePlanner();
        var message = "1. Scaffold project\n2. Add tests\n3. Wire CI";

        Assert.True(planner.RequiresPlanning(message));
    }

    [Fact]
    public void RequiresPlanning_TwoNumberedItems_ReturnsFalse()
    {
        // Only 2 items: defer to the model rather than short-circuit.
        var planner = MakePlanner();
        var message = "1. Do this\n2. Do that";

        Assert.False(planner.RequiresPlanning(message));
    }

    [Fact]
    public void RequiresPlanning_LongMessage_ReturnsTrue()
    {
        var planner = MakePlanner();
        var message = new string('a', 401);

        Assert.True(planner.RequiresPlanning(message));
    }

    [Fact]
    public void RequiresPlanning_ShortImperative_ReturnsFalse()
    {
        // The old heuristic would have fired here; the trimmed one must not.
        // Defer to the model — which will call propose_plan if it actually needs to.
        var planner = MakePlanner();

        Assert.False(planner.RequiresPlanning("create a function that does X"));
    }

    [Fact]
    public void RequiresPlanning_Question_ReturnsFalse()
    {
        var planner = MakePlanner();

        Assert.False(planner.RequiresPlanning("how does X work?"));
    }

    [Fact]
    public void RequiresPlanning_Empty_ReturnsFalse()
    {
        var planner = MakePlanner();

        Assert.False(planner.RequiresPlanning(""));
        Assert.False(planner.RequiresPlanning("   "));
    }

    [Fact]
    public void RequiresPlanning_Disabled_ReturnsFalse()
    {
        var planner = MakePlanner(enabled: false);
        var message = new string('a', 500); // Would trigger if enabled.

        Assert.False(planner.RequiresPlanning(message));
    }
}
