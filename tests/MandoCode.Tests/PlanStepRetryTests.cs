using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Retrying a failed step.
///
/// Until now a failed step offered only "skip" or "kill the plan", so a transient failure — a
/// momentary tool error, a model that fumbled one call — cost the whole step. Retry is signalled by
/// the consumer setting the step back to Pending while handling StepFailed; triage re-dispatches
/// the same index rather than advancing the cursor past it.
///
/// Workflow engine only: the legacy runner walks its steps with a foreach and has no way back to
/// one it has already passed.
/// </summary>
public class PlanStepRetryTests
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

    /// <summary>Fails the named instruction for the first <paramref name="failures"/> attempts.</summary>
    private static ScriptedPlanStepExecutor FailsThenSucceeds(string failing, int failures)
    {
        var seen = 0;
        return new ScriptedPlanStepExecutor((instr, _) =>
        {
            if (instr != failing) return "ok";
            if (seen++ < failures) throw new InvalidOperationException("transient");
            return "ok on retry";
        });
    }

    [Fact]
    public async Task RetryRunsTheSameStepAgain()
    {
        var exec = FailsThenSucceeds("flaky", failures: 1);
        var plan = MakePlan("first", "flaky", "third");
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var e in runner.ExecutePlanAsync(plan))
        {
            if (e.ProgressType == TaskProgressType.StepFailed)
            {
                plan.Steps[e.CurrentStep - 1].Status = TaskStepStatus.Pending;
            }
        }

        // "flaky" ran twice — once failing, once succeeding — and the plan carried on.
        Assert.Equal(["first", "flaky", "flaky", "third"], exec.Executed);
        Assert.Equal(TaskStepStatus.Completed, plan.Steps[1].Status);
    }

    [Fact]
    public async Task RetryIsCappedSoAPermanentFailureCannotLoop()
    {
        // A step that fails identically every time must not spin forever just because the consumer
        // keeps asking for a retry.
        var exec = FailsThenSucceeds("doomed", failures: int.MaxValue);
        var plan = MakePlan("doomed", "after");
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var e in runner.ExecutePlanAsync(plan))
        {
            if (e.ProgressType == TaskProgressType.StepFailed)
            {
                plan.Steps[e.CurrentStep - 1].Status = TaskStepStatus.Pending;
            }
        }

        var attempts = exec.Executed.Count(i => i == "doomed");
        Assert.Equal(PlanRunContext.MaxRetriesPerStep + 1, attempts);   // first try plus the retries

        // Exhaustion pauses at the unresolved step instead of silently skipping required work.
        Assert.Equal(TaskStepStatus.Failed, plan.Steps[0].Status);
        Assert.Equal(TaskPlanStatus.Paused, plan.Status);
        Assert.DoesNotContain("after", exec.Executed);
    }

    [Fact]
    public async Task NotRetryingStillSkips()
    {
        // The existing behaviour has to survive: a consumer that does not ask for a retry gets the
        // step skipped, exactly as before.
        var exec = FailsThenSucceeds("boom", failures: int.MaxValue);
        var plan = MakePlan("boom", "after");
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var _ in runner.ExecutePlanAsync(plan)) { }

        Assert.Equal(["boom", "after"], exec.Executed);
        Assert.Equal(TaskStepStatus.Skipped, plan.Steps[0].Status);
    }

    [Fact]
    public async Task CancellingStillBeatsRetrying()
    {
        // Cancel must win: a consumer that cancels has made a stronger statement than one asking
        // to try again.
        var exec = FailsThenSucceeds("boom", failures: int.MaxValue);
        var plan = MakePlan("boom", "never runs");
        var runner = new WorkflowPlanRunner(exec);

        await foreach (var e in runner.ExecutePlanAsync(plan))
        {
            if (e.ProgressType == TaskProgressType.StepFailed) runner.CancelPlan(plan);
        }

        Assert.Equal(["boom"], exec.Executed);
        Assert.Equal(TaskPlanStatus.Cancelled, plan.Status);
    }
}
