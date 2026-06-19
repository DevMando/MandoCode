using System.Text;
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
}
