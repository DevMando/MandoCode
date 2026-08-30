using Xunit;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// MAF-side sibling of the old FunctionInvocationFilterLifecycleTests (feat/agent-framework-migration,
/// Phase 6, since deleted along with the rest of SK). Same regressions, same assertions — driven
/// through AgentFunctionMiddleware.InterceptAsync directly instead of a real Kernel, via
/// AgentMiddlewareTestHelpers.
/// </summary>
public class AgentFunctionMiddlewareLifecycleTests
{
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    [Fact]
    public async Task PendingCount_ReturnsToZero_AfterSuccessfulInvocation()
    {
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn(() => "ok", "test_func");
        var started = 0;
        var finished = 0;
        middleware.OnFunctionStarted += () => started++;
        middleware.OnFunctionFinished += () => finished++;

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn);

        Assert.Equal(0, middleware.PendingFunctionCount);
        Assert.Equal(1, started);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenFunctionThrows()
    {
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn(new Func<string>(() => throw new InvalidOperationException("tool blew up")), "test_func");
        var finished = 0;
        middleware.OnFunctionFinished += () => finished++;

        var result = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn);

        // Same contract as SK: the plugin exception is swallowed into an error string for the
        // model, not rethrown to the caller.
        Assert.Contains("tool blew up", result?.ToString());
        Assert.Equal(0, middleware.PendingFunctionCount);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenUiEventHandlerThrows()
    {
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn(() => "ok", "test_func");
        middleware.OnFunctionInvoked += _ => throw new InvalidOperationException("render failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn));

        Assert.Equal(0, middleware.PendingFunctionCount);
    }

    [Fact]
    public async Task OversizedSingleToolResult_IsTruncatedBeforeItReachesTheModel()
    {
        const int budget = 100;
        var middleware = new AgentFunctionMiddleware(0, resultCharBudget: budget);
        var fn = Fn(() => new string('x', 10_000), "large_result");

        using var scope = middleware.BeginScope();
        var result = (await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn))?.ToString();

        Assert.NotNull(result);
        Assert.Equal(budget, result!.Length);
        Assert.Contains("tool result truncated", result);
        Assert.Equal(budget, scope.TotalResultChars);
        Assert.True(scope.BudgetExhausted);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenApprovalCallbackThrows()
    {
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => "written", "write_file");
        middleware.OnWriteApprovalRequested = (_, _, _) => throw new InvalidOperationException("prompt crashed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
            {
                ["relativePath"] = "foo.txt",
                ["content"] = "hello"
            }));

