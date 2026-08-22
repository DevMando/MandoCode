using MandoCode.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MandoCode.Tests;

/// <summary>
/// Shared harness for driving AgentFunctionMiddleware.InterceptAsync directly in tests —
/// the MAF equivalent of the existing SK tests' "BuildKernel + kernel.InvokeAsync" pattern.
/// No live model, no real AIAgent needed: we construct a FunctionInvocationContext by hand and
/// call the middleware delegate exactly the way MAF's FunctionInvokingChatClient would.
/// </summary>
internal static class AgentMiddlewareTestHelpers
{
    /// <summary>
    /// Invokes <paramref name="middleware"/> for a single function call, with `next` wired to
    /// actually execute the underlying AIFunction (mirrors SK's kernel.InvokeAsync running the
    /// real plugin method through the filter).
    /// </summary>
    public static async Task<object?> InvokeAsync(
        AgentFunctionMiddleware middleware,
        AIFunction function,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var context = new FunctionInvocationContext
        {
            Function = function,
            Arguments = arguments ?? new AIFunctionArguments(),
        };

        return await middleware.InterceptAsync(
            agent: null!, // AgentFunctionMiddleware.InterceptAsync never reads its `agent` parameter.
            context,
            next: (ctx, ct) => ctx.Function.InvokeAsync(ctx.Arguments, ct),
            cancellationToken);
    }
}
