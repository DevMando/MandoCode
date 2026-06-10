using Xunit;
using Microsoft.SemanticKernel;
using MandoCode.Models;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Regression tests for the pending-count lifecycle in FunctionInvocationFilter.
/// PendingFunctionCount drives the stall watchdog's pause/resume: a leaked increment
/// (exception after OnFunctionStarted but before the old inner finally) pinned the
/// watchdog paused for the rest of the session, so a later model stall hung silently.
/// These tests drive the filter through a real Kernel so the actual SK pipeline runs.
/// </summary>
public class FunctionInvocationFilterLifecycleTests
{
    private static (Kernel Kernel, FunctionInvocationFilter Filter) BuildKernel(
        FunctionInvocationFilter? filter = null,
        Delegate? method = null,
        string functionName = "test_func")
    {
        filter ??= new FunctionInvocationFilter(5);
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
    public async Task PendingCount_ReturnsToZero_AfterSuccessfulInvocation()
    {
        var (kernel, filter) = BuildKernel();
        var started = 0;
        var finished = 0;
        filter.OnFunctionStarted += () => started++;
        filter.OnFunctionFinished += () => finished++;

        await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["test_func"]);

        Assert.Equal(0, filter.PendingFunctionCount);
        Assert.Equal(1, started);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenFunctionThrows()
    {
        // The function itself throwing is swallowed into an error result for the model —
        // the count must still come back down and OnFunctionFinished must still fire.
        var (kernel, filter) = BuildKernel(
            method: new Func<string>(() => throw new InvalidOperationException("tool blew up")));
        var finished = 0;
        filter.OnFunctionFinished += () => finished++;

        var result = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["test_func"]);

        Assert.Contains("tool blew up", result.ToString());
        Assert.Equal(0, filter.PendingFunctionCount);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenUiEventHandlerThrows()
    {
        // THE regression: an exception between OnFunctionStarted and the invocation
        // (here: a UI event handler) used to skip the decrement entirely, leaving the
        // count pinned > 0 for the session and the stall watchdog permanently paused.
        var (kernel, filter) = BuildKernel();
        filter.OnFunctionInvoked += _ => throw new InvalidOperationException("render failed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["test_func"]));

        Assert.Equal(0, filter.PendingFunctionCount);
    }

    [Fact]
    public async Task PendingCount_ReturnsToZero_WhenApprovalCallbackThrows()
    {
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => "written",
            functionName: "write_file");
        filter.OnWriteApprovalRequested = (_, _, _) => throw new InvalidOperationException("prompt crashed");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"], new KernelArguments
            {
                ["relativePath"] = "foo.txt",
                ["content"] = "hello"
            }));

