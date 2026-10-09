using Microsoft.Extensions.AI;

namespace MandoCode.Services;

/// <summary>Transient evidence for one turn or plan attempt. Does not own conversation history.</summary>
internal sealed class PendingImageEvidence
{
    private readonly List<AIContent> _pending = [];
    private int _deliveries;

    public bool TryAttach(ReadOnlyMemory<byte> bytes, string mediaType, string caption, out string error)
    {
        lock (_pending)
        {
            if (_deliveries >= AIService.MaxImageDeliveriesPerTurn)
            {
                error = "The image-delivery limit for this turn or plan attempt was reached. Use the images already supplied or DOM observations.";
                return false;
            }
            if (_pending.Count >= 8)
            {
                error = "Too many images are already queued for this turn.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(caption)) _pending.Add(new TextContent(caption));
            _pending.Add(new DataContent(bytes, mediaType));
        }
        error = string.Empty;
        return true;
    }

    public ChatMessage? Take()
    {
        lock (_pending)
        {
            if (_pending.Count == 0) return null;
            List<AIContent> contents = [new TextContent(
                "Host-captured image input follows. Describe only what is actually visible in it."), .. _pending];
            _pending.Clear();
            _deliveries++;
            return new ChatMessage(ChatRole.User, contents);
        }
    }

    public void Clear()
    {
        lock (_pending) _pending.Clear();
    }

    public void ResetBudget()
    {
        lock (_pending) _deliveries = 0;
    }
}
