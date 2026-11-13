namespace MandoCode.Acp.Protocol;

/// <summary>Protocol-wide constants.</summary>
public static class AcpProtocol
{
    /// <summary>The only protocol version this client speaks.</summary>
    public const int Version = 1;

    /// <summary>Tag of the official schema the DTOs were written against.</summary>
    public const string SchemaTag = "v1.16.0";
}

/// <summary>JSON-RPC method names used by ACP v1 (stable methods only).</summary>
public static class AcpMethods
{
    // Client -> agent
    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";
    public const string SessionNew = "session/new";
    public const string SessionLoad = "session/load";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";
    public const string SessionSetMode = "session/set_mode";
    public const string SessionSetConfigOption = "session/set_config_option";
    public const string SessionList = "session/list";
    public const string SessionResume = "session/resume";
    public const string SessionClose = "session/close";

    // Agent -> client
    public const string SessionUpdate = "session/update";
    public const string SessionRequestPermission = "session/request_permission";
    public const string FsReadTextFile = "fs/read_text_file";
    public const string FsWriteTextFile = "fs/write_text_file";
}

/// <summary>JSON-RPC and ACP error codes.</summary>
public static class AcpErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
    public const int AuthRequired = -32000;
    public const int ResourceNotFound = -32002;
}

// The schema defines the following as string enums. They are modelled as plain strings with
// constant classes so that values added by newer agents never break deserialization.

public static class StopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string MaxTurnRequests = "max_turn_requests";
    public const string Refusal = "refusal";
    public const string Cancelled = "cancelled";
}

public static class ToolKinds
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Move = "move";
    public const string Search = "search";
    public const string Execute = "execute";
    public const string Think = "think";
    public const string Fetch = "fetch";
    public const string SwitchMode = "switch_mode";
    public const string Other = "other";
}

public static class ToolCallStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class PermissionOptionKinds
{
    public const string AllowOnce = "allow_once";
    public const string AllowAlways = "allow_always";
    public const string RejectOnce = "reject_once";
    public const string RejectAlways = "reject_always";
}

public static class PlanEntryPriorities
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}

public static class PlanEntryStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

public static class Roles
{
    public const string Assistant = "assistant";
    public const string User = "user";
}
