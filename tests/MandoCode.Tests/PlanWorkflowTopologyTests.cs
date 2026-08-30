using Xunit;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Guards the plan graph's shape.
///
/// Resume matches checkpointed state to executors by identity and requires byte-identical topology,
/// so the graph must not depend on how many steps a plan has — otherwise a checkpoint from a 3-step
/// plan could never be restored into a 12-step one, and replanning would invalidate every
/// checkpoint in the field.
///
/// Asserted against Workflow.ReflectExecutors()/ReflectEdges() rather than ToString(), which only
/// returns the type name and would make every assertion here pass vacuously.
/// </summary>
public class PlanWorkflowTopologyTests
{
    private static TaskPlan PlanWith(int stepCount) => new()
    {
        OriginalRequest = "goal",
        Steps = [.. Enumerable.Range(1, stepCount).Select(i => new TaskStep
        {
            StepNumber = i,
            Description = $"step {i}",
            Instruction = $"do {i}",
            Status = TaskStepStatus.Pending,
        })],
    };

    private static Microsoft.Agents.AI.Workflows.Workflow Build(int stepCount)
    {
        var ctx = new PlanRunContext(
            PlanWith(stepCount),
            new ScriptedPlanStepExecutor(),
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        // Build() validates edge typing, connectivity from the start executor and executor binding,
        // so a malformed graph fails here rather than at runtime mid-plan.
        return WorkflowPlanRunner.BuildWorkflow(ctx);
    }

    private static string Shape(int stepCount)
    {
        var wf = Build(stepCount);
        var nodes = wf.ReflectExecutors().Keys.OrderBy(k => k, StringComparer.Ordinal);
        var edges = wf.ReflectEdges()
            .SelectMany(kv => kv.Value.Select(e => $"{kv.Key}->{e}"))
            .OrderBy(s => s, StringComparer.Ordinal);

        return $"start={wf.StartExecutorId}\nnodes={string.Join(",", nodes)}\nedges={string.Join(",", edges)}";
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void GraphBuilds_ForAnyStepCount(int stepCount)
    {
        Assert.NotEmpty(Build(stepCount).ReflectExecutors());
    }

    [Fact]
    public void Topology_IsIdentical_RegardlessOfStepCount()
    {
        // The step cursor lives in workflow state and in messages — never in the graph's shape.
        var one = Shape(1);
        Assert.Equal(one, Shape(3));
        Assert.Equal(one, Shape(12));
    }

    [Fact]
    public void ExecutorSet_IsTheGoldenList()
    {
        // Deliberately hard-coded. This SHOULD fail when the graph gains a node — that failure is
        // the reminder to bump PlanExecutorIds.TopologyVersion and decide what happens to any
        // checkpoints already written. (Checkpointing is not live yet, so growing the graph before
        // then costs nothing.)
        string[] expected =
        [
            PlanExecutorIds.Finalizer,
            PlanExecutorIds.Intake,
            PlanExecutorIds.StepRunner,
            PlanExecutorIds.Triage,
        ];

        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal),
            Build(2).ReflectExecutors().Keys.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void StartsAtIntake()
    {
        Assert.Equal(PlanExecutorIds.Intake, Build(2).StartExecutorId);
    }

    [Fact]
    public void TriageLoopsBackToTheStepRunner()
    {
        // The loop-back edge is what lets one step-runner node serve a plan of any length.
        var edges = Build(2).ReflectEdges();
        Assert.True(edges.ContainsKey(PlanExecutorIds.Triage));

        // Two ways out of triage: back to the step runner for the next step, or on to the finalizer.
        Assert.Equal(2, edges[PlanExecutorIds.Triage].Count);

        var shape = Shape(2);
        Assert.Contains(PlanExecutorIds.StepRunner, shape, StringComparison.Ordinal);
        Assert.Contains(PlanExecutorIds.Triage, shape, StringComparison.Ordinal);
    }
}
