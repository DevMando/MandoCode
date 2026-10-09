using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MandoCode.Services;

/// <summary>Compatibility boundary for persisted chat data. Never imports a foreign system prompt.</summary>
internal static class ConversationHistoryCodec
{
    public static string? Export(IEnumerable<ChatMessage> history)
    {
        try
        {
            var messages = history.Where(message => message.Role != ChatRole.System).ToList();
            return messages.Count == 0 ? null : JsonSerializer.Serialize(messages);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Conversation history could not be serialized: {0}", ex.Message);
            return null;
        }
    }

    public static IReadOnlyList<ChatMessage> Read(string json)
    {
        try
        {
            var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json);
            return messages?.Where(message => message is not null && message.Role != ChatRole.System).ToArray() ?? [];
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Conversation history could not be restored: {0}", ex.Message);
            return [];
        }
    }
}
