using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

public sealed record Annotations : AcpObject
{
    [JsonPropertyName("audience")] public IReadOnlyList<string>? Audience { get; set; }
    [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
    [JsonPropertyName("priority")] public double? Priority { get; set; }
}

/// <summary>
/// A piece of prompt or message content, discriminated by <c>type</c>. Values with an
/// unrecognised <c>type</c> deserialize to <see cref="UnknownContentBlock"/>.
/// </summary>
[JsonConverter(typeof(ContentBlockConverter))]
public abstract record ContentBlock : AcpObject;

public sealed record TextContent : ContentBlock
{
    [JsonPropertyName("type")] public string Type => "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("annotations")] public Annotations? Annotations { get; set; }

    public TextContent() { }
    public TextContent(string text) => Text = text;
}

public sealed record ImageContent : ContentBlock
{
    [JsonPropertyName("type")] public string Type => "image";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("annotations")] public Annotations? Annotations { get; set; }
}

public sealed record AudioContent : ContentBlock
{
    [JsonPropertyName("type")] public string Type => "audio";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
    [JsonPropertyName("annotations")] public Annotations? Annotations { get; set; }
}

public sealed record ResourceLink : ContentBlock
{
    [JsonPropertyName("type")] public string Type => "resource_link";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("annotations")] public Annotations? Annotations { get; set; }
}

public sealed record EmbeddedResource : ContentBlock
{
    [JsonPropertyName("type")] public string Type => "resource";
    [JsonPropertyName("resource")] public EmbeddedResourceContents Resource { get; set; } = new TextResourceContents();
    [JsonPropertyName("annotations")] public Annotations? Annotations { get; set; }
}

/// <summary>The payload of an <see cref="EmbeddedResource"/>: text or a base64 blob (told apart by which field is present).</summary>
[JsonConverter(typeof(EmbeddedResourceContentsConverter))]
public abstract record EmbeddedResourceContents : AcpObject;

public sealed record TextResourceContents : EmbeddedResourceContents
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
}

public sealed record BlobResourceContents : EmbeddedResourceContents
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = "";
    [JsonPropertyName("blob")] public string Blob { get; set; } = "";
    [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
}

/// <summary>A content block whose <c>type</c> this client does not know. The raw JSON is preserved.</summary>
public sealed record UnknownContentBlock(string? Kind, JsonElement Raw) : ContentBlock;