        Assert.Equal(0, middleware.PendingFunctionCount);
    }

    [Fact]
    public async Task StuckApprovalPrompt_UnwindsOnCancellation()
    {
        var middleware = new AgentFunctionMiddleware(5);
        var fn = Fn((string relativePath, string content) => "written", "write_file");
        var neverCompletes = new TaskCompletionSource<DiffApprovalResult>();
        middleware.OnWriteApprovalRequested = (_, _, _) => neverCompletes.Task;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
            {
                ["relativePath"] = "foo.txt",
                ["content"] = "hello"
            }, cts.Token));

        Assert.Equal(0, middleware.PendingFunctionCount);
    }

    [Fact]
    public async Task PluginLevelEditFailures_TripTheEditFailureCircuit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "target.txt"), "hello world");

            var middleware = new AgentFunctionMiddleware(0, new ProjectRootAccessor(tempRoot));
            middleware.OnWriteApprovalRequested = (_, _, _) =>
                Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });

            var fn = Fn((string relativePath, string old_text, string new_text) =>
                $"Error: Could not find the specified text in {relativePath}.", "edit_file");

            using var scope = middleware.BeginScope();

            AIFunctionArguments Args(int i) => new()
            {
                ["relativePath"] = "target.txt",
                ["old_text"] = "hello world",
                ["new_text"] = $"variant {i}"
            };

            for (var i = 0; i < InvocationScope.EditFailureCircuitThreshold; i++)
            {
                var r = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(i));
                Assert.StartsWith("Error:", r?.ToString());
            }

            var normalizedKey = Path.GetFullPath(Path.Combine(tempRoot, "target.txt"));
            Assert.Equal(InvocationScope.EditFailureCircuitThreshold, scope.GetEditFailureCount(normalizedKey));

            var tripped = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(99));
            Assert.Contains("Edit-failure circuit tripped", tripped?.ToString());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RangedReads_DifferentRanges_AreNotRedundant_SameRangeIs()
    {
        var middleware = new AgentFunctionMiddleware(0);
        var fn = Fn((string relativePath, int startLine, int endLine) =>
            $"File: {relativePath} (lines {startLine}-{endLine} of 1000)\n...", "read_file_contents");

        using var scope = middleware.BeginScope();

        AIFunctionArguments Args(int start, int end) => new()
        {
            ["relativePath"] = "big.txt",
            ["startLine"] = start,
            ["endLine"] = end
        };

        var page1 = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(1, 400));
        Assert.DoesNotContain("already read", page1?.ToString());

        var page2 = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(401, 800));
        Assert.DoesNotContain("already read", page2?.ToString());

        var repeat = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(1, 400));
        Assert.Contains("already read", repeat?.ToString());
    }

    [Fact]
    public async Task EditFailures_AcrossPathAliases_ShareOneCircuitCounter()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Games"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "Games", "index.html"), "hello world");

            var middleware = new AgentFunctionMiddleware(0, new ProjectRootAccessor(tempRoot));
            middleware.OnWriteApprovalRequested = (_, _, _) =>
                Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });

            var fn = Fn((string relativePath, string old_text, string new_text) =>
                $"Error: Could not find the specified text in {relativePath}.", "edit_file");

            using var scope = middleware.BeginScope();

            var alias = $"{Path.GetFileName(tempRoot)}/Games/index.html";

            AIFunctionArguments Args(string path, int i) => new()
            {
                ["relativePath"] = path,
                ["old_text"] = "hello world",
                ["new_text"] = $"variant {i}"
            };

            for (var i = 0; i < InvocationScope.EditFailureCircuitThreshold; i++)
            {
                var r = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args(alias, i));
                Assert.StartsWith("Error:", r?.ToString());
            }

            var tripped = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, Args("Games/index.html", 99));
            Assert.Contains("Edit-failure circuit tripped", tripped?.ToString());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProposePlan_QueuesThePlan_WithoutRunningIt()
    {
        // The defining property of the deferred-proposal design: propose_plan returns a receipt and
        // the plan runs only once the host drains the turn. Previously this single call awaited
        // approval and every step inline, which is what forced the watchdog pause, the prompt-gate
        // release dance, and the post-plan mutation gate into existence.
        var ran = false;
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => { ran = true; return Task.FromResult("plan executed"); }
        };
        var middleware = new AgentFunctionMiddleware(0, null, null, handoff);
        var fn = Fn((string goal, string steps) => "should never run — intercepted", "propose_plan");

        using var scope = middleware.BeginScope();

        var receipt = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        });

        Assert.False(ran);
        Assert.True(handoff.HasPendingProposal);
        Assert.True(scope.ProposalPending);
        Assert.Contains("Plan received", receipt?.ToString());

        // ...and it runs when the host asks for it, after the turn.
        var manifest = await handoff.RunPendingPlanAsync();
        Assert.True(ran);
        Assert.Equal("plan executed", manifest);
        Assert.False(handoff.HasPendingProposal);
    }

    [Fact]
    public async Task SecondProposePlan_InSameTurn_ReplacesTheFirst()
    {
        // Previously refused outright, because a second proposal meant the first had already run
        // and the model was starting uninvited extra work. With execution deferred nothing has run
        // yet, so last-wins is both correct and safer than a prose refusal the model can ignore.
        TaskPlan? executed = null;
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (plan, _) => { executed = plan; return Task.FromResult("done"); }
        };
        var middleware = new AgentFunctionMiddleware(0, null, null, handoff);
        var fn = Fn((string goal, string steps) => "should never run — intercepted", "propose_plan");

        using var scope = middleware.BeginScope();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "first goal",
            ["steps"] = "[{\"description\":\"one\",\"instruction\":\"do one\"}]"
        });
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "second goal",
            ["steps"] = "[{\"description\":\"two\",\"instruction\":\"do two\"},{\"description\":\"three\",\"instruction\":\"do three\"}]"
        });

        await handoff.RunPendingPlanAsync();

        Assert.NotNull(executed);
        Assert.Equal("second goal", executed!.OriginalRequest);
        Assert.Equal(2, executed.Steps.Count);
    }

    [Fact]
    public async Task MalformedProposal_DoesNotConsumeThePlanSlot()
    {
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => Task.FromResult("plan executed")
        };
        var middleware = new AgentFunctionMiddleware(0, null, null, handoff);
        var fn = Fn((string goal, string steps) => "should never run — intercepted", "propose_plan");

        using var scope = middleware.BeginScope();

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "not json at all"
        });
        Assert.False(scope.ProposalPending);
        Assert.False(handoff.HasPendingProposal);

        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        });
        Assert.True(scope.ProposalPending);
        Assert.True(handoff.HasPendingProposal);
    }

    [Fact]
    public async Task ProposePlan_DuringPlanExecution_IsRefused()
    {
        // A step's own model call can reach propose_plan. Nested planning is always a runaway.
        var handoff = new PlanHandoff();
        var middleware = new AgentFunctionMiddleware(0, null, null, handoff);
        var fn = Fn((string goal, string steps) => "should never run — intercepted", "propose_plan");

        string? nested = null;
        handoff.OnPlanRequested = async (_, _) =>
        {
            using var stepScope = middleware.BeginScope();
            nested = (await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
            {
                ["goal"] = "a plan within a plan",
                ["steps"] = "[{\"description\":\"nested\",\"instruction\":\"nested\"}]"
            }))?.ToString();
            return "outer done";
        };

        using var scope = middleware.BeginScope();
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "outer",
            ["steps"] = "[{\"description\":\"one\",\"instruction\":\"do one\"}]"
        });
        await handoff.RunPendingPlanAsync();

        Assert.Contains("already executing", nested);
    }
}
