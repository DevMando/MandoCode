using Xunit;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// MAF-side sibling of PostPlanMutationGateTests' Kernel-driven cases (feat/agent-framework-migration,
/// Phase 6). The PlanHandoff-manifest tests in the SK file aren't duplicated here — PlanHandoff
/// and TaskPlan are already framework-agnostic (confirmed during the migration survey), so those
/// tests already exercise the same code this middleware calls into; there's nothing SK-specific
/// in them to port.
/// </summary>
public class AgentFunctionMiddlewarePostPlanMutationGateTests
{
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    [Fact]
    public async Task CompletedPlanScope_RefusesMutatingCall_WithoutInvokingIt()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using var scope = middleware.BeginScope();
        scope.MarkPlanWorkCompleted();

        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "Test/index.html",
            ["content"] = "<html>"
        });

        Assert.False(invoked);
        Assert.Contains("already completed", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("edit_file")]
    [InlineData("delete_file")]
    [InlineData("delete_folder")]
    [InlineData("create_folder")]
    public async Task CompletedPlanScope_RefusesAllMutatingFunctions(string functionName)
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath) => { invoked = true; return "ok"; }, functionName);

        using var scope = middleware.BeginScope();
        scope.MarkPlanWorkCompleted();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["relativePath"] = "Test" });

        Assert.False(invoked);
    }

    [Fact]
    public async Task CompletedPlanScope_StillAllowsReads()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath) => { invoked = true; return "file contents"; }, "read_file_contents");

        using var scope = middleware.BeginScope();
        scope.MarkPlanWorkCompleted();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["relativePath"] = "Test/index.html" });

        Assert.True(invoked);
    }

    [Fact]
    public async Task FreshScope_AllowsMutationsAgain()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using (var planTurn = middleware.BeginScope())
        {
            planTurn.MarkPlanWorkCompleted();
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
    public async Task RejectedPlan_DoesNotEngageGate()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { invoked = true; return "written"; }, "write_file");

        using var scope = middleware.BeginScope();
        scope.MarkPlanProcessed();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "Test/index.html",
            ["content"] = "<html>"
        });

        Assert.True(invoked);
    }
}
