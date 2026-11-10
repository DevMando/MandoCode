using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

// Why hand-written converters instead of [JsonPolymorphic]:
//  * on net8 System.Text.Json requires the discriminator to be the FIRST property of the object
//    (AllowOutOfOrderMetadataProperties only arrived in net9), but JSON-RPC peers are free to
//    order properties however they like;
//  * an unknown discriminator value throws, while ACP requires clients to ignore variants they
//    do not understand (new session/update kinds are added regularly).
// These converters find the discriminator in any position, deserialize the matching concrete
// type through the source-generated AcpJsonContext (no reflection), and degrade to an
// Unknown* value for unrecognised or malformed variants.

/// <summary>Shared logic for unions discriminated by a string property.</summary>
internal abstract class DiscriminatedUnionConverter<T> : JsonConverter<T> where T : AcpObject
{
    protected abstract string DiscriminatorName { get; }

    /// <summary>Deserializes the variant for <paramref name="kind"/>, or returns null when the kind is unknown.</summary>
    protected abstract T? ReadKnown(string kind, JsonElement root);

    protected abstract T CreateUnknown(string? kind, JsonElement raw);

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        string? kind = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(DiscriminatorName, out var discriminator)
            && discriminator.ValueKind == JsonValueKind.String)
        {
            kind = discriminator.GetString();
        }

        if (kind is null)
            return CreateUnknown(null, root.Clone());

        try
        {
            return ReadKnown(kind, root) ?? CreateUnknown(kind, root.Clone());
        }
        catch (JsonException)
        {
            // A known variant with a malformed body must not take the whole stream down.
            return CreateUnknown(kind, root.Clone());
        }
    }
}

internal sealed class ContentBlockConverter : DiscriminatedUnionConverter<ContentBlock>
{
    protected override string DiscriminatorName => "type";

    protected override ContentBlock? ReadKnown(string kind, JsonElement root) => kind switch
    {
        "text" => root.Deserialize(AcpJsonContext.Default.TextContent),
        "image" => root.Deserialize(AcpJsonContext.Default.ImageContent),
        "audio" => root.Deserialize(AcpJsonContext.Default.AudioContent),
        "resource_link" => root.Deserialize(AcpJsonContext.Default.ResourceLink),
        "resource" => root.Deserialize(AcpJsonContext.Default.EmbeddedResource),
        _ => null,
    };

    protected override ContentBlock CreateUnknown(string? kind, JsonElement raw) => new UnknownContentBlock(kind, raw);

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TextContent v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.TextContent); break;
            case ImageContent v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.ImageContent); break;
            case AudioContent v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.AudioContent); break;
            case ResourceLink v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.ResourceLink); break;
            case EmbeddedResource v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.EmbeddedResource); break;
            case UnknownContentBlock v: v.Raw.WriteTo(writer); break;
            default: throw new NotSupportedException($"Unsupported content block type {value.GetType().Name}.");
        }
    }
}

/// <summary>Read-only: the client never sends <c>session/update</c>.</summary>
internal sealed class SessionUpdateConverter : DiscriminatedUnionConverter<SessionUpdate>
{
    protected override string DiscriminatorName => "sessionUpdate";

    protected override SessionUpdate? ReadKnown(string kind, JsonElement root) => kind switch
    {
        "user_message_chunk" => root.Deserialize(AcpJsonContext.Default.UserMessageChunk),
        "agent_message_chunk" => root.Deserialize(AcpJsonContext.Default.AgentMessageChunk),
        "agent_thought_chunk" => root.Deserialize(AcpJsonContext.Default.AgentThoughtChunk),
        "tool_call" => root.Deserialize(AcpJsonContext.Default.ToolCall),
        "tool_call_update" => root.Deserialize(AcpJsonContext.Default.ToolCallUpdate),
        "plan" => root.Deserialize(AcpJsonContext.Default.Plan),
        "available_commands_update" => root.Deserialize(AcpJsonContext.Default.AvailableCommandsUpdate),
        "current_mode_update" => root.Deserialize(AcpJsonContext.Default.CurrentModeUpdate),
        _ => null,
    };

    protected override SessionUpdate CreateUnknown(string? kind, JsonElement raw) => new UnknownSessionUpdate(kind, raw);

