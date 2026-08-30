using Xunit;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// Successor to AgentFunctionMiddlewarePostPlanMutationGateTests, retargeted onto the pending-plan
/// gate.
///
/// The original gate refused mutations for the REST of the turn after a plan had run, because the
/// plan executed inside the propose_plan tool call: the outer model never saw the steps run, read
/// the returned summary as "not started yet", and redid the work — observed live overwriting a
/// finished build under an auto-approved session.
///
/// Deferring execution removes the post-plan turn entirely, so the window that needs guarding
/// shrinks to "between propose_plan and the end of the reply" — the model must not race the plan
/// it just queued. The incident these tests were written for is still the reason they exist, which
/// is why they were retargeted rather than deleted.
/// </summary>
public class AgentFunctionMiddlewarePendingPlanGateTests
{
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    [Fact]
    public async Task PendingPlanScope_RefusesMutatingCall_WithoutInvokingIt()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using var scope = middleware.BeginScope();
        scope.MarkProposalPending();

        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "Test/index.html",
            ["content"] = "<html>"
        });

        Assert.False(invoked);
        Assert.Contains("plan is queued", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("edit_file")]
    [InlineData("delete_file")]
    [InlineData("delete_folder")]
    [InlineData("create_folder")]
    public async Task PendingPlanScope_RefusesAllMutatingFunctions(string functionName)
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath) => { invoked = true; return "ok"; }, functionName);

        using var scope = middleware.BeginScope();
        scope.MarkProposalPending();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["relativePath"] = "Test" });

        Assert.False(invoked);
    }

    [Fact]
    public async Task PendingPlanScope_StillAllowsReads()
    {
        // The model may still want to look around before summarising; only writes would race the plan.
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath) => { invoked = true; return "file contents"; }, "read_file_contents");

        using var scope = middleware.BeginScope();
        scope.MarkProposalPending();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["relativePath"] = "Test/index.html" });

        Assert.True(invoked);
    }

    [Fact]
    public async Task FreshScope_AllowsMutationsAgain()
    {
        // The plan runs between turns; by the next scope it has finished and the model is free again.
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using (var planTurn = middleware.BeginScope())
        {
            planTurn.MarkProposalPending();
        }

        using var nextTurn = middleware.BeginScope();
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "Test/index.html",
            ["content"] = "<html>"
        });

        Assert.True(invoked);
    }

    [Fact]
    public async Task ScopeWithNoProposal_DoesNotEngageGate()
    {
        // Successor to RejectedPlan_DoesNotEngageGate. A turn where nothing was proposed — including
        // one where the user went on to reject the plan — must leave the model free to do the work
        // directly.
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using var scope = middleware.BeginScope();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "Test/index.html",
            ["content"] = "<html>"
        });

        Assert.True(invoked);
    }
}
