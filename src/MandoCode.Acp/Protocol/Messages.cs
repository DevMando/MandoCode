using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

/// <summary>
/// Base of every protocol object: carries the optional <c>_meta</c> extension bag.
/// Incoming DTOs are deliberately lenient (no <c>required</c> members): a missing field
/// becomes a default value instead of failing the whole message.
/// <para>
/// Properties use <c>{ get; set; }</c>, not <c>init</c>, on purpose: on net8 the System.Text.Json
/// source generator routes types with init-only properties through a "parameterized constructor"
/// path in which fields absent from the JSON overwrite property initializers with <c>null</c>
/// (see <c>AcpProtocolTests.MissingFields_KeepInitializerDefaults</c>).
/// </para>
/// </summary>
public abstract record AcpObject
{
    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; set; }
}

// ───────────────────────── initialize ─────────────────────────

public sealed record Implementation : AcpObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed record FileSystemCapabilities : AcpObject
{
    [JsonPropertyName("readTextFile")] public bool ReadTextFile { get; set; }
    [JsonPropertyName("writeTextFile")] public bool WriteTextFile { get; set; }
}

public sealed record ClientCapabilities : AcpObject
{
    [JsonPropertyName("fs")] public FileSystemCapabilities Fs { get; set; } = new();
    [JsonPropertyName("terminal")] public bool Terminal { get; set; }
}

public sealed record InitializeRequest : AcpObject
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; } = AcpProtocol.Version;
    [JsonPropertyName("clientCapabilities")] public ClientCapabilities? ClientCapabilities { get; set; }
    [JsonPropertyName("clientInfo")] public Implementation? ClientInfo { get; set; }
}

public sealed record PromptCapabilities : AcpObject
{
    [JsonPropertyName("image")] public bool Image { get; set; }
    [JsonPropertyName("audio")] public bool Audio { get; set; }
    [JsonPropertyName("embeddedContext")] public bool EmbeddedContext { get; set; }
}

public sealed record McpCapabilities : AcpObject
{
    [JsonPropertyName("http")] public bool Http { get; set; }
    [JsonPropertyName("sse")] public bool Sse { get; set; }
}

public sealed record AgentCapabilities : AcpObject
{
    [JsonPropertyName("loadSession")] public bool LoadSession { get; set; }
    [JsonPropertyName("promptCapabilities")] public PromptCapabilities PromptCapabilities { get; set; } = new();
    [JsonPropertyName("mcpCapabilities")] public McpCapabilities McpCapabilities { get; set; } = new();
    private SessionCapabilities? _sessionCapabilities;
    /// <summary>Never null: an agent may send <c>null</c> (or nothing) for it, which means no extra session features.</summary>
    [JsonPropertyName("sessionCapabilities")] public SessionCapabilities SessionCapabilities { get => _sessionCapabilities ??= new(); set => _sessionCapabilities = value; }
}

/// <summary>
/// An authentication method advertised by the agent. The schema models this as a union
/// (<c>terminal</c> | agent); only the common fields are kept because this client never
/// calls <c>authenticate</c> - the agent's own login flow does the work.
/// </summary>
public sealed record AuthMethod : AcpObject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

public sealed record InitializeResponse : AcpObject
{
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("agentCapabilities")] public AgentCapabilities AgentCapabilities { get; set; } = new();
    [JsonPropertyName("authMethods")] public IReadOnlyList<AuthMethod> AuthMethods { get; set; } = [];
    [JsonPropertyName("agentInfo")] public Implementation? AgentInfo { get; set; }
}

public sealed record AuthenticateRequest : AcpObject
{
    [JsonPropertyName("methodId")] public string MethodId { get; set; } = "";
}

public sealed record AuthenticateResponse : AcpObject;

// ───────────────────────── sessions ─────────────────────────

public sealed record EnvVariable : AcpObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public sealed record HttpHeader : AcpObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

/// <summary>
/// An MCP server handed to the agent. This client always sends an empty list (MandoCode's own
/// MCP servers are not forwarded); the types exist so the request shape matches the schema.
/// </summary>
[JsonConverter(typeof(McpServerConverter))]
public abstract record McpServer : AcpObject;