    public override void Write(Utf8JsonWriter writer, SessionUpdate value, JsonSerializerOptions options) =>
        throw new NotSupportedException("session/update is sent by agents only; this client never serializes it.");
}

/// <summary>Read-only: tool-call content is produced by agents.</summary>
internal sealed class ToolCallContentConverter : DiscriminatedUnionConverter<ToolCallContent>
{
    protected override string DiscriminatorName => "type";

    protected override ToolCallContent? ReadKnown(string kind, JsonElement root) => kind switch
    {
        "content" => root.Deserialize(AcpJsonContext.Default.ContentToolCallContent),
        "diff" => root.Deserialize(AcpJsonContext.Default.DiffToolCallContent),
        "terminal" => root.Deserialize(AcpJsonContext.Default.TerminalToolCallContent),
        _ => null,
    };

    protected override ToolCallContent CreateUnknown(string? kind, JsonElement raw) => new UnknownToolCallContent(kind, raw);

    public override void Write(Utf8JsonWriter writer, ToolCallContent value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Tool-call content is sent by agents only; this client never serializes it.");
}

/// <summary>
/// The text/blob distinction carries no discriminator: a payload with a <c>blob</c> field is a
/// <see cref="BlobResourceContents"/>, anything else is treated as <see cref="TextResourceContents"/>.
/// </summary>
internal sealed class EmbeddedResourceContentsConverter : JsonConverter<EmbeddedResourceContents>
{
    public override EmbeddedResourceContents? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Embedded resource contents must be an object.");

        return root.TryGetProperty("blob", out _)
            ? root.Deserialize(AcpJsonContext.Default.BlobResourceContents)
            : root.Deserialize(AcpJsonContext.Default.TextResourceContents);
    }

    public override void Write(Utf8JsonWriter writer, EmbeddedResourceContents value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TextResourceContents v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.TextResourceContents); break;
            case BlobResourceContents v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.BlobResourceContents); break;
            default: throw new NotSupportedException($"Unsupported resource contents type {value.GetType().Name}.");
        }
    }
}

/// <summary>Reads and writes the <c>cancelled</c> / <c>selected</c> permission outcome (discriminator <c>outcome</c>).</summary>
internal sealed class RequestPermissionOutcomeConverter : JsonConverter<RequestPermissionOutcome>
{
    public override RequestPermissionOutcome? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        string? kind = root.ValueKind == JsonValueKind.Object
                       && root.TryGetProperty("outcome", out var p)
                       && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        return kind switch
        {
            "cancelled" => root.Deserialize(AcpJsonContext.Default.CancelledPermissionOutcome),
            "selected" => root.Deserialize(AcpJsonContext.Default.SelectedPermissionOutcome),
            _ => throw new JsonException($"Unknown permission outcome '{kind}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, RequestPermissionOutcome value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case CancelledPermissionOutcome v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.CancelledPermissionOutcome); break;
            case SelectedPermissionOutcome v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.SelectedPermissionOutcome); break;
            default: throw new NotSupportedException($"Unsupported permission outcome {value.GetType().Name}.");
        }
    }
}

/// <summary><c>http</c> / <c>sse</c> servers carry a <c>type</c>; stdio servers do not.</summary>
internal sealed class McpServerConverter : JsonConverter<McpServer>
{
    public override McpServer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        string? kind = root.ValueKind == JsonValueKind.Object
                       && root.TryGetProperty("type", out var p)
                       && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        return kind switch
        {
            "http" => root.Deserialize(AcpJsonContext.Default.McpServerHttp),
            "sse" => root.Deserialize(AcpJsonContext.Default.McpServerSse),
            _ => root.Deserialize(AcpJsonContext.Default.McpServerStdio),
        };
    }

    public override void Write(Utf8JsonWriter writer, McpServer value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case McpServerStdio v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.McpServerStdio); break;
            case McpServerHttp v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.McpServerHttp); break;
            case McpServerSse v: JsonSerializer.Serialize(writer, v, AcpJsonContext.Default.McpServerSse); break;
            default: throw new NotSupportedException($"Unsupported MCP server type {value.GetType().Name}.");
        }
    }
}
