using System.Text;
using MandoCode.Acp.Protocol;
using MandoCode.Models;

namespace MandoCode.Services.Acp;

/// <summary>
/// Collects what an agent replays during <c>session/load</c> into plain messages: consecutive chunks of the same
/// speaker become one message. Only user and assistant text is kept; tool calls, thoughts, plans and the rest of the
/// replay are not shown again.
/// </summary>
public sealed class AcpHistoryCollector
{
    private readonly object _gate = new();
    private readonly List<ChatMsg> _messages = [];
    private string? _role;
    private readonly StringBuilder _text = new();

    /// <summary>Called for every replayed update, in order (it may run on the connection's notification thread).</summary>
    public void Add(SessionUpdate update)
    {
        var (role, text) = update switch
        {
            UserMessageChunk { Content: TextContent user } => ("user", user.Text),
            AgentMessageChunk { Content: TextContent agent } => ("assistant", agent.Text),
            _ => (null, null),
        };

        if (role is null || text is null)
            return;

        lock (_gate)
        {
            if (_role != role)
                Flush();
            _role = role;
            _text.Append(text);
        }
    }

    public IReadOnlyList<ChatMsg> Messages()
    {
        lock (_gate)
        {
            Flush();
            return _messages.ToArray();
        }
    }

    private void Flush()
    {
        if (_role is not null && _text.ToString().Trim() is { Length: > 0 } text)
            _messages.Add(new ChatMsg { Role = _role, Text = text });
        _role = null;
        _text.Clear();
    }
}
