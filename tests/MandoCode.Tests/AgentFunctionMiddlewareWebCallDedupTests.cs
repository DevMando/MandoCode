using Xunit;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// MAF-side sibling of WebCallDedupCircuitTests (feat/agent-framework-migration, Phase 6).
/// </summary>
public class AgentFunctionMiddlewareWebCallDedupTests
{
    private static (AgentFunctionMiddleware Middleware, Microsoft.Extensions.AI.AIFunction Fn, List<string> Calls) BuildSearch(
        Func<string, string>? handler = null)
    {
        var calls = new List<string>();
        var middleware = new AgentFunctionMiddleware(5);
        var fn = AIFunctionFactory.Create(
            (string query) => { calls.Add(query); return handler?.Invoke(query) ?? $"results for {query}"; },
            new AIFunctionFactoryOptions { Name = "search_web" });
        return (middleware, fn, calls);
    }

    private static (AgentFunctionMiddleware Middleware, Microsoft.Extensions.AI.AIFunction Fn, List<string> Calls) BuildFetch()
    {
        var calls = new List<string>();
        var middleware = new AgentFunctionMiddleware(5);
        var fn = AIFunctionFactory.Create(
            (string url) => { calls.Add(url); return $"page at {url}"; },
            new AIFunctionFactoryOptions { Name = "fetch_webpage" });
        return (middleware, fn, calls);
    }

    private static Task<object?> InvokeSearch(AgentFunctionMiddleware middleware, Microsoft.Extensions.AI.AIFunction fn, string query) =>
        AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["query"] = query });

    private static Task<object?> InvokeFetch(AgentFunctionMiddleware middleware, Microsoft.Extensions.AI.AIFunction fn, string url) =>
        AgentMiddlewareTestHelpers.InvokeAsync(middleware, fn, new AIFunctionArguments { ["url"] = url });

    [Fact]
    public async Task SecondIdenticalSearch_IsRefused_WithoutInvoking()
    {
        var (middleware, fn, calls) = BuildSearch();
        using var scope = middleware.BeginScope();

        await InvokeSearch(middleware, fn, "Star Fox 64 Arwing design");
        var second = await InvokeSearch(middleware, fn, "Star Fox 64 Arwing design");

        Assert.Single(calls);
        Assert.Contains("already ran this exact web search", second?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManyIdenticalSearches_OnlyRunOnce()
    {
        var (middleware, fn, calls) = BuildSearch();
        using var scope = middleware.BeginScope();

        for (var i = 0; i < 40; i++)
            await InvokeSearch(middleware, fn, "Star Fox 64 supply ring silver gold");

        Assert.Single(calls);
    }

    [Fact]
    public async Task WhitespaceOnlyDifference_IsTreatedAsDuplicate()
    {
        var (middleware, fn, calls) = BuildSearch();
        using var scope = middleware.BeginScope();

        await InvokeSearch(middleware, fn, "arwing  design   colors");
        await InvokeSearch(middleware, fn, "arwing design colors");

        Assert.Single(calls);
    }

    [Fact]
    public async Task DifferentQuery_IsAllowed()
    {
        var (middleware, fn, calls) = BuildSearch();
        using var scope = middleware.BeginScope();

        await InvokeSearch(middleware, fn, "arwing design");
        await InvokeSearch(middleware, fn, "corneria level layout");

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task FetchWebpage_SameUrl_IsRefused()
    {
        var (middleware, fn, calls) = BuildFetch();
        using var scope = middleware.BeginScope();

        await InvokeFetch(middleware, fn, "https://example.com/arwing");
        var second = await InvokeFetch(middleware, fn, "https://example.com/arwing");

        Assert.Single(calls);
        Assert.Contains("already ran this exact web page fetch", second?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FreshScope_DoesNotRefuseRepeatSearch()
    {
        var (middleware, fn, calls) = BuildSearch();

        using (var step1 = middleware.BeginScope())
            await InvokeSearch(middleware, fn, "arwing design");

        string? second;
        using (var step2 = middleware.BeginScope())
            second = (await InvokeSearch(middleware, fn, "arwing design"))?.ToString();

        Assert.DoesNotContain("already ran this exact web", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedSearch_IsNotRecorded_NotRefusedOnRetry()
    {
        var attempt = 0;
        var (middleware, fn, calls) = BuildSearch(_ =>
            ++attempt == 1 ? "Error: search provider rate-limited" : "results");
        using var scope = middleware.BeginScope();

        await InvokeSearch(middleware, fn, "arwing design");
        var retry = (await InvokeSearch(middleware, fn, "arwing design"))?.ToString();

        Assert.DoesNotContain("already ran this exact web", retry, StringComparison.OrdinalIgnoreCase);
    }
}
