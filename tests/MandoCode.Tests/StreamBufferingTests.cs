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
    public async Task BufferAsync_DistinctAssistantMessagesAreNotDuplicated()
    {
        var deltas = new List<string>();
        var result = await StreamBuffering.BufferAsync(ToStream(
            new AgentResponseUpdate(ChatRole.Assistant, "I will inspect the form.") { MessageId = "before-tool" },
            new AgentResponseUpdate(ChatRole.Assistant, "I found the fields.") { MessageId = "after-tool" }),
            onChunk: () => { }, onText: deltas.Add);
        Assert.Equal(new[] { "I will inspect the form.", "I found the fields." }, deltas);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Text, "I will inspect the form"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Text, "I found the fields"));
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

    // Shaped like the OllamaSharp connector's last chunk: the done payload rides on the chat update.
    private static AgentResponseUpdate DoneChunk(long evalDurationNs, string doneReason) => new()
    {
        RawRepresentation = new ChatResponseUpdate
        {
            RawRepresentation = new OllamaSharp.Models.Chat.ChatDoneResponseStream
            {
                Done = true,
                DoneReason = doneReason,
                EvalDuration = evalDurationNs,
            },
        },
    };

    [Fact]
    public async Task FrameworkAccumulator_KeepsNoRawRepresentation()
    {
        // Why BufferAsync attaches the done chunk itself: without it, a streamed reply had no
        // timing (no tok/s) and no stop reason (no cut-off notice).
        var plain = await ToStream(Chunk("hi"), DoneChunk(1_000_000_000, "stop")).ToAgentResponseAsync();
        Assert.Null(DoneStreamLocator.Find(plain));
    }

    [Fact]
    public async Task BufferAsync_KeepsTheDoneChunk_ForTimingAndStopReason()
    {
        var result = await StreamBuffering.BufferAsync(
            ToStream(Chunk("cut "), Chunk("off"), DoneChunk(2_000_000_000, "length")),
            onChunk: () => { });

        var done = DoneStreamLocator.Find(result);
        Assert.NotNull(done);
        Assert.Equal(2_000_000_000, done!.EvalDuration);
        Assert.Equal("length", done.DoneReason);
        Assert.Equal("cut off", result.Text);
    }

    [Fact]
    public async Task BufferAsync_KeepsTheLastDoneChunk_WhenToolRoundsMakeSeveral()
    {
        var result = await StreamBuffering.BufferAsync(
            ToStream(Chunk("checking"), DoneChunk(1_000_000_000, "stop"), Chunk("answer"), DoneChunk(3_000_000_000, "stop")),
            onChunk: () => { });

        Assert.Equal(3_000_000_000, DoneStreamLocator.Find(result)!.EvalDuration);
    }

    [Fact]
    public async Task BufferAsync_WithoutADoneChunk_LeavesNoRawRepresentation()
    {
        var result = await StreamBuffering.BufferAsync(ToStream(Chunk("hi")), onChunk: () => { });
        Assert.Null(DoneStreamLocator.Find(result));
    }

    [Fact]
    public void Locator_ReadsTheNonStreamingShape()
    {
        var done = new OllamaSharp.Models.Chat.ChatDoneResponseStream { Done = true, EvalDuration = 5 };
        var response = new AgentResponse(new ChatResponse { RawRepresentation = done });
        Assert.Same(done, DoneStreamLocator.Find(response));
    }
}
