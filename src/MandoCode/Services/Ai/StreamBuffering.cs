using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;

namespace MandoCode.Services;

/// <summary>
/// Consumes a streamed agent response into a single non-streaming-shaped
/// <see cref="AgentResponse"/>, firing a per-chunk callback as it goes.
///
/// This is the core of the "stream for a watchdog heartbeat, render at the end" approach:
/// the caller's <paramref name="onChunk"/> resets the stall watchdog on every chunk (so a
/// long-but-healthy generation never false-positives), while the assembled result is identical
/// to what the non-streaming API would have returned — so the fallback parser, token recording,
/// and every downstream consumer behave exactly as before.
///
/// Extracted from <see cref="AIService"/> so the buffering/heartbeat logic can be unit-tested
/// against canned streams without a live model or agent. Does NOT hand-roll the accumulation:
/// <see cref="AgentResponseExtensions.ToAgentResponseAsync"/> is MAF's own built-in
/// stream-to-response accumulator, so this is a thin heartbeat wrapper around it rather than a
/// duplicate of framework logic.
/// </summary>
public static class StreamBuffering
{
    /// <param name="stream">The streamed chunks.</param>
    /// <param name="onChunk">Invoked once per chunk BEFORE it's appended — the watchdog heartbeat.</param>
    /// <param name="onText">
    /// Optional, invoked with each chunk's text as it arrives. Lets a caller show progress while a
    /// long generation is still running; the assembled result is unaffected either way.
    /// </param>
    /// <param name="cancellationToken">Cancels enumeration; an <see cref="OperationCanceledException"/> propagates.</param>
    public static Task<AgentResponse> BufferAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        Action onChunk,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default) =>
        WithHeartbeat(stream, onChunk, onText, cancellationToken).ToAgentResponseAsync(cancellationToken);

    private static async IAsyncEnumerable<AgentResponseUpdate> WithHeartbeat(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        Action onChunk,
        Action<string>? onText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            onChunk();

            if (onText != null)
            {
                var text = update.Text;
                // Never let a progress display break the generation it is reporting on.
                if (!string.IsNullOrEmpty(text))
                {
                    try { onText(text); } catch { }
                }
            }

            yield return update;
        }
    }
}
