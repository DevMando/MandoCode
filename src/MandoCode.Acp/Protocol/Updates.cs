using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

/// <summary>The <c>session/update</c> notification.</summary>
public sealed record SessionNotification : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("update")] public SessionUpdate Update { get; set; } = new UnknownSessionUpdate(null, default);
}

/// <summary>
/// The payload of <c>session/update</c>, discriminated by <c>sessionUpdate</c>. Read-only on the
/// client side: this client never sends one. Variants this client does not know (for example
/// <c>usage_update</c>) deserialize to <see cref="UnknownSessionUpdate"/>.
/// </summary>
[JsonConverter(typeof(SessionUpdateConverter))]
public abstract record SessionUpdate : AcpObject;

public abstract record ContentChunkUpdate : SessionUpdate
{
    [JsonPropertyName("content")] public ContentBlock Content { get; set; } = new TextContent();
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
}

public sealed record UserMessageChunk : ContentChunkUpdate;
public sealed record AgentMessageChunk : ContentChunkUpdate;
public sealed record AgentThoughtChunk : ContentChunkUpdate;

public sealed record ToolCallLocation : AcpObject
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("line")] public int? Line { get; set; }
}

/// <summary>Tool-call content, discriminated by <c>type</c>. Unknown types become <see cref="UnknownToolCallContent"/>.</summary>
[JsonConverter(typeof(ToolCallContentConverter))]
public abstract record ToolCallContent : AcpObject;

public sealed record ContentToolCallContent : ToolCallContent
{
    [JsonPropertyName("content")] public ContentBlock Content { get; set; } = new TextContent();
}

public sealed record DiffToolCallContent : ToolCallContent
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("oldText")] public string? OldText { get; set; }
    [JsonPropertyName("newText")] public string NewText { get; set; } = "";
}

public sealed record TerminalToolCallContent : ToolCallContent
{
    [JsonPropertyName("terminalId")] public string TerminalId { get; set; } = "";
}

public sealed record UnknownToolCallContent(string? Kind, JsonElement Raw) : ToolCallContent;

/// <summary>
/// Fields shared by <c>tool_call</c> and <c>tool_call_update</c>. Everything except the id is
/// optional in updates, so all of it is nullable here; <see cref="ToolCall"/> sets the defaults
/// the schema documents (<c>kind: other</c>, <c>status: pending</c>) via <c>GetEffectiveKind()</c> /
/// <c>GetEffectiveStatus()</c>.
/// </summary>
public abstract record ToolCallBase : SessionUpdate
{
    [JsonPropertyName("toolCallId")] public string ToolCallId { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>One of <see cref="ToolKinds"/>, or a value from a newer agent.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    /// <summary>One of <see cref="ToolCallStatuses"/>, or a value from a newer agent.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("content")] public IReadOnlyList<ToolCallContent>? Content { get; set; }
    [JsonPropertyName("locations")] public IReadOnlyList<ToolCallLocation>? Locations { get; set; }
    [JsonPropertyName("rawInput")] public JsonElement? RawInput { get; set; }
    [JsonPropertyName("rawOutput")] public JsonElement? RawOutput { get; set; }

    /// <summary>The kind, falling back to the schema default (<see cref="ToolKinds.Other"/>) when the agent omitted it.</summary>
    public string GetEffectiveKind() => Kind ?? ToolKinds.Other;
}

public sealed record ToolCall : ToolCallBase
{
    /// <summary>The status, falling back to the schema default (<see cref="ToolCallStatuses.Pending"/>) when omitted.</summary>
    public string GetEffectiveStatus() => Status ?? ToolCallStatuses.Pending;
}

public sealed record ToolCallUpdate : ToolCallBase;

public sealed record PlanEntry : AcpObject
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    /// <summary>One of <see cref="PlanEntryPriorities"/>.</summary>
    [JsonPropertyName("priority")] public string Priority { get; set; } = "";
    /// <summary>One of <see cref="PlanEntryStatuses"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

/// <summary>A full replacement of the agent's plan (the <c>plan</c> update always carries every entry).</summary>
public sealed record Plan : SessionUpdate
{
    [JsonPropertyName("entries")] public IReadOnlyList<PlanEntry> Entries { get; set; } = [];
}

public sealed record UnstructuredCommandInput : AcpObject
{
    [JsonPropertyName("hint")] public string Hint { get; set; } = "";
}

public sealed record AvailableCommand : AcpObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("input")] public UnstructuredCommandInput? Input { get; set; }
}

public sealed record AvailableCommandsUpdate : SessionUpdate
{
    [JsonPropertyName("availableCommands")] public IReadOnlyList<AvailableCommand> AvailableCommands { get; set; } = [];
}

public sealed record CurrentModeUpdate : SessionUpdate
{
    [JsonPropertyName("currentModeId")] public string CurrentModeId { get; set; } = "";
}

/// <summary>A <c>session/update</c> variant this client does not know. The raw JSON is preserved; consumers ignore it.</summary>
public sealed record UnknownSessionUpdate(string? Kind, JsonElement Raw) : SessionUpdate;
