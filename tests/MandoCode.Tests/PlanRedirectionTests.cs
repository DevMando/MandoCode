using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PlanRedirectionTests
{
    [Theory]
    [InlineData("write_file")]
    [InlineData("delete_file")]
    [InlineData("execute_command")]
    public async Task NewInstructions_StopActivePlan_AndRefuseFurtherWork(string name)
    {
        var handoff = new PlanHandoff();
        using var execution = handoff.BeginResumedExecution(Array.Empty<PlanFileOperation>());
        var middleware = new AgentFunctionMiddleware(5, planHandoff: handoff);
        var calls = 0;
        var prompts = 0;
        Task<DiffApprovalResult> Redirect()
        {
            prompts++;
            return Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.NewInstructions, UserMessage = "Write one poem instead of ten" });
        }
        middleware.OnWriteApprovalRequested = (_, _, _) => Redirect();
        middleware.OnDeleteApprovalRequested = (_, _) => Redirect();
        middleware.OnCommandApprovalRequested = _ => Redirect();
        Delegate action = name switch
        {
            "write_file" => new Func<string, string, string>((relativePath, content) => { calls++; return "written"; }),
            "delete_file" => new Func<string, string>(relativePath => { calls++; return "deleted"; }),
            _ => new Func<string, string>(command => { calls++; return "executed"; })
        };
        var function = AIFunctionFactory.Create(action, new AIFunctionFactoryOptions { Name = name });
        var arguments = new AIFunctionArguments { ["relativePath"] = "poems.txt", ["content"] = "ten poems", ["command"] = "test" };
        using var scope = middleware.BeginScope();
        var first = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, function, arguments);
        var second = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, function, arguments);
        var propose = AIFunctionFactory.Create(new Func<string>(() => "must not run"), new AIFunctionFactoryOptions { Name = "propose_plan" });
        var third = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, propose);
        Assert.True(scope.PlanCancellationRequested);
        Assert.Equal(0, calls);
        Assert.Equal(1, prompts);
        Assert.Contains("replacement plan", first?.ToString());
        Assert.Contains("refused", second?.ToString());
        Assert.Contains("refused", third?.ToString());
        Assert.False(handoff.HasPendingProposal);
        Assert.Equal("Write one poem instead of ten", handoff.TakeReplacementInstructions());
        Assert.Null(handoff.TakeReplacementInstructions());
    }

    [Fact]
    public async Task DirectChatInstructions_DoNotCancelAPlan_OrCreateAReplacement()
    {
        var handoff = new PlanHandoff();
        var middleware = new AgentFunctionMiddleware(5, planHandoff: handoff);
        middleware.OnWriteApprovalRequested = (_, _, _) => Task.FromResult(new DiffApprovalResult
        { Response = DiffApprovalResponse.NewInstructions, UserMessage = "Use a different name" });
        var function = AIFunctionFactory.Create(new Func<string, string, string>((relativePath, content) => "must not write"),
            new AIFunctionFactoryOptions { Name = "write_file" });
        using var scope = middleware.BeginScope();
        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, function,
            new AIFunctionArguments { ["relativePath"] = "a.txt", ["content"] = "text" });
        Assert.False(scope.PlanCancellationRequested);
        Assert.Contains("Use a different name", result?.ToString());
        Assert.Null(handoff.TakeReplacementInstructions());
    }

    [Fact]
    public void UnconsumedReplacement_IsDiscardedWhenExecutionEnds()
    {
        var handoff = new PlanHandoff();
        using (handoff.BeginResumedExecution(Array.Empty<PlanFileOperation>()))
            Assert.True(handoff.RequestReplacement("new requirement"));
        Assert.Null(handoff.TakeReplacementInstructions());
        Assert.False(handoff.RequestReplacement("outside a plan"));
    }
}
