using Xunit;
using MandoCode.Models;
using MandoCode.Plugins;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// The single-slot proposal store. A proposal belongs to the turn that produced it — anything else
/// means a plan the user walked away from runs later, attached to an unrelated request.
///
/// Observed live: cancelling a turn after propose_plan left the proposal queued, and the cancelled
/// run also threw OperationCanceledException past the host's catch-all, which reported it a second
/// time as "Unexpected error: A task was canceled." on top of "Request cancelled."
/// </summary>
public class PlanProposalSlotTests
{
    private static PlanStepProposal[] Steps(params string[] descriptions)
        => [.. descriptions.Select(d => new PlanStepProposal(d, $"do {d}"))];

    [Fact]
    public void SetThenClear_LeavesNothingPending()
    {
        var handoff = new PlanHandoff();
        handoff.SetPendingProposal("goal", Steps("one"));
        Assert.True(handoff.HasPendingProposal);

        handoff.ClearPendingProposal();
        Assert.False(handoff.HasPendingProposal);
    }

    [Fact]
    public async Task ClearedProposal_DoesNotRunLater()
    {
        // The host clears at the start of every turn, so a proposal abandoned by a cancelled turn
        // cannot execute at the end of the next one.
        var ran = false;
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => { ran = true; return Task.FromResult("executed"); }
        };

        handoff.SetPendingProposal("abandoned goal", Steps("one", "two"));
        handoff.ClearPendingProposal();

        var manifest = await handoff.RunPendingPlanAsync();

        Assert.Null(manifest);
        Assert.False(ran);
    }

    [Fact]
    public async Task RunningTakesTheSlot_SoAReplayIsANoOp()
    {
        var runs = 0;
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => { runs++; return Task.FromResult("executed"); }
        };

        handoff.SetPendingProposal("goal", Steps("one"));

        Assert.Equal("executed", await handoff.RunPendingPlanAsync());
        Assert.Null(await handoff.RunPendingPlanAsync());
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task NoProposal_ReturnsNullRatherThanThrowing()
    {
        // Hosts call this unconditionally after every turn.
        Assert.Null(await new PlanHandoff().RunPendingPlanAsync());
    }

    [Fact]
    public void LastProposalWins()
    {
        var handoff = new PlanHandoff();
        handoff.SetPendingProposal("first", Steps("a"));
        handoff.SetPendingProposal("second", Steps("b", "c"));

        Assert.True(handoff.HasPendingProposal);
    }
}
