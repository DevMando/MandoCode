using Xunit;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// MAF-side sibling of FunctionInvocationFilterLifecycleTests (feat/agent-framework-migration,
/// Phase 6). Same regressions, same assertions — driven through AgentFunctionMiddleware.InterceptAsync
/// directly instead of a real Kernel, via AgentMiddlewareTestHelpers. The SK tests stay in place
/// and untouched; SK is still the live chat path until the actual cutover.
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
    public async Task SecondProposePlan_InSameTurn_IsShortCircuited()
    {
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => Task.FromResult("plan executed")
        };
        var middleware = new AgentFunctionMiddleware(0, null, null, handoff);
        var fn = Fn((string goal, string steps) => "should never run — intercepted", "propose_plan");

        using var scope = middleware.BeginScope();

        var args = new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        };

        var first = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, args);
        Assert.Contains("plan executed", first?.ToString());
        Assert.True(scope.PlanAlreadyProcessed);

        var second = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, args);
        Assert.Contains("already proposed", second?.ToString());
        Assert.DoesNotContain("plan executed", second?.ToString());
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

        var malformed = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "not json at all"
        });
        Assert.False(scope.PlanAlreadyProcessed);

        var retry = await AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        });
        Assert.Contains("plan executed", retry?.ToString());
        Assert.True(scope.PlanAlreadyProcessed);
    }
}
