using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MandoCode.Services;

/// <summary>
/// Consumes a streamed chat response into a single non-streaming-shaped
/// <see cref="ChatMessageContent"/>, firing a per-chunk callback as it goes.
///
/// This is the core of the "stream for a watchdog heartbeat, render at the end" approach:
/// the caller's <paramref name="onChunk"/> resets the stall watchdog on every chunk (so a
/// long-but-healthy generation never false-positives), while the assembled result is identical
/// to what the non-streaming API would have returned — so the fallback parser, token recording,
/// and every downstream consumer behave exactly as before.
///
/// Extracted from <see cref="AIService"/> so the buffering/heartbeat logic can be unit-tested
/// against canned streams without a live model or kernel.
/// </summary>
public static class StreamBuffering
{
    /// <param name="stream">The streamed chunks (e.g. from <c>GetStreamingChatMessageContentsAsync</c>).</param>
    /// <param name="onChunk">Invoked once per chunk BEFORE it's appended — the watchdog heartbeat.</param>
    /// <param name="cancellationToken">Cancels enumeration; an <see cref="OperationCanceledException"/> propagates.</param>
    public static async Task<ChatMessageContent> BufferAsync(
        IAsyncEnumerable<StreamingChatMessageContent> stream,
        Action onChunk,
        CancellationToken cancellationToken = default)
    {
        var buffer = new StringBuilder();
        StreamingChatMessageContent? last = null;

        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            // Heartbeat first: a chunk arriving at all is proof of life, even if its Content
            // is empty (a tool-call round or a metadata-only final chunk still counts).
            onChunk();

            last = chunk;
            if (!string.IsNullOrEmpty(chunk.Content))
                buffer.Append(chunk.Content);
        }

        // Carry the FINAL chunk's InnerContent/Metadata/ModelId onto the result. For the Ollama
        // connector the last chunk is the ChatDoneResponseStream that ExtractAndRecordTokens reads
        // eval counts from — so token tracking keeps working with zero changes.
        return new ChatMessageContent(AuthorRole.Assistant, buffer.ToString())
        {
            ModelId = last?.ModelId,
            Metadata = last?.Metadata,
            InnerContent = last?.InnerContent
        };
    }

    /// <summary>
    /// MAF-side sibling for the SK -> Agent Framework migration (feat/agent-framework-migration,
    /// Phase 5). Same heartbeat contract as the SK overload above — <paramref name="onChunk"/>
    /// fires once per update, before accumulation, so the stall watchdog resets on proof of
    /// life even for an empty/metadata-only chunk. Unlike the SK overload, this does NOT
    /// hand-roll the accumulation: <see cref="AgentResponseExtensions.ToAgentResponseAsync"/> is
    /// MAF's own built-in stream-to-response accumulator, so this is a thin heartbeat wrapper
    /// around it rather than a duplicate of framework logic. Not yet called by anything — ready
    /// for whenever the live chat path switches from _chatService's streaming to _agent's.
    /// </summary>
    public static Task<AgentResponse> BufferAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        Action onChunk,
        CancellationToken cancellationToken = default) =>
        WithHeartbeat(stream, onChunk, cancellationToken).ToAgentResponseAsync(cancellationToken);

    private static async IAsyncEnumerable<AgentResponseUpdate> WithHeartbeat(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        Action onChunk,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            onChunk();
            yield return update;
        }
    }
}
