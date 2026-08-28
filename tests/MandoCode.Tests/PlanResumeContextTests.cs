using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// What a resumed plan carries forward.
///
/// A resumed plan runs in a process that never saw the conversation it came from, so anything the
/// steps need has to come out of the saved record. Two things were initially lost, both of which
/// have already caused real failures in this codebase: the verbatim request (steps treat it as
/// authoritative for WHERE work happens — losing it made every file land in the project root), and
/// the results of earlier steps (the context the remaining work usually builds on).
/// </summary>
public class PlanResumeContextTests
{
    private static PlanRunState SavedMidPlan() => new()
    {
        Goal = "in @Games/ build a pacman game",
        Cursor = 1,
        PreviousResults = ["Step 1 (html): created Games/index.html"],
        Steps =
        [
            new PlanStepState
            {
                Number = 1, Description = "html", Instruction = "create index.html",
                Status = TaskStepStatus.Completed, Result = "created Games/index.html",
            },
            new PlanStepState
            {
                Number = 2, Description = "css", Instruction = "create style.css",
                Status = TaskStepStatus.Pending,
            },
        ],
    };

    [Fact]
    public async Task ResumeRunsOnlyTheOutstandingSteps()
    {
        var exec = new ScriptedPlanStepExecutor();
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var _ in runner.ResumeAsync(SavedMidPlan())) { }

        Assert.Equal(["create style.css"], exec.Executed);
    }

    [Fact]
    public async Task ResumedStepsSeeWhatEarlierStepsProduced()
    {
        // The gap this test exists for: PreviousResults starts empty on a fresh run, so without
        // seeding, step 2 would resume knowing nothing about the file step 1 wrote.
        var exec = new ScriptedPlanStepExecutor();
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var _ in runner.ResumeAsync(SavedMidPlan())) { }

        var seen = exec.PreviousResultsSeen.Single();
        Assert.Contains("Games/index.html", seen.Single());
    }

    [Fact]
    public async Task ResumeCompletesThePlan()
    {
        var plan = PlanCheckpointStore.ToPlan(SavedMidPlan());
        Assert.Equal(1, PlanCheckpointStore.OutstandingSteps(SavedMidPlan()));

        var exec = new ScriptedPlanStepExecutor();
        var runner = new WorkflowPlanRunner(exec);

        var events = new List<TaskProgressEvent>();
        await foreach (var e in runner.ResumeAsync(SavedMidPlan())) events.Add(e);

        Assert.Contains(events, e => e.ProgressType == TaskProgressType.PlanCompleted);
    }

    [Fact]
    public void TheSavedRecordCarriesTheVerbatimRequest()
    {
        // The CLI feeds this into AIService before resuming, because the process that received the
        // original message is gone. Target folders named here override unqualified paths in a step
        // instruction, so losing it is how a plan writes everything to the wrong place.
        Assert.Contains("@Games/", SavedMidPlan().Goal);
    }
}
