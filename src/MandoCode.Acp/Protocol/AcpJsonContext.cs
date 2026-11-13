using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Acp.Protocol;

/// <summary>
/// Source-generated serialization metadata for every ACP DTO. All protocol (de)serialization goes
/// through this context, so nothing in the protocol layer depends on runtime reflection.
/// Unknown JSON members are ignored (the System.Text.Json default); null members are omitted on write.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// initialize / authenticate
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(AuthenticateRequest))]
[JsonSerializable(typeof(AuthenticateResponse))]
// sessions
[JsonSerializable(typeof(NewSessionRequest))]
[JsonSerializable(typeof(NewSessionResponse))]
[JsonSerializable(typeof(LoadSessionRequest))]
[JsonSerializable(typeof(LoadSessionResponse))]
[JsonSerializable(typeof(SetSessionConfigOptionRequest))]
[JsonSerializable(typeof(SetSessionConfigOptionResponse))]
[JsonSerializable(typeof(ConfigOptionUpdate))]
[JsonSerializable(typeof(ResumeSessionRequest))]
[JsonSerializable(typeof(ResumeSessionResponse))]
[JsonSerializable(typeof(CloseSessionRequest))]
[JsonSerializable(typeof(CloseSessionResponse))]
[JsonSerializable(typeof(ListSessionsRequest))]
[JsonSerializable(typeof(ListSessionsResponse))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(SessionConfigOption))]
[JsonSerializable(typeof(SessionCapabilities))]
[JsonSerializable(typeof(SessionConfigSelectOption))]
[JsonSerializable(typeof(SessionConfigSelectGroup))]
[JsonSerializable(typeof(SetSessionModeRequest))]
[JsonSerializable(typeof(SetSessionModeResponse))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResponse))]
[JsonSerializable(typeof(CancelNotification))]
// agent -> client
[JsonSerializable(typeof(SessionNotification))]
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(RequestPermissionResponse))]
[JsonSerializable(typeof(ReadTextFileRequest))]
[JsonSerializable(typeof(ReadTextFileResponse))]
[JsonSerializable(typeof(WriteTextFileRequest))]
[JsonSerializable(typeof(WriteTextFileResponse))]
// unions (the converters deserialize concrete variants through these)
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ImageContent))]
[JsonSerializable(typeof(AudioContent))]
[JsonSerializable(typeof(ResourceLink))]
[JsonSerializable(typeof(EmbeddedResource))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSerializable(typeof(SessionUpdate))]
[JsonSerializable(typeof(UserMessageChunk))]
[JsonSerializable(typeof(AgentMessageChunk))]
[JsonSerializable(typeof(AgentThoughtChunk))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(ToolCallUpdate))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(AvailableCommandsUpdate))]
[JsonSerializable(typeof(CurrentModeUpdate))]
[JsonSerializable(typeof(ToolCallContent))]
[JsonSerializable(typeof(ContentToolCallContent))]
[JsonSerializable(typeof(DiffToolCallContent))]
[JsonSerializable(typeof(TerminalToolCallContent))]
[JsonSerializable(typeof(RequestPermissionOutcome))]
[JsonSerializable(typeof(CancelledPermissionOutcome))]
[JsonSerializable(typeof(SelectedPermissionOutcome))]
[JsonSerializable(typeof(McpServer))]
[JsonSerializable(typeof(McpServerStdio))]
[JsonSerializable(typeof(McpServerHttp))]
[JsonSerializable(typeof(McpServerSse))]
public sealed partial class AcpJsonContext : JsonSerializerContext
{
}
