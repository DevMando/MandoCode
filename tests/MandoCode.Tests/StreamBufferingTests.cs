using System.Runtime.CompilerServices;
using MandoCode.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Deterministic coverage for the streaming → buffered-result layer (the watchdog-heartbeat
/// path). Feeds canned streams through <see cref="StreamBuffering.BufferAsync"/> — no live model,
/// no agent — to lock in the behavior the live spike proved against the real connector.
///
/// This used to also cover an SK-side overload (<c>IAsyncEnumerable&lt;StreamingChatMessageContent&gt;</c>)
/// alongside this one; that overload was deleted in the final SK cleanup
/// (feat/agent-framework-migration) once its only caller (the also-deleted <c>InvokeChatAsync</c>)
/// was gone. MAF's own <see cref="AgentResponseExtensions.ToAgentResponseAsync"/> does the actual
/// accumulation now, so these tests cover the heartbeat wrapper and cancellation propagation;
/// they deliberately don't re-verify the framework's own accumulation behavior.
/// </summary>
public class StreamBufferingTests
{
    private static AgentResponseUpdate Chunk(string? text) => new(ChatRole.Assistant, text);

    private static async IAsyncEnumerable<AgentResponseUpdate> ToStream(params AgentResponseUpdate[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BufferAsync_ConcatenatesTextInOrder()
    {
        var result = await StreamBuffering.BufferAsync(
            ToStream(Chunk("The "), Chunk("secret "), Chunk("number "), Chunk("is 42")),
            onChunk: () => { });

        Assert.Equal("The secret number is 42", result.Text);
    }

    [Fact]
    public async Task BufferAsync_FiresHeartbeatOncePerChunk_IncludingEmptyOnes()
    {
        // Empty-content chunks (a tool-call round, a metadata-only final chunk) are still proof
        // of life, so they must tick the heartbeat — that's what keeps the watchdog satisfied
        // across a tool round mid-stream.
        var beats = 0;
        var result = await StreamBuffering.BufferAsync(
            ToStream(Chunk("a"), Chunk(""), Chunk(null), Chunk("b")),
            onChunk: () => beats++);

        Assert.Equal(4, beats);
        Assert.Equal("ab", result.Text);
    }

    [Fact]
    public async Task BufferAsync_EmptyStream_ReturnsEmptyText()
    {
        var result = await StreamBuffering.BufferAsync(ToStream(), onChunk: () => { });
        Assert.True(string.IsNullOrEmpty(result.Text));
    }

    [Fact]
    public async Task BufferAsync_PropagatesCancellation()
    {
        // The stall watchdog cancels via the linked token; that must surface as an
        // OperationCanceledException so AIService can classify it as a ModelStallException.
        using var cts = new CancellationTokenSource();

        static async IAsyncEnumerable<AgentResponseUpdate> CancelAwareStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "first");
            ct.ThrowIfCancellationRequested();   // cancelled by the onChunk below before we get here
            yield return new AgentResponseUpdate(ChatRole.Assistant, "second");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await StreamBuffering.BufferAsync(
                CancelAwareStream(),
                onChunk: () => cts.Cancel(),   // cancel after the first chunk
                cancellationToken: cts.Token));
    }
}
