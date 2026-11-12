using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

/// <summary>
/// What the agent can do with sessions beyond <c>session/new</c>. A capability is present when the agent sent an
/// object for it (<c>{}</c> counts), absent when it sent nothing or null.
/// </summary>
public sealed record SessionCapabilities : AcpObject
{
    [JsonPropertyName("list")] public JsonElement? List { get; set; }
    [JsonPropertyName("resume")] public JsonElement? Resume { get; set; }
    [JsonPropertyName("close")] public JsonElement? Close { get; set; }

    // Methods, not properties: they are not part of the wire format.
    public bool CanList() => IsPresent(List);
    public bool CanResume() => IsPresent(Resume);
    public bool CanClose() => IsPresent(Close);

    private static bool IsPresent(JsonElement? value) => value is { ValueKind: JsonValueKind.Object };
}

/// <summary>One value a config option can take.</summary>
public sealed record SessionConfigSelectOption : AcpObject
{
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
}

/// <summary>A group of values under a header (the schema allows <c>options</c> to be flat or grouped).</summary>
public sealed record SessionConfigSelectGroup : AcpObject
{
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("options")] public IReadOnlyList<SessionConfigSelectOption> Options { get; set; } = [];
}

/// <summary>
/// A session setting the agent exposes (model, mode, reasoning effort, ...). Only the <c>select</c> kind is used by
/// this client; other kinds (for example <c>boolean</c>) are kept as they arrive and ignored.
/// </summary>
public sealed record SessionConfigOption : AcpObject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>One of <see cref="ConfigOptionCategories"/>, or an agent-specific name; null when uncategorised.</summary>
    [JsonPropertyName("category")] public string? Category { get; set; }

    /// <summary><c>select</c> or <c>boolean</c>.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("currentValue")] public JsonElement? CurrentValue { get; set; }
    [JsonPropertyName("options")] public JsonElement? Options { get; set; }

    [JsonIgnore] public bool IsSelect => string.Equals(Type, "select", StringComparison.Ordinal);

    /// <summary>The current value of a select option; null for other kinds.</summary>
    [JsonIgnore]
    public string? CurrentSelection => IsSelect && CurrentValue is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>The selectable values of a select option, groups flattened (a group's name is kept in <see cref="ConfigChoice.Group"/>).</summary>
    public IReadOnlyList<ConfigChoice> Choices()
    {
        if (!IsSelect || Options is not { ValueKind: JsonValueKind.Array } options)
            return [];

        var result = new List<ConfigChoice>();
        foreach (var item in options.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("options", out var grouped) && grouped.ValueKind == JsonValueKind.Array)
            {
                var groupName = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                foreach (var inner in grouped.EnumerateArray())
                    if (ReadChoice(inner, groupName) is { } choice)
                        result.Add(choice);
            }
            else if (ReadChoice(item, null) is { } flat)
            {
                result.Add(flat);
            }
        }

        return result;
    }

    private static ConfigChoice? ReadChoice(JsonElement item, string? group)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return new ConfigChoice(value.GetString()!, string.IsNullOrEmpty(name) ? value.GetString()! : name, description, group);
    }
}

/// <summary>A value a select option accepts, as shown to the user.</summary>
public sealed record ConfigChoice(string Value, string Name, string? Description, string? Group);

/// <summary>Semantic categories of config options (UX hints; agents may add their own, starting with an underscore).</summary>
public static class ConfigOptionCategories
{
    public const string Mode = "mode";
    public const string Model = "model";
    public const string ModelConfig = "model_config";
    public const string ThoughtLevel = "thought_level";
}

public sealed record SetSessionConfigOptionRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("configId")] public string ConfigId { get; set; } = "";

    /// <summary>The value id; the request carries no <c>type</c>, which the protocol reads as a value id.</summary>
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public sealed record SetSessionConfigOptionResponse : AcpObject
{
    [JsonPropertyName("configOptions")] public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];
}

/// <summary>A <c>config_option_update</c> session update: the full set of options and their current values.</summary>
public sealed record ConfigOptionUpdate : SessionUpdate
{
    [JsonPropertyName("configOptions")] public IReadOnlyList<SessionConfigOption> ConfigOptions { get; set; } = [];
}

public sealed record ResumeSessionRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("mcpServers")] public IReadOnlyList<McpServer> McpServers { get; set; } = [];
}

public sealed record ResumeSessionResponse : AcpObject
{
    [JsonPropertyName("modes")] public SessionModeState? Modes { get; set; }
    [JsonPropertyName("configOptions")] public IReadOnlyList<SessionConfigOption>? ConfigOptions { get; set; }
}

public sealed record CloseSessionRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
}

public sealed record CloseSessionResponse : AcpObject;

public sealed record ListSessionsRequest : AcpObject
{
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("cursor")] public string? Cursor { get; set; }
}

public sealed record SessionInfo : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
}

public sealed record ListSessionsResponse : AcpObject
{
    [JsonPropertyName("sessions")] public IReadOnlyList<SessionInfo> Sessions { get; set; } = [];
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
}
