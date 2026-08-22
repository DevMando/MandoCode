using Xunit;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// Verifies the exact accumulation pattern AIService.ExecuteAgentModelCallAsync uses to avoid
/// losing the record of tool calls that genuinely completed before a later failure (e.g. context
/// overflow partway through a multi-round tool-calling turn) — feat/agent-framework-migration.
///
/// This doesn't reflection-invoke ExecuteAgentModelCallAsync itself (that would need a real
/// _agent/network call to exercise meaningfully); it verifies the pattern standalone, against
/// the real AgentFunctionMiddleware class, using the same event-subscription shape the
/// production code uses. AgentFunctionMiddlewareLifecycleTests already proves OnFunctionInvoked/
/// OnFunctionCompleted fire correctly per call; this proves an accumulator subscribed across
/// MULTIPLE calls on the same middleware instance correctly retains earlier successes when a
/// later step in the same logical turn fails.
/// </summary>
public class PartialTraceAccumulationTests
{
    private static AIFunction Fn(Delegate method, string name) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { Name = name });

    [Fact]
    public async Task Trace_retains_earlier_successful_calls_after_a_later_failure()
    {
        var middleware = new AgentFunctionMiddleware(5);

        // Exact shape from ExecuteAgentModelCallAsync: subscribe, accumulate, record on
        // exception, unsubscribe in finally.
        var partialTrace = new List<string>();
        void OnInvoked(FunctionCall call) => partialTrace.Add($"called {call.FunctionName}()");
        void OnCompleted(FunctionExecutionResult result) => partialTrace.Add($"{result.FunctionName} → {result.Result}");

        middleware.OnFunctionInvoked += OnInvoked;
        middleware.OnFunctionCompleted += OnCompleted;

        List<string>? lastCallPartialTrace = null;
        try
        {
            try
            {
                // Round 1 and round 2 of "this turn" both succeed.
                await AgentMiddlewareTestHelpers.InvokeAsync(middleware, Fn(() => "result-one", "tool_one"));
                await AgentMiddlewareTestHelpers.InvokeAsync(middleware, Fn(() => "result-two", "tool_two"));

                // Round 3 is where the OUTER call fails (e.g. the provider rejected a
                // context-overflowed request) — this happens at the HTTP/connector layer, not
                // inside any one tool, so it's simulated here as a plain throw rather than
                // routed through the middleware.
                throw new InvalidOperationException("simulated context overflow");
            }
            catch
            {
                if (partialTrace.Count > 0)
                    lastCallPartialTrace = new List<string>(partialTrace);
                throw;
            }
        }
        catch (InvalidOperationException)
        {
            // expected — the point of this test is what survives, not the exception itself
        }
        finally
        {
            middleware.OnFunctionInvoked -= OnInvoked;
            middleware.OnFunctionCompleted -= OnCompleted;
        }

        Assert.NotNull(lastCallPartialTrace);
        Assert.Equal(4, lastCallPartialTrace!.Count);
        Assert.Equal("called tool_one()", lastCallPartialTrace[0]);
        Assert.Equal("tool_one → result-one", lastCallPartialTrace[1]);
        Assert.Equal("called tool_two()", lastCallPartialTrace[2]);
        Assert.Equal("tool_two → result-two", lastCallPartialTrace[3]);
    }

    [Fact]
    public async Task Trace_is_null_when_nothing_completed_before_the_failure()
    {
        // The other side of the fix: a failure on the VERY FIRST round (before any tool call
        // completed) must NOT produce a misleadingly-non-empty trace — the caller falls back to
        // SynthesizeHistorySummary in that case, exactly as before this fix.
        var middleware = new AgentFunctionMiddleware(5);
        var partialTrace = new List<string>();
        void OnInvoked(FunctionCall call) => partialTrace.Add($"called {call.FunctionName}()");
        void OnCompleted(FunctionExecutionResult result) => partialTrace.Add($"{result.FunctionName} → {result.Result}");

        middleware.OnFunctionInvoked += OnInvoked;
        middleware.OnFunctionCompleted += OnCompleted;

        List<string>? lastCallPartialTrace = null;
        try
        {
            try
            {
                throw new InvalidOperationException("fails before any tool call");
            }
            catch
            {
                if (partialTrace.Count > 0)
                    lastCallPartialTrace = new List<string>(partialTrace);
                throw;
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            middleware.OnFunctionInvoked -= OnInvoked;
            middleware.OnFunctionCompleted -= OnCompleted;
        }

        Assert.Null(lastCallPartialTrace);
    }

    [Fact]
    public async Task Handlers_are_unsubscribed_after_the_call_so_a_later_unrelated_call_does_not_pollute_the_trace()
    {
        var middleware = new AgentFunctionMiddleware(5);

        // First "ExecuteAgentModelCallAsync call": subscribe, run one tool, succeed, unsubscribe.
        var firstTrace = new List<string>();
        void OnInvoked1(FunctionCall call) => firstTrace.Add(call.FunctionName);
        middleware.OnFunctionInvoked += OnInvoked1;
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, Fn(() => "ok", "first_call_tool"));
        middleware.OnFunctionInvoked -= OnInvoked1;

        // Second, unrelated call: a fresh accumulator must see ONLY its own tool call.
        var secondTrace = new List<string>();
        void OnInvoked2(FunctionCall call) => secondTrace.Add(call.FunctionName);
        middleware.OnFunctionInvoked += OnInvoked2;
        await AgentMiddlewareTestHelpers.InvokeAsync(middleware, Fn(() => "ok", "second_call_tool"));
        middleware.OnFunctionInvoked -= OnInvoked2;

        Assert.Equal(["first_call_tool"], firstTrace);
        Assert.Equal(["second_call_tool"], secondTrace);
    }
}
