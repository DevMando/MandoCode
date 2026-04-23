using Xunit;
using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests for PlanHandoff — the bridge between FunctionInvocationFilter and the UI.
/// </summary>
public class PlanHandoffTests
{
    [Fact]
    public async Task ProcessAsync_NoSubscriber_ReturnsFallbackMessage()
    {
        var handoff = new PlanHandoff();
        var proposals = new[] { new PlanStepProposal("step", "do thing") };

        var result = await handoff.ProcessAsync("goal", proposals);

        Assert.Contains("not wired up", result);
    }

    [Fact]
    public async Task ProcessAsync_InvokesCallback_AndReturnsSummary()
    {
        var handoff = new PlanHandoff();
        TaskPlan? captured = null;
        handoff.OnPlanRequested = (plan, ct) =>
        {
            captured = plan;
            return Task.FromResult("plan-done");
        };

        var proposals = new[]
        {
            new PlanStepProposal("a", "instruction A"),
            new PlanStepProposal("b", "instruction B"),
        };

        var result = await handoff.ProcessAsync("build a feature", proposals);

        Assert.Equal("plan-done", result);
        Assert.NotNull(captured);
        Assert.Equal("build a feature", captured!.OriginalRequest);
        Assert.Equal(2, captured.Steps.Count);
        Assert.Equal("a", captured.Steps[0].Description);
    }

    [Fact]
    public async Task ProcessAsync_EmptyProposals_ShortCircuits()
    {
        var handoff = new PlanHandoff();
        var invoked = false;
        handoff.OnPlanRequested = (_, _) =>
        {
            invoked = true;
            return Task.FromResult("should not run");
        };

        var result = await handoff.ProcessAsync("goal", Array.Empty<PlanStepProposal>());

        Assert.False(invoked);
        Assert.Contains("no steps", result);
    }

    [Fact]
    public async Task ProcessAsync_RecursiveCall_ShortCircuits()
    {
        // Simulates the model calling propose_plan from *inside* a running plan step.
        // The guard in PlanHandoff must return an error message, not enter the callback.
        var handoff = new PlanHandoff();
        var innerInvocations = 0;
        string? innerResult = null;

        handoff.OnPlanRequested = async (_, _) =>
        {
            // While the outer call is executing, simulate a nested propose_plan call.
            innerResult = await handoff.ProcessAsync("nested goal", new[]
            {
                new PlanStepProposal("x", "y")
            });
            innerInvocations++;
            return "outer done";
        };

        var proposals = new[] { new PlanStepProposal("step", "do thing") };
        var result = await handoff.ProcessAsync("goal", proposals);

        Assert.Equal("outer done", result);
        Assert.Equal(1, innerInvocations);
        Assert.NotNull(innerResult);
        Assert.Contains("already executing", innerResult!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessAsync_ResetsFlagAfterCompletion()
    {
        // After the outer call returns, the guard must reset so future plans can run.
        var handoff = new PlanHandoff();
        handoff.OnPlanRequested = (_, _) => Task.FromResult("ok");

        var first = await handoff.ProcessAsync("g1", new[] { new PlanStepProposal("a", "b") });
        var second = await handoff.ProcessAsync("g2", new[] { new PlanStepProposal("c", "d") });

        Assert.Equal("ok", first);
        Assert.Equal("ok", second);
    }
}
