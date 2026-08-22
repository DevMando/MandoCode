using System.Runtime.CompilerServices;
using MandoCode.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Deterministic coverage for the streaming → buffered-result layer (the watchdog-heartbeat
/// path). Feeds canned streams through <see cref="StreamBuffering.BufferAsync"/> — no live model,
/// no kernel — to lock in the behavior the live spike proved against the real connector.
/// </summary>
public class StreamBufferingTests
{
    private static StreamingChatMessageContent Chunk(string? content, object? innerContent = null, string? modelId = null)
        => new(AuthorRole.Assistant, content, innerContent: innerContent, modelId: modelId);

    private static async IAsyncEnumerable<StreamingChatMessageContent> ToStream(
        params StreamingChatMessageContent[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BufferAsync_ConcatenatesContentInOrder()
    {
        var result = await StreamBuffering.BufferAsync(
            ToStream(Chunk("The "), Chunk("secret "), Chunk("number "), Chunk("is 42")),
            onChunk: () => { });

        Assert.Equal("The secret number is 42", result.Content);
        Assert.Equal(AuthorRole.Assistant, result.Role);
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
        Assert.Equal("ab", result.Content);
    }

    [Fact]
    public async Task BufferAsync_CarriesFinalChunkMetadataForTokenTracking()
    {
        // ExtractAndRecordTokens reads InnerContent (the Ollama ChatDoneResponseStream) off the
        // result. The LAST chunk carries it, so buffering must surface the final chunk's
        // InnerContent/ModelId — or streaming would silently break token tracking.
        var doneMarker = new object();
        var result = await StreamBuffering.BufferAsync(
            ToStream(
                Chunk("Hello ", modelId: "glm-5.2:cloud"),
                Chunk("world", innerContent: doneMarker, modelId: "glm-5.2:cloud")),
            onChunk: () => { });

        Assert.Equal("Hello world", result.Content);
        Assert.Same(doneMarker, result.InnerContent);
        Assert.Equal("glm-5.2:cloud", result.ModelId);
    }

    [Fact]
    public async Task BufferAsync_EmptyStream_ReturnsEmptyContent()
    {
        var result = await StreamBuffering.BufferAsync(ToStream(), onChunk: () => { });
        Assert.Equal("", result.Content);
    }

    [Fact]
    public async Task BufferAsync_PropagatesCancellation()
    {
        // The stall watchdog cancels via the linked token; that must surface as an
        // OperationCanceledException so AIService can classify it as a ModelStallException.
        using var cts = new CancellationTokenSource();

        static async IAsyncEnumerable<StreamingChatMessageContent> CancelAwareStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return Chunk("first");
            ct.ThrowIfCancellationRequested();   // cancelled by the onChunk below before we get here
            yield return Chunk("second");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await StreamBuffering.BufferAsync(
                CancelAwareStream(),
                onChunk: () => cts.Cancel(),   // cancel after the first chunk
                cts.Token));
    }

    // ============================================================
    // MAF-side overload (feat/agent-framework-migration, Phase 5) — same heartbeat contract,
    // but accumulation itself is MAF's own AgentResponseExtensions.ToAgentResponseAsync, not
    // hand-rolled here. These tests cover the heartbeat wrapper and cancellation propagation;
    // they deliberately don't re-verify the framework's own accumulation behavior.
    // ============================================================

    private static AgentResponseUpdate MeaiChunk(string? text) => new(ChatRole.Assistant, text);

    private static async IAsyncEnumerable<AgentResponseUpdate> ToMeaiStream(params AgentResponseUpdate[] items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BufferAsync_Meai_ConcatenatesTextInOrder()
    {
        var result = await StreamBuffering.BufferAsync(
            ToMeaiStream(MeaiChunk("The "), MeaiChunk("secret "), MeaiChunk("number "), MeaiChunk("is 42")),
            onChunk: () => { });

        Assert.Equal("The secret number is 42", result.Text);
    }

    [Fact]
    public async Task BufferAsync_Meai_FiresHeartbeatOncePerChunk_IncludingEmptyOnes()
    {
        var beats = 0;
        var result = await StreamBuffering.BufferAsync(
            ToMeaiStream(MeaiChunk("a"), MeaiChunk(""), MeaiChunk(null), MeaiChunk("b")),
            onChunk: () => beats++);

        Assert.Equal(4, beats);
        Assert.Equal("ab", result.Text);
    }

    [Fact]
    public async Task BufferAsync_Meai_EmptyStream_ReturnsEmptyText()
    {
        var result = await StreamBuffering.BufferAsync(ToMeaiStream(), onChunk: () => { });
        Assert.True(string.IsNullOrEmpty(result.Text));
    }

    [Fact]
    public async Task BufferAsync_Meai_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();

        static async IAsyncEnumerable<AgentResponseUpdate> CancelAwareStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "first");
            ct.ThrowIfCancellationRequested();
            yield return new AgentResponseUpdate(ChatRole.Assistant, "second");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await StreamBuffering.BufferAsync(
                CancelAwareStream(),
                onChunk: () => cts.Cancel(),
                cts.Token));
    }
}
