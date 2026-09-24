using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp.Models.Chat;

namespace MandoCode.Services;

/// <summary>
/// Finds Ollama's final "done" chunk — generation timing (<c>EvalDuration</c>, the tok/s figure) and
/// why generation stopped (<c>DoneReason</c>, "length" for a reply cut off at the token limit) — in
/// whichever shape it arrives. A non-streaming call carries it on the response's
/// <see cref="ChatResponse"/>; a streamed call carries it on its last chunk, which
/// <see cref="StreamBuffering.BufferAsync"/> attaches to the assembled response directly, because
/// MAF's accumulator keeps no raw representation of its own.
/// </summary>
public static class DoneStreamLocator
{
    public static ChatDoneResponseStream? Find(AgentResponse response) => FromRaw(response.RawRepresentation);

    public static ChatDoneResponseStream? FromRaw(object? raw) => raw switch
    {
        ChatDoneResponseStream done => done,
        ChatResponse response => FromRaw(response.RawRepresentation),
        ChatResponseUpdate update => FromRaw(update.RawRepresentation),
        AgentResponseUpdate update => FromRaw(update.RawRepresentation),
        _ => null,
    };
}
