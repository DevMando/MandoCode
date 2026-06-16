using Xunit;
using Microsoft.SemanticKernel;
using ArdinCode.Services;

namespace ArdinCode.Tests;

/// <summary>
/// Regression tests for the duplicate web-call circuit. Observed live: a model fired the
/// SAME search_web query 40+ times in a single plan step, burning the whole turn's token
/// budget on byte-identical Tavily responses. Web results are individually small, so the
/// result-char budget circuit never tripped, and the 2s time-window cache misses when a
/// slow cloud model spaces the calls further apart. The per-scope dedup set is the guard.
/// </summary>
public class WebCallDedupCircuitTests
{
    private static (Kernel Kernel, FunctionInvocationFilter Filter, List<string> Calls) BuildSearchKernel(
        Func<string, string>? handler = null)
    {
        var calls = new List<string>();
        var filter = new FunctionInvocationFilter(5);
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromFunctions("TestPlugin", new[]
        {
            KernelFunctionFactory.CreateFromMethod(
                (string query) => { calls.Add(query); return handler?.Invoke(query) ?? $"results for {query}"; },
                "search_web")
        });
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(filter);
        return (kernel, filter, calls);
    }

    private static (Kernel Kernel, FunctionInvocationFilter Filter, List<string> Calls) BuildFetchKernel()
    {
        var calls = new List<string>();
        var filter = new FunctionInvocationFilter(5);
        var builder = Kernel.CreateBuilder();
        builder.Plugins.AddFromFunctions("TestPlugin", new[]
        {
            KernelFunctionFactory.CreateFromMethod(
                (string url) => { calls.Add(url); return $"page at {url}"; },
                "fetch_webpage")
        });
        var kernel = builder.Build();
        kernel.FunctionInvocationFilters.Add(filter);
        return (kernel, filter, calls);
    }

    private static Task<FunctionResult> InvokeSearch(Kernel kernel, string query) =>
        kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["search_web"],
            new KernelArguments { ["query"] = query });

    private static Task<FunctionResult> InvokeFetch(Kernel kernel, string url) =>
        kernel.InvokeAsync(kernel.Plugins["TestPlugin"]["fetch_webpage"],
            new KernelArguments { ["url"] = url });

    [Fact]
    public async Task SecondIdenticalSearch_IsRefused_WithoutInvoking()
    {
        var (kernel, filter, calls) = BuildSearchKernel();
        using var scope = filter.BeginScope();

        await InvokeSearch(kernel, "Star Fox 64 Arwing design");
        var second = await InvokeSearch(kernel, "Star Fox 64 Arwing design");

        Assert.Single(calls); // the plugin ran exactly once
        Assert.Contains("already ran this exact web search", second.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManyIdenticalSearches_OnlyRunOnce()
    {
        // The exact observed pathology: dozens of byte-identical calls in one scope.
        var (kernel, filter, calls) = BuildSearchKernel();
        using var scope = filter.BeginScope();

        for (var i = 0; i < 40; i++)
            await InvokeSearch(kernel, "Star Fox 64 supply ring silver gold");

        Assert.Single(calls);
    }

    [Fact]
    public async Task WhitespaceOnlyDifference_IsTreatedAsDuplicate()
    {
        var (kernel, filter, calls) = BuildSearchKernel();
        using var scope = filter.BeginScope();

        await InvokeSearch(kernel, "arwing  design   colors");
        await InvokeSearch(kernel, "arwing design colors"); // collapsed whitespace

        Assert.Single(calls);
    }

    [Fact]
    public async Task DifferentQuery_IsAllowed()
    {
        var (kernel, filter, calls) = BuildSearchKernel();
        using var scope = filter.BeginScope();

        await InvokeSearch(kernel, "arwing design");
        await InvokeSearch(kernel, "corneria level layout");

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task FetchWebpage_SameUrl_IsRefused()
    {
        var (kernel, filter, calls) = BuildFetchKernel();
        using var scope = filter.BeginScope();

        await InvokeFetch(kernel, "https://example.com/arwing");
        var second = await InvokeFetch(kernel, "https://example.com/arwing");

        Assert.Single(calls);
        Assert.Contains("already ran this exact web page fetch", second.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshScope_DoesNotRefuseRepeatSearch()
    {
        // Each plan step gets its own scope — a query legitimately repeated for a
        // different sub-task must NOT be refused by this circuit. (Asserting on the
        // refusal message rather than invocation count isolates THIS circuit from the
        // pre-existing 2s time-window cache, which would also dedup calls fired this
        // fast — in production the steps are seconds apart, so it never interferes.)
        var (kernel, filter, calls) = BuildSearchKernel();

        using (var step1 = filter.BeginScope())
            await InvokeSearch(kernel, "arwing design");

        string second;
        using (var step2 = filter.BeginScope())
            second = (await InvokeSearch(kernel, "arwing design")).ToString();

        Assert.DoesNotContain("already ran this exact web", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedSearch_IsNotRecorded_NotRefusedOnRetry()
    {
        // A search that errored (DuckDuckGo 403, etc.) stays retryable — only successful,
        // byte-identical repeats are the loop we're guarding against. Asserting on the
        // refusal message (not invocation count) isolates this circuit from the 2s cache.
        var attempt = 0;
        var (kernel, filter, calls) = BuildSearchKernel(_ =>
            ++attempt == 1 ? "Error: search provider rate-limited" : "results");
        using var scope = filter.BeginScope();

        await InvokeSearch(kernel, "arwing design"); // errors → must not be recorded
        var retry = (await InvokeSearch(kernel, "arwing design")).ToString();

        Assert.DoesNotContain("already ran this exact web", retry, StringComparison.OrdinalIgnoreCase);
    }
}
