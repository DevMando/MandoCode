using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MandoCode.Services;

/// <summary>
/// SK ChatHistory &lt;-&gt; MEAI ChatMessage conversion for the SK -> Agent Framework migration
/// (feat/agent-framework-migration — see memory agent-framework-migration.md).
///
/// <see cref="ToMeaiMessages"/> (Phase 4) validated that MandoCode's real exported history
/// round-trips through MEAI's content types. <see cref="ToSkMessages"/> (the live cutover) is
/// the reverse direction: <see cref="AIService"/> keeps <c>_chatHistory</c> (SK's ChatHistory) as
/// the sole mutable source of truth — compaction, export/import, and pre-flight size estimation
/// all keep working unmodified — and converts to/from MEAI messages only around the actual model
/// call. There's no AgentSession/stateful-accumulation involved: verified empirically that
/// AIAgent.RunAsync(IEnumerable&lt;ChatMessage&gt;) is stateless when called without a session —
/// the model call is a pure function of whatever message list you pass, exactly like SK's
/// GetChatMessageContentAsync(history, ...) today.
/// </summary>
public static class ChatHistoryConversion
{
    /// <summary>
    /// Converts a full SK ChatHistory to MEAI ChatMessages, including the system message
    /// (unlike AIService.ExportHistoryJson, which deliberately excludes it — the system prompt
    /// is rebuilt fresh on every session rather than persisted). Function calls/results living in
    /// <see cref="ChatMessageContent.Items"/> (SK's auto-invoke loop puts them there, not in
    /// <c>Content</c>) are preserved as MEAI FunctionCallContent/FunctionResultContent — the same
    /// content SK's own polymorphic serialization round-trips, just re-typed.
    /// </summary>
    public static List<ChatMessage> ToMeaiMessages(ChatHistory history)
    {
        var result = new List<ChatMessage>(history.Count);
        foreach (var msg in history)
        {
            result.Add(ToMeaiMessage(msg));
        }
        return result;
    }

    private static ChatMessage ToMeaiMessage(ChatMessageContent msg)
    {
        var role = msg.Role switch
        {
            var r when r == AuthorRole.System => ChatRole.System,
            var r when r == AuthorRole.User => ChatRole.User,
            var r when r == AuthorRole.Assistant => ChatRole.Assistant,
            var r when r == AuthorRole.Tool => ChatRole.Tool,
            _ => ChatRole.User
        };

        var contents = new List<AIContent>();

        // Plain Content wins when present — matches FormatMessageForSummary's own fallback
        // order (Content first, Items only when Content is empty), so both code paths agree
        // on which representation is authoritative for a given message.
        if (!string.IsNullOrEmpty(msg.Content))
        {
            contents.Add(new Microsoft.Extensions.AI.TextContent(msg.Content));
        }

        if (msg.Items != null)
        {
            foreach (var item in msg.Items)
            {
                switch (item)
                {
                    case Microsoft.SemanticKernel.TextContent tc when string.IsNullOrEmpty(msg.Content):
                        contents.Add(new Microsoft.Extensions.AI.TextContent(tc.Text));
                        break;

                    case Microsoft.SemanticKernel.FunctionCallContent fc:
                        contents.Add(new Microsoft.Extensions.AI.FunctionCallContent(
                            callId: fc.Id ?? Guid.NewGuid().ToString(),
                            name: fc.FunctionName,
                            arguments: fc.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value)));
                        break;

                    case Microsoft.SemanticKernel.FunctionResultContent fr:
                        contents.Add(new Microsoft.Extensions.AI.FunctionResultContent(
                            callId: fr.CallId ?? Guid.NewGuid().ToString(),
                            result: fr.Result));
                        break;
                }
            }
        }

        return new ChatMessage(role, contents);
    }

    /// <summary>
    /// Converts MEAI ChatMessages back to SK ChatMessageContent, for appending an AIAgent
    /// response's new messages onto <c>_chatHistory</c>. Mirrors <see cref="ToMeaiMessage"/> in
    /// reverse: each MEAI content item becomes the equivalent SK KernelContent item. pluginName
    /// is always null on the resulting SK function-call/result content — MAF has no plugin
    /// concept, so there's nothing meaningful to put there, and nothing downstream (dedup keys,
    /// display formatting) reads FunctionCallContent.PluginName off history entries.
    /// </summary>
    public static List<ChatMessageContent> ToSkMessages(IEnumerable<ChatMessage> messages)
    {
        var result = new List<ChatMessageContent>();
        foreach (var msg in messages)
        {
            result.Add(ToSkMessage(msg));
        }
        return result;
    }

    private static ChatMessageContent ToSkMessage(ChatMessage msg)
    {
        var role = msg.Role switch
        {
            var r when r == ChatRole.System => AuthorRole.System,
            var r when r == ChatRole.User => AuthorRole.User,
            var r when r == ChatRole.Assistant => AuthorRole.Assistant,
            var r when r == ChatRole.Tool => AuthorRole.Tool,
            _ => AuthorRole.User
        };

        var items = new ChatMessageContentItemCollection();
        foreach (var content in msg.Contents)
        {
            switch (content)
            {
                case Microsoft.Extensions.AI.TextContent tc:
                    items.Add(new Microsoft.SemanticKernel.TextContent(tc.Text));
                    break;

                case Microsoft.Extensions.AI.FunctionCallContent fc:
                    items.Add(new Microsoft.SemanticKernel.FunctionCallContent(
                        functionName: fc.Name,
                        pluginName: null,
                        id: fc.CallId,
                        arguments: fc.Arguments != null
                            ? new KernelArguments(fc.Arguments)
                            : null));
                    break;

                case Microsoft.Extensions.AI.FunctionResultContent fr:
                    items.Add(new Microsoft.SemanticKernel.FunctionResultContent(
                        functionName: null,
                        pluginName: null,
                        callId: fr.CallId,
                        result: fr.Result));
                    break;
            }
        }

        return new ChatMessageContent(role, items);
    }
}
