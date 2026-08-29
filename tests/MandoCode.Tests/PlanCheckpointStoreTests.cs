using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Reconstructing a plan from a saved record.
///
/// These cover the pure logic — what resume would actually re-run, and how a half-finished step is
/// treated. The file I/O is deliberately not exercised: the store writes under the user's real
/// profile directory, and a test that reaches into ~/.mandocode would be writing to the machine it
/// runs on. Making the root injectable is the prerequisite for covering that, and is worth doing
/// before the store grows.
/// </summary>
public class PlanCheckpointStoreTests
{
    private static PlanRunState StateWith(params TaskStepStatus[] statuses) => new()
    {
        Goal = "build a game",
        Cursor = statuses.Count(s => s is TaskStepStatus.Completed or TaskStepStatus.Skipped),
        Steps = [.. statuses.Select((s, i) => new PlanStepState
        {
            Number = i + 1,
            Description = $"step {i + 1}",
            Instruction = $"do thing {i + 1}",
            Status = s,
            Result = s == TaskStepStatus.Completed ? $"result {i + 1}" : null,
        })],
    };

    [Fact]
    public void OutstandingStepsCountsOnlyUnfinishedWork()
    {
        var state = StateWith(
            TaskStepStatus.Completed,
            TaskStepStatus.Skipped,
            TaskStepStatus.Pending,
            TaskStepStatus.Failed);

        Assert.Equal(2, PlanCheckpointStore.OutstandingSteps(state));
    }

    [Fact]
    public void AFinishedPlanHasNothingOutstanding()
    {
        // The selector uses this to delete the record, so a completed plan is never offered.
        var state = StateWith(TaskStepStatus.Completed, TaskStepStatus.Skipped);
        Assert.Equal(0, PlanCheckpointStore.OutstandingSteps(state));
    }

    [Fact]
    public void RebuiltPlanKeepsFinishedStepsFinished()
    {
        // This is what stops resume redoing writes that already succeeded — the runner steps over
        // anything Completed or Skipped.
        var plan = PlanCheckpointStore.ToPlan(
            StateWith(TaskStepStatus.Completed, TaskStepStatus.Skipped, TaskStepStatus.Pending));

        Assert.Equal(TaskStepStatus.Completed, plan.Steps[0].Status);
        Assert.Equal(TaskStepStatus.Skipped, plan.Steps[1].Status);
    }

    [Fact]
    public void AnInterruptedStepIsRunAgain()
    {
        // A step that was InProgress when the process died may have half run. Re-running is the
        // safer assumption: the alternative is silently skipping work that never completed.
        var plan = PlanCheckpointStore.ToPlan(StateWith(TaskStepStatus.InProgress));
        Assert.Equal(TaskStepStatus.Pending, plan.Steps[0].Status);
    }

    [Fact]
    public void AFailedStepIsRunAgain()
    {
        var plan = PlanCheckpointStore.ToPlan(StateWith(TaskStepStatus.Failed));
        Assert.Equal(TaskStepStatus.Pending, plan.Steps[0].Status);
    }

    [Fact]
    public void RebuiltPlanCarriesTheInstructions()
    {
        // Resume re-issues the instruction, not the short display description.
        var plan = PlanCheckpointStore.ToPlan(StateWith(TaskStepStatus.Pending, TaskStepStatus.Pending));

        Assert.Equal("build a game", plan.OriginalRequest);
        Assert.Equal("do thing 2", plan.Steps[1].Instruction);
    }

    [Fact]
    public void RebuiltPlanStartsPending()
    {
        // Not Completed or Cancelled — the run is about to begin again.
        Assert.Equal(
            TaskPlanStatus.Pending,
            PlanCheckpointStore.ToPlan(StateWith(TaskStepStatus.Pending)).Status);
    }

    [Fact]
    public void PathIsStablePerProject_AndDistinguishesSameLeafNames()
    {
        var a = PlanCheckpointStore.PathFor(@"C:\one\api");
        var b = PlanCheckpointStore.PathFor(@"C:\two\api");

        Assert.Equal(a, PlanCheckpointStore.PathFor(@"C:\one\api\"));   // trailing separator
        Assert.NotEqual(a, b);                                          // two folders called "api"
        Assert.Contains("api-", Path.GetFileName(a));                   // readable leaf retained
    }

    [Fact]
    public void DesktopAgentCheckpointPaths_DoNotCollideWithinOneProject()
    {
        var first = PlanCheckpointStore.PathFor(@"C:\work\api", "agent-one");
        var second = PlanCheckpointStore.PathFor(@"C:\work\api", "agent-two");

        Assert.NotEqual(first, second);
        Assert.Equal(first, PlanCheckpointStore.PathFor(@"C:\work\api\", "agent-one"));
        Assert.NotEqual(first, PlanCheckpointStore.PathFor(@"C:\other\api", "agent-one"));
    }
}
