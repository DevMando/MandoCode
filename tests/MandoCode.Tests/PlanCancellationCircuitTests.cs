using Xunit;
using Microsoft.SemanticKernel;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Regression tests for the plan-cancellation circuit in FunctionInvocationFilter.
/// Picking "Cancel the plan" at a diff-approval prompt only set a scope flag that
/// ExecutePlanStepAsync reads AFTER the step's model call completes — SK's auto-invoke
/// loop kept executing the response's remaining tool calls, each opening a fresh
/// approval prompt. Observed live: a 3-file scaffolding step re-prompted for every
/// file after the first cancel. The circuit refuses all tool calls in a cancelled
/// scope mechanically, before any approval UI.
/// </summary>
public class PlanCancellationCircuitTests
{
    private static (Kernel Kernel, FunctionInvocationFilter Filter) BuildKernel(
        Delegate? method = null,
        string functionName = "test_func")
    {
        var filter = new FunctionInvocationFilter(5);
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromFunctions("TestPlugin", new[]
        {
            KernelFunctionFactory.CreateFromMethod(method ?? (() => "ok"), functionName)
        });
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(filter);
        return (kernel, filter);
    }

    [Fact]
    public async Task CancelledScope_RefusesToolCall_WithoutInvokingIt()
    {
        var invoked = false;
        var (kernel, filter) = BuildKernel(method: new Func<string>(() => { invoked = true; return "ok"; }));

        using var scope = filter.BeginScope();
        scope.RequestPlanCancellation();

        var result = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["test_func"]);

        Assert.False(invoked);
        Assert.Contains("cancelled the plan", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAtWriteApproval_SuppressesFurtherApprovalPrompts()
    {
        // THE live bug: cancel at the first file's diff, and the step's remaining
        // write_file calls each showed their own approval prompt anyway.
        var writes = 0;
        var prompts = 0;
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => { writes++; return "written"; },
            functionName: "write_file");
        filter.OnWriteApprovalRequested = (_, _, _) =>
        {
            prompts++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.CancelPlan });
        };

        using var scope = filter.BeginScope();

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"],
            new KernelArguments { ["relativePath"] = "a.txt", ["content"] = "one" });
        var second = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"],
            new KernelArguments { ["relativePath"] = "b.txt", ["content"] = "two" });

        Assert.True(scope.PlanCancellationRequested);
        Assert.Equal(1, prompts);   // the second call must never reach the approval UI
        Assert.Equal(0, writes);    // and nothing may be written
        Assert.Contains("refused", second.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshScope_IsNotPoisonedByPriorCancelledScope()
    {
        // Cancellation is per-scope: the next chat turn / plan step begins a new scope
        // and must run tools normally again.
        var invoked = false;
        var (kernel, filter) = BuildKernel(method: new Func<string>(() => { invoked = true; return "ok"; }));

        using (var cancelled = filter.BeginScope())
        {
            cancelled.RequestPlanCancellation();
        }

        using var fresh = filter.BeginScope();
        var result = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["test_func"]);

        Assert.True(invoked);
        Assert.Equal("ok", result.ToString());
    }
}