        Assert.Equal(0, filter.PendingFunctionCount);
    }

    [Fact]
    public async Task StuckApprovalPrompt_UnwindsOnCancellation()
    {
        // A wedged approval prompt (never-completing task) must not hang the turn
        // forever: the WaitAsync(context.CancellationToken) wrapper lets cancellation
        // unwind the invocation, and the count must come back down with it.
        var (kernel, filter) = BuildKernel(
            method: (string relativePath, string content) => "written",
            functionName: "write_file");
        var neverCompletes = new TaskCompletionSource<DiffApprovalResult>();
        filter.OnWriteApprovalRequested = (_, _, _) => neverCompletes.Task;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["write_file"], new KernelArguments
            {
                ["relativePath"] = "foo.txt",
                ["content"] = "hello"
            }, cts.Token));

        Assert.Equal(0, filter.PendingFunctionCount);
    }

    [Fact]
    public async Task PluginLevelEditFailures_TripTheEditFailureCircuit()
    {
        // The preview validates old_text against a snapshot captured at interception.
        // When an earlier edit in the same batch changes the file in between, the
        // preview PASSES but the plugin's re-validation fails ("Could not find ...").
        // Those plugin-level failures must count toward the N=3 circuit — they used to
        // bypass it entirely, allowing 9+ consecutive misses on one path.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Real file so the filter captures old content and runs the preview;
            // old_text below exists uniquely, so the preview always passes.
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "target.txt"), "hello world");

            var filter = new FunctionInvocationFilter(0, new ProjectRootAccessor(tempRoot));
            filter.OnWriteApprovalRequested = (_, _, _) =>
                Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });

            // The "plugin": always fails the way FileSystemPlugin.EditFile does live.
            var (kernel, _) = BuildKernel(filter,
                method: (string relativePath, string old_text, string new_text) =>
                    $"Error: Could not find the specified text in {relativePath}.",
                functionName: "edit_file");

            using var scope = filter.BeginScope();

            KernelArguments Args(int i) => new()
            {
                ["relativePath"] = "target.txt",
                ["old_text"] = "hello world",
                ["new_text"] = $"variant {i}" // varies so the call-dedup cache never matches
            };

            for (var i = 0; i < InvocationScope.EditFailureCircuitThreshold; i++)
            {
                var r = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["edit_file"], Args(i));
                Assert.StartsWith("Error:", r.ToString());
            }

            // Failure counts key on the NORMALIZED (absolute) path so aliases share a counter.
            var normalizedKey = Path.GetFullPath(Path.Combine(tempRoot, "target.txt"));
            Assert.Equal(InvocationScope.EditFailureCircuitThreshold, scope.GetEditFailureCount(normalizedKey));

            // The next attempt must be refused by the circuit, before any execution.
            var tripped = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["edit_file"], Args(99));
            Assert.Contains("Edit-failure circuit tripped", tripped.ToString());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RangedReads_DifferentRanges_AreNotRedundant_SameRangeIs()
    {
        // Paging through a large file (startLine=1..400, then 401..800) must never trip
        // the redundant-read circuit — the dedup key includes the range. Re-reading the
        // SAME range with no intervening write still trips it.
        var filter = new FunctionInvocationFilter(0);
        var (kernel, _) = BuildKernel(filter,
            method: (string relativePath, int startLine, int endLine) =>
                $"File: {relativePath} (lines {startLine}-{endLine} of 1000)\n...",
            functionName: "read_file_contents");

        using var scope = filter.BeginScope();

        KernelArguments Args(int start, int end) => new()
        {
            ["relativePath"] = "big.txt",
            ["startLine"] = start,
            ["endLine"] = end
        };

        var page1 = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["read_file_contents"], Args(1, 400));
        Assert.DoesNotContain("already read", page1.ToString());

        var page2 = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["read_file_contents"], Args(401, 800));
        Assert.DoesNotContain("already read", page2.ToString());

        var repeat = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["read_file_contents"], Args(1, 400));
        Assert.Contains("already read", repeat.ToString());
    }

    [Fact]
    public async Task EditFailures_AcrossPathAliases_ShareOneCircuitCounter()
    {
        // Observed live: a model addressed the same file as both
        // "src/MandoCode/bin/Debug/net8.0/Games/index.html" and "Games/index.html"
        // (the plugin's StripRedundantRootPrefix resolves both to one file). Raw-string
        // bookkeeping kept two separate failure counters, so the circuit never tripped.
        // Normalized keys must make failures under one alias trip the circuit for the other.
        var tempRoot = Path.Combine(Path.GetTempPath(), "mandocode-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Games"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "Games", "index.html"), "hello world");

            var filter = new FunctionInvocationFilter(0, new ProjectRootAccessor(tempRoot));
            filter.OnWriteApprovalRequested = (_, _, _) =>
                Task.FromResult(new DiffApprovalResult { Response = DiffApprovalResponse.Approved });

            var (kernel, _) = BuildKernel(filter,
                method: (string relativePath, string old_text, string new_text) =>
                    $"Error: Could not find the specified text in {relativePath}.",
                functionName: "edit_file");

            using var scope = filter.BeginScope();

            // The redundant-root alias: "<rootLastSegment>/Games/index.html" resolves to
            // the same file as "Games/index.html" via StripRedundantRootPrefix.
            var alias = $"{Path.GetFileName(tempRoot)}/Games/index.html";

            KernelArguments Args(string path, int i) => new()
            {
                ["relativePath"] = path,
                ["old_text"] = "hello world",
                ["new_text"] = $"variant {i}"
            };

            for (var i = 0; i < InvocationScope.EditFailureCircuitThreshold; i++)
            {
                var r = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["edit_file"], Args(alias, i));
                Assert.StartsWith("Error:", r.ToString());
            }

            // Fourth attempt via the OTHER alias must hit the shared circuit.
            var tripped = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["edit_file"], Args("Games/index.html", 99));
            Assert.Contains("Edit-failure circuit tripped", tripped.ToString());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SecondProposePlan_InSameTurn_IsShortCircuited()
    {
        // Observed runaway: a model completed a plan, then immediately proposed ANOTHER
        // round of unrequested work in the same turn. PlanHandoff's recursion guard only
        // covers a plan that's still running — the scope-level circuit must catch repeats
        // after the first plan finishes.
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => Task.FromResult("plan executed")
        };
        var filter = new FunctionInvocationFilter(0, null, null, handoff);
        var (kernel, _) = BuildKernel(filter,
            method: (string goal, string steps) => "should never run — intercepted",
            functionName: "propose_plan");

        using var scope = filter.BeginScope();

        var args = new KernelArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        };

        var first = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["propose_plan"], args);
        Assert.Contains("plan executed", first.ToString());
        Assert.True(scope.PlanAlreadyProcessed);

        var second = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["propose_plan"], args);
        Assert.Contains("already proposed", second.ToString());
        Assert.DoesNotContain("plan executed", second.ToString());
    }

    [Fact]
    public async Task MalformedProposal_DoesNotConsumeThePlanSlot()
    {
        // An empty/unparseable steps payload must stay retryable — only a REAL plan
        // (≥1 step) consumes the one-plan-per-turn slot.
        var handoff = new PlanHandoff
        {
            OnPlanRequested = (_, _) => Task.FromResult("plan executed")
        };
        var filter = new FunctionInvocationFilter(0, null, null, handoff);
        var (kernel, _) = BuildKernel(filter,
            method: (string goal, string steps) => "should never run — intercepted",
            functionName: "propose_plan");

        using var scope = filter.BeginScope();

        var malformed = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["propose_plan"], new KernelArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "not json at all"
        });
        Assert.False(scope.PlanAlreadyProcessed);

        var retry = await kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["propose_plan"], new KernelArguments
        {
            ["goal"] = "build a game",
            ["steps"] = "[{\"description\":\"step one\",\"instruction\":\"do the thing\"}]"
        });
        Assert.Contains("plan executed", retry.ToString());
        Assert.True(scope.PlanAlreadyProcessed);
    }
}
