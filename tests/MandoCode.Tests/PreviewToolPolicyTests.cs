using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PreviewToolPolicyTests
{
    [Theory]
    [InlineData("inspect_desktop_preview")]
    [InlineData("observe_desktop_preview")]
    [InlineData("screenshot_desktop_preview")]
    [InlineData("press_key_desktop_preview")]
    [InlineData("click_desktop_preview")]
    [InlineData("hover_desktop_preview")]
    [InlineData("fill_desktop_preview")]
    [InlineData("select_desktop_preview")]
    [InlineData("scroll_desktop_preview")]
    [InlineData("wait_for_desktop_preview")]
    [InlineData("open_desktop_preview")]
    [InlineData("refresh_desktop_preview")]
    public async Task LiveOperationsAreNeverReplayedFromCache(string name)
    {
        var middleware = new AgentFunctionMiddleware(5);
        var calls = 0;
        var tool = AIFunctionFactory.Create(() => { calls++; return "{\"ok\":true}"; }, new AIFunctionFactoryOptions { Name = name });
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, tool);
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, tool);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BrowserFailureIsReportedAsFailureInTheTranscript()
    {
        var middleware = new AgentFunctionMiddleware(5);
        FunctionExecutionResult? completed = null;
        middleware.OnFunctionCompleted += result => completed = result;
        var tool = AIFunctionFactory.Create(() => "{\"ok\":false,\"error\":\"selector is ambiguous\"}", new AIFunctionFactoryOptions { Name = "click_desktop_preview" });
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, tool);
        Assert.False(completed!.Success);
    }

    [Theory]
    [InlineData("inspect_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", true)]
    [InlineData("click_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", true)]
    [InlineData("observe_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", true)]
    [InlineData("screenshot_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", true)]
    [InlineData("press_key_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", true)]
    [InlineData("wait_for_desktop_preview", "{\"ok\":true,\"readyState\":\"interactive\"}", true)]
    [InlineData("open_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", false)]
    [InlineData("refresh_desktop_preview", "{\"ok\":true,\"readyState\":\"complete\"}", false)]
    [InlineData("inspect_desktop_preview", "{\"ok\":false}", false)]
    [InlineData("inspect_desktop_preview", "{\"ok\":true,\"readyState\":\"loading\"}", false)]
    [InlineData("inspect_desktop_preview", "{\"ok\":true}", false)]
    [InlineData("inspect_desktop_preview", "page unavailable", false)]
    public void FreshnessRequiresAnActualBrowserObservation(string name, string result, bool fresh)
    {
        ChatMessage[] history = [
            new(ChatRole.Assistant, [new FunctionCallContent("edit", "edit_file", new Dictionary<string, object?> { ["path"] = "index.html" })]),
            new(ChatRole.Tool, [new FunctionResultContent("edit", "edited")]),
            new(ChatRole.Assistant, [new FunctionCallContent("browser", name)]),
            new(ChatRole.Tool, [new FunctionResultContent("browser", result)])
        ];
        Assert.Equal(fresh, PlanToolEvidence.AssessFreshness(history) == null);
    }
}
