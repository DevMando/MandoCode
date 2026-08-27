using System.Text.RegularExpressions;
using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Guards the plan workflow's identity scheme. MAF matches checkpointed state back to executors by
/// identity, and for agent-backed executors that identity comes from both the agent's Id and Name —
/// so a checkpoint written under one identity can never be resumed under another, and there is no
/// repair path for checkpoints already on disk.
///
/// These are cheap assertions protecting an expensive mistake: a careless rename in review would
/// silently orphan every checkpoint in the field.
/// </summary>
public class PlanExecutorIdsTests
{
    [Fact]
    public void AllExecutorIds_AreDistinct()
    {
        var all = PlanExecutorIds.All;
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AllExecutorIds_FollowTheNamingScheme()
    {
        // Versioned and namespaced, so a topology bump is visible in every id.
        var pattern = new Regex(@"^mandocode\.plan\.v\d+\.[a-z][a-z-]*$");

        foreach (var id in PlanExecutorIds.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Matches(pattern, id);
        }
    }

    [Fact]
    public void StepAgent_DoesNotShareIdentityWithGeneralistAgent()
    {
        // Two differently-purposed agents under one name is exactly the collision that routes
        // restored state to the wrong executor.
        Assert.NotEqual(PlanExecutorIds.GeneralistAgentId, PlanExecutorIds.StepAgentId);
        Assert.NotEqual(PlanExecutorIds.GeneralistAgentName, PlanExecutorIds.StepAgentName);
    }

    [Fact]
    public void GeneralistAgentName_IsUnchanged()
    {
        // User-visible, and pinned as the agent's identity. Changing it would invalidate every
        // existing checkpoint for no benefit.
        Assert.Equal("MandoCode", PlanExecutorIds.GeneralistAgentName);
    }

    [Fact]
    public void TopologyVersion_MatchesTheVersionEmbeddedInTheIds()
    {
        // If the ids say v2 but TopologyVersion still says 1, resume would accept a checkpoint
        // written against a different graph.
        foreach (var id in PlanExecutorIds.All)
        {
            Assert.Contains($".v{PlanExecutorIds.TopologyVersion}.", id);
        }
    }

    [Fact]
    public void GoldenList_MatchesExactly()
    {
        // Deliberately hard-coded rather than derived. This test SHOULD fail when the graph
        // changes — that failure is the reminder to bump TopologyVersion and decide what happens
        // to checkpoints already written.
        string[] expected =
        [
            "mandocode.plan.v1.intake",
            "mandocode.plan.v1.approval",
            "mandocode.plan.v1.gate",
            "mandocode.plan.v1.step-runner",
            "mandocode.plan.v1.triage",
            "mandocode.plan.v1.step-decision",
            "mandocode.plan.v1.replanner",
            "mandocode.plan.v1.finalizer",
        ];

        Assert.Equal(expected, PlanExecutorIds.All);
    }
}