public sealed record McpServerStdio : McpServer
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public IReadOnlyList<string> Args { get; set; } = [];
    [JsonPropertyName("env")] public IReadOnlyList<EnvVariable> Env { get; set; } = [];
}

public sealed record McpServerHttp : McpServer
{
    [JsonPropertyName("type")] public string Type => "http";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("headers")] public IReadOnlyList<HttpHeader> Headers { get; set; } = [];
}

public sealed record McpServerSse : McpServer
{
    [JsonPropertyName("type")] public string Type => "sse";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("headers")] public IReadOnlyList<HttpHeader> Headers { get; set; } = [];
}

public sealed record NewSessionRequest : AcpObject
{
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("mcpServers")] public IReadOnlyList<McpServer> McpServers { get; set; } = [];
}

public sealed record SessionMode : AcpObject
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed record SessionModeState : AcpObject
{
    [JsonPropertyName("currentModeId")] public string CurrentModeId { get; set; } = "";
    [JsonPropertyName("availableModes")] public IReadOnlyList<SessionMode> AvailableModes { get; set; } = [];
}

public sealed record NewSessionResponse : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("modes")] public SessionModeState? Modes { get; set; }
    [JsonPropertyName("configOptions")] public IReadOnlyList<SessionConfigOption>? ConfigOptions { get; set; }
}

public sealed record LoadSessionRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("cwd")] public string Cwd { get; set; } = "";
    [JsonPropertyName("mcpServers")] public IReadOnlyList<McpServer> McpServers { get; set; } = [];
}

public sealed record LoadSessionResponse : AcpObject
{
    [JsonPropertyName("modes")] public SessionModeState? Modes { get; set; }
    [JsonPropertyName("configOptions")] public IReadOnlyList<SessionConfigOption>? ConfigOptions { get; set; }
}

public sealed record SetSessionModeRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("modeId")] public string ModeId { get; set; } = "";
}

public sealed record SetSessionModeResponse : AcpObject;

public sealed record PromptRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("prompt")] public IReadOnlyList<ContentBlock> Prompt { get; set; } = [];
}

public sealed record PromptResponse : AcpObject
{
    /// <summary>One of <see cref="StopReasons"/>; kept as a string so new values do not break parsing.</summary>
    [JsonPropertyName("stopReason")] public string StopReason { get; set; } = "";
}

/// <summary>The <c>session/cancel</c> notification.</summary>
public sealed record CancelNotification : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
}

// ───────────────────────── client-side requests ─────────────────────────

public sealed record PermissionOption : AcpObject
{
    [JsonPropertyName("optionId")] public string OptionId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>One of <see cref="PermissionOptionKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
}

public sealed record RequestPermissionRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("toolCall")] public ToolCallUpdate ToolCall { get; set; } = new();
    [JsonPropertyName("options")] public IReadOnlyList<PermissionOption> Options { get; set; } = [];
}

/// <summary>The client's answer to a permission request: the user either picked an option or the turn was cancelled.</summary>
[JsonConverter(typeof(RequestPermissionOutcomeConverter))]
public abstract record RequestPermissionOutcome : AcpObject;

public sealed record CancelledPermissionOutcome : RequestPermissionOutcome
{
    [JsonPropertyName("outcome")] public string Outcome => "cancelled";
}

public sealed record SelectedPermissionOutcome : RequestPermissionOutcome
{
    [JsonPropertyName("outcome")] public string Outcome => "selected";
    [JsonPropertyName("optionId")] public string OptionId { get; set; } = "";
}

public sealed record RequestPermissionResponse : AcpObject
{
    [JsonPropertyName("outcome")] public RequestPermissionOutcome Outcome { get; set; } = new CancelledPermissionOutcome();
}

public sealed record ReadTextFileRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("line")] public int? Line { get; set; }
    [JsonPropertyName("limit")] public int? Limit { get; set; }
}

public sealed record ReadTextFileResponse : AcpObject
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public sealed record WriteTextFileRequest : AcpObject
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public sealed record WriteTextFileResponse : AcpObject;
