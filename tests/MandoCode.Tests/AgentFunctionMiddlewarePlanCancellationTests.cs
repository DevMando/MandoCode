using Xunit;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// MAF-side sibling of PlanCancellationCircuitTests (feat/agent-framework-migration, Phase 6).
/// </summary>
public class AgentFunctionMiddlewarePlanCancellationTests
{
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    [Fact]
    public async Task CancelledScope_RefusesToolCall_WithoutInvokingIt()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn(new Func<string>(() => { invoked = true; return "ok"; }), "test_func");

        using var scope = middleware.BeginScope();
        scope.RequestPlanCancellation();

        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn);

        Assert.False(invoked);
        Assert.Contains("cancelled the plan", result?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAtWriteApproval_SuppressesFurtherApprovalPrompts()
    {
        var writes = 0;
        var prompts = 0;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => { writes++; return "written"; }, "write_file");
        middleware.OnWriteApprovalRequested = (_, _, _) =>
        {
            prompts++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan });
        };

        using var scope = middleware.BeginScope();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "a.txt",
            ["content"] = "one"
        });
        var second = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["relativePath"] = "b.txt",
            ["content"] = "two"
        });

        Assert.True(scope.PlanCancellationRequested);
        Assert.Equal(1, prompts);
        Assert.Equal(0, writes);
        Assert.Contains("refused", second?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshScope_IsNotPoisonedByPriorCancelledScope()
    {
        var invoked = false;
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn(new Func<string>(() => { invoked = true; return "ok"; }), "test_func");

        using (var cancelled = middleware.BeginScope())
        {
            cancelled.RequestPlanCancellation();
        }

        using var fresh = middleware.BeginScope();
        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn);

        Assert.True(invoked);
        Assert.Equal("ok", result?.ToString());
    }
}
