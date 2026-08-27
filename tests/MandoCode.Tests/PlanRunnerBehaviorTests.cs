using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Behavior of the current plan runner, driven through <see cref="IPlanStepExecutor"/> with no live
/// model. These assertions are the baseline the workflow engine must reproduce, so they are written
/// against <see cref="IPlanRunner"/> rather than the concrete service — the same suite should run
/// unchanged against the new engine.
/// </summary>
public class PlanRunnerBehaviorTests
{
    private static TaskPlan MakePlan(params string[] instructions) => new()
    {
        OriginalRequest = "build the thing",
        Steps = [.. instructions.Select((instr, i) => new TaskStep
        {
            StepNumber = i + 1,
            Description = $"step {i + 1}",
            Instruction = instr,
            Status = TaskStepStatus.Pending,
        })],
    };

    private static IPlanRunner MakeRunner(IPlanStepExecutor executor)
        => new TaskPlannerService(executor, new MandoCodeConfig());

    private static async Task<List<TaskProgressEvent>> DrainAsync(
        IPlanRunner runner, TaskPlan plan, CancellationToken ct = default)
    {
        var events = new List<TaskProgressEvent>();
        await foreach (var e in runner.ExecutePlanAsync(plan, ct))
        {
            events.Add(e);
        }
        return events;
    }

    [Fact]
    public async Task RunsEveryStep_InOrder()
    {
        var exec = new ScriptedPlanStepExecutor();
        var plan = MakePlan("first", "second", "third");

        await DrainAsync(MakeRunner(exec), plan);

        Assert.Equal(["first", "second", "third"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
        Assert.Equal(3, plan.CompletedStepsCount);
    }

    [Fact]
    public async Task CarriesEarlierResults_ForwardIntoLaterSteps()
    {
        var exec = new ScriptedPlanStepExecutor((_, i) => $"result-{i}");
        var plan = MakePlan("a", "b", "c");

        await DrainAsync(MakeRunner(exec), plan);

        Assert.Empty(exec.PreviousResultsSeen[0]);
        Assert.Contains("result-0", exec.PreviousResultsSeen[1].Single());
        Assert.Equal(2, exec.PreviousResultsSeen[2].Count);
    }

    [Fact]
    public async Task WaitsForQuiescence_AfterEveryStep()
    {
        // Without this the next step can start while the previous one is still writing files.
        var exec = new ScriptedPlanStepExecutor();
        await DrainAsync(MakeRunner(exec), MakePlan("a", "b"));

        Assert.Equal(2, exec.QuiescenceWaits);
    }

    [Fact]
    public async Task EmitsPlanCreated_ThenAStepEventPerStep()
    {
        var exec = new ScriptedPlanStepExecutor();
        var events = await DrainAsync(MakeRunner(exec), MakePlan("a", "b"));

        Assert.Equal(TaskProgressType.PlanCreated, events[0].ProgressType);
        Assert.Equal(2, events.Count(e => e.ProgressType == TaskProgressType.StepCompleted));
        Assert.Contains(events, e => e.ProgressType == TaskProgressType.PlanCompleted);
    }

    [Fact]
    public async Task CancelledToken_StopsBeforeRunningAnyFurtherStep()
    {
        using var cts = new CancellationTokenSource();
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
        {
            if (instr == "second") cts.Cancel();
            return "ok";
        });
        var plan = MakePlan("first", "second", "third");

        await DrainAsync(MakeRunner(exec), plan, cts.Token);

        Assert.Equal(["first", "second"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }

    [Fact]
    public async Task CancelPlan_MidFlight_StopsTheRun()
    {
        var runner = default(IPlanRunner);
        var plan = MakePlan("first", "second", "third");
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
        {
            if (instr == "first") runner!.CancelPlan(plan);
            return "ok";
        });
        runner = MakeRunner(exec);

        await DrainAsync(runner, plan);

        Assert.Equal(["first"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }

    [Fact]
    public async Task FailedStep_WithNoInteractiveConsumer_IsDowngradedToSkipped()
    {
        // Documents a real hazard rather than endorsing it. The runner defers the skip-vs-cancel
        // decision to the consumer, which is expected to mutate plan.Status DURING the yield. A
        // non-interactive caller does no such thing, so every failure silently becomes "skipped"
        // and the plan still reports Completed. The workflow rebuild removes this by making
        // progress read-only and routing the decision through a request port — when it does, this
        // test should be rewritten, not deleted.
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
            instr == "boom" ? throw new InvalidOperationException("nope") : "ok");
        var plan = MakePlan("fine", "boom", "also fine");

        await DrainAsync(MakeRunner(exec), plan);

        Assert.Equal(["fine", "boom", "also fine"], exec.Executed);
        Assert.Equal(TaskStepStatus.Skipped, plan.Steps[1].Status);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public async Task SkippedSteps_AreNotReExecuted()
    {
        var exec = new ScriptedPlanStepExecutor();
        var plan = MakePlan("a", "b", "c");
        plan.Steps[1].Status = TaskStepStatus.Skipped;

        await DrainAsync(MakeRunner(exec), plan);

        Assert.Equal(["a", "c"], exec.Executed);
    }
}
