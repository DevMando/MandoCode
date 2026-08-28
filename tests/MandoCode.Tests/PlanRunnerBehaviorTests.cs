using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Behavior shared by every plan runner, driven through <see cref="IPlanStepExecutor"/> with no
/// live model.
///
/// Every case runs against BOTH engines. That is the whole point: while `planner` can select
/// either one, any divergence between them makes an A/B against real local models
/// uninterpretable — a behavior difference would be indistinguishable from a model difference.
/// </summary>
public class PlanRunnerBehaviorTests
{
    public static TheoryData<string> Engines => new() { "legacy", "workflow" };

    private static IPlanRunner MakeRunner(string engine, IPlanStepExecutor executor) => engine switch
    {
        "legacy" => new TaskPlannerService(executor, new MandoCodeConfig()),
        "workflow" => new WorkflowPlanRunner(executor),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "unknown planner engine"),
    };

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

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task RunsEveryStep_InOrder(string engine)
    {
        var exec = new ScriptedPlanStepExecutor();
        var plan = MakePlan("first", "second", "third");

        await DrainAsync(MakeRunner(engine, exec), plan);

        Assert.Equal(["first", "second", "third"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
        Assert.Equal(3, plan.CompletedStepsCount);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task CarriesEarlierResults_ForwardIntoLaterSteps(string engine)
    {
        var exec = new ScriptedPlanStepExecutor((_, i) => $"result-{i}");
        var plan = MakePlan("a", "b", "c");

        await DrainAsync(MakeRunner(engine, exec), plan);

        Assert.Empty(exec.PreviousResultsSeen[0]);
        Assert.Contains("result-0", exec.PreviousResultsSeen[1].Single());
        Assert.Equal(2, exec.PreviousResultsSeen[2].Count);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task WaitsForQuiescence_AfterEveryStep(string engine)
    {
        // Without this the next step can start while the previous one is still writing files.
        var exec = new ScriptedPlanStepExecutor();
        await DrainAsync(MakeRunner(engine, exec), MakePlan("a", "b"));

        Assert.Equal(2, exec.QuiescenceWaits);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task EmitsPlanCreated_ThenAStepEventPerStep(string engine)
    {
        var exec = new ScriptedPlanStepExecutor();
        var events = await DrainAsync(MakeRunner(engine, exec), MakePlan("a", "b"));

        Assert.Equal(TaskProgressType.PlanCreated, events[0].ProgressType);
        Assert.Equal(2, events.Count(e => e.ProgressType == TaskProgressType.StepCompleted));
        Assert.Contains(events, e => e.ProgressType == TaskProgressType.PlanCompleted);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task CancelledToken_StopsBeforeRunningAnyFurtherStep(string engine)
    {
        using var cts = new CancellationTokenSource();
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
        {
            if (instr == "second") cts.Cancel();
            return "ok";
        });
        var plan = MakePlan("first", "second", "third");

        await DrainAsync(MakeRunner(engine, exec), plan, cts.Token);

        Assert.Equal(["first", "second"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task CancelPlan_MidFlight_StopsTheRun(string engine)
    {
        var runner = default(IPlanRunner);
        var plan = MakePlan("first", "second", "third");
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
        {
            if (instr == "first") runner!.CancelPlan(plan);
            return "ok";
        });
        runner = MakeRunner(engine, exec);

        await DrainAsync(runner, plan);

        Assert.Equal(["first"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task FailedStep_WithNoInteractiveConsumer_IsDowngradedToSkipped(string engine)
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

        await DrainAsync(MakeRunner(engine, exec), plan);

        Assert.Equal(["fine", "boom", "also fine"], exec.Executed);
        Assert.Equal(TaskStepStatus.Skipped, plan.Steps[1].Status);
        Assert.Equal(TaskPlanStatus.Completed, plan.Status);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task SkippedSteps_AreNotReExecuted(string engine)
    {
        var exec = new ScriptedPlanStepExecutor();
        var plan = MakePlan("a", "b", "c");
        plan.Steps[1].Status = TaskStepStatus.Skipped;

        await DrainAsync(MakeRunner(engine, exec), plan);

        Assert.Equal(["a", "c"], exec.Executed);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task ConsumerCancellingOnAFailedStep_StopsThePlan(string engine)
    {
        // The one place a runner must wait for the consumer. The consumer decides skip-vs-cancel by
        // mutating plan.Status while handling StepFailed; reading it before the decision lands is
        // the bug the legacy runner documents — "Cancel the plan" silently downgraded to "skip".
        // Every other progress event is fire-and-forget so rendering never holds up the model, so
        // this asserts the exception is still honoured.
        var exec = new ScriptedPlanStepExecutor((instr, _) =>
            instr == "boom" ? throw new InvalidOperationException("nope") : "ok");
        var plan = MakePlan("fine", "boom", "never runs");
        var runner = MakeRunner(engine, exec);

        await foreach (var e in runner.ExecutePlanAsync(plan))
        {
            if (e.ProgressType == TaskProgressType.StepFailed) runner.CancelPlan(plan);
        }

        Assert.Equal(["fine", "boom"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task ProgressEvents_ArriveInOrder(string engine)
    {
        // Fire-and-forget is only safe because the transport is FIFO with a single reader: the
        // display may trail the work, but it can never show it out of sequence.
        var exec = new ScriptedPlanStepExecutor();
        var events = await DrainAsync(MakeRunner(engine, exec), MakePlan("a", "b", "c"));

        var types = events.Select(e => e.ProgressType).ToList();
        Assert.Equal(TaskProgressType.PlanCreated, types.First());
        Assert.Equal(TaskProgressType.PlanCompleted, types.Last());

        // Each step's completion follows every earlier step's completion.
        var completedSteps = events
            .Where(e => e.ProgressType == TaskProgressType.StepCompleted)
            .Select(e => e.CurrentStep)
            .ToList();
        Assert.Equal(completedSteps.OrderBy(n => n), completedSteps);
    }
}
