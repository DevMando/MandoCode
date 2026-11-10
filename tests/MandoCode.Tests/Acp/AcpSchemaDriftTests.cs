using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MandoCode.Acp.Protocol;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>
/// Compares the hand-written DTOs with the vendored official schema
/// (<c>Acp/schema/schema.v1.json</c>, tag recorded in <see cref="AcpProtocol.SchemaTag"/>).
/// Catches typos in JSON names, required fields we forgot to model, and drift in the string "enums".
/// </summary>
public class AcpSchemaDriftTests
{
    private static readonly JsonElement Defs = LoadDefs();

    private static JsonElement LoadDefs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Acp", "schema", "schema.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("$defs").Clone();
    }

    // CLR type -> schema definition it models.
    private static readonly (Type Clr, string Def)[] Models =
    [
        (typeof(InitializeRequest), "InitializeRequest"),
        (typeof(InitializeResponse), "InitializeResponse"),
        (typeof(ClientCapabilities), "ClientCapabilities"),
        (typeof(FileSystemCapabilities), "FileSystemCapabilities"),
        (typeof(AgentCapabilities), "AgentCapabilities"),
        (typeof(PromptCapabilities), "PromptCapabilities"),
        (typeof(McpCapabilities), "McpCapabilities"),
        (typeof(Implementation), "Implementation"),
        (typeof(AuthenticateRequest), "AuthenticateRequest"),
        (typeof(AuthenticateResponse), "AuthenticateResponse"),
        (typeof(NewSessionRequest), "NewSessionRequest"),
        (typeof(NewSessionResponse), "NewSessionResponse"),
        (typeof(LoadSessionRequest), "LoadSessionRequest"),
        (typeof(LoadSessionResponse), "LoadSessionResponse"),
        (typeof(SetSessionModeRequest), "SetSessionModeRequest"),
        (typeof(SetSessionModeResponse), "SetSessionModeResponse"),
        (typeof(SessionMode), "SessionMode"),
        (typeof(SessionModeState), "SessionModeState"),
        (typeof(PromptRequest), "PromptRequest"),
        (typeof(PromptResponse), "PromptResponse"),
        (typeof(CancelNotification), "CancelNotification"),
        (typeof(SessionNotification), "SessionNotification"),
        (typeof(RequestPermissionRequest), "RequestPermissionRequest"),
        (typeof(RequestPermissionResponse), "RequestPermissionResponse"),
        (typeof(PermissionOption), "PermissionOption"),
        (typeof(SelectedPermissionOutcome), "SelectedPermissionOutcome"),
        (typeof(ReadTextFileRequest), "ReadTextFileRequest"),
        (typeof(ReadTextFileResponse), "ReadTextFileResponse"),
        (typeof(WriteTextFileRequest), "WriteTextFileRequest"),
        (typeof(WriteTextFileResponse), "WriteTextFileResponse"),
        (typeof(EnvVariable), "EnvVariable"),
        (typeof(HttpHeader), "HttpHeader"),
        (typeof(McpServerStdio), "McpServerStdio"),
        (typeof(McpServerHttp), "McpServerHttp"),
        (typeof(McpServerSse), "McpServerSse"),
        (typeof(Annotations), "Annotations"),
        (typeof(TextContent), "TextContent"),
        (typeof(ImageContent), "ImageContent"),
        (typeof(AudioContent), "AudioContent"),
        (typeof(ResourceLink), "ResourceLink"),
        (typeof(EmbeddedResource), "EmbeddedResource"),
        (typeof(TextResourceContents), "TextResourceContents"),
        (typeof(BlobResourceContents), "BlobResourceContents"),
        (typeof(UserMessageChunk), "ContentChunk"),
        (typeof(AgentMessageChunk), "ContentChunk"),
        (typeof(AgentThoughtChunk), "ContentChunk"),
        (typeof(ToolCall), "ToolCall"),
        (typeof(ToolCallUpdate), "ToolCallUpdate"),
        (typeof(ToolCallLocation), "ToolCallLocation"),
        (typeof(ContentToolCallContent), "Content"),
        (typeof(DiffToolCallContent), "Diff"),
        (typeof(TerminalToolCallContent), "Terminal"),
        (typeof(Plan), "Plan"),
        (typeof(PlanEntry), "PlanEntry"),
        (typeof(UnstructuredCommandInput), "UnstructuredCommandInput"),
        (typeof(AvailableCommand), "AvailableCommand"),
        (typeof(AvailableCommandsUpdate), "AvailableCommandsUpdate"),
        (typeof(CurrentModeUpdate), "CurrentModeUpdate"),
    ];

    // Union discriminators live on the wrapping oneOf, not in the variant's own definition.
    private static readonly HashSet<string> Discriminators = ["type", "sessionUpdate", "outcome"];

    public static IEnumerable<object[]> ModelData() =>
        Models.Select(m => new object[] { m.Clr.Name, m.Def });

    private static Type ClrFor(string clrName) => Models.First(m => m.Clr.Name == clrName).Clr;

    private static JsonTypeInfo InfoFor(Type type) =>
        AcpJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"{type.Name} is not registered in AcpJsonContext.");

    [Fact]
    public void VendoredSchema_IsPresentAndNonTrivial()
    {
        Assert.True(Defs.EnumerateObject().Count() > 100);
        Assert.True(Defs.TryGetProperty("SessionUpdate", out _));
    }

    [Theory]
    [MemberData(nameof(ModelData))]
    public void ModelledProperties_ExistInSchema(string clrName, string defName)
    {
        var schemaProps = SchemaProperties(defName);
        var clrProps = InfoFor(ClrFor(clrName)).Properties.Select(p => p.Name).ToList();

        Assert.Contains("_meta", clrProps);
        var unknown = clrProps.Where(n => n != "_meta" && !Discriminators.Contains(n) && !schemaProps.Contains(n)).ToList();
        Assert.True(unknown.Count == 0,
            $"{clrName} models properties missing from schema def '{defName}': {string.Join(", ", unknown)}");
    }

    [Theory]
    [MemberData(nameof(ModelData))]
    public void RequiredSchemaProperties_AreModelled(string clrName, string defName)
    {
        var required = SchemaRequired(defName);
        var clrProps = InfoFor(ClrFor(clrName)).Properties.Select(p => p.Name).ToHashSet();

        var missing = required.Where(r => !clrProps.Contains(r)).ToList();
        Assert.True(missing.Count == 0,
            $"{clrName} does not model required properties of '{defName}': {string.Join(", ", missing)}");
    }

    public static IEnumerable<object[]> EnumData() =>
    [
        [typeof(StopReasons), "StopReason"],
        [typeof(ToolKinds), "ToolKind"],
        [typeof(ToolCallStatuses), "ToolCallStatus"],
        [typeof(PermissionOptionKinds), "PermissionOptionKind"],
        [typeof(PlanEntryPriorities), "PlanEntryPriority"],
        [typeof(PlanEntryStatuses), "PlanEntryStatus"],
        [typeof(Roles), "Role"],
    ];

    [Theory]
    [MemberData(nameof(EnumData))]
    public void ConstantClasses_MatchSchemaEnums(Type constants, string defName)
    {
        var fromClr = constants.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var fromSchema = Defs.GetProperty(defName).GetProperty("oneOf").EnumerateArray()
            .Select(e => e.GetProperty("const").GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(fromSchema, fromClr);
    }

    [Fact]
    public void SessionUpdate_KnownVariantsAreSubsetOfSchemaVariants()
    {
        var schemaKinds = Defs.GetProperty("SessionUpdate").GetProperty("oneOf").EnumerateArray()
            .Select(v => v.GetProperty("properties").GetProperty("sessionUpdate").GetProperty("const").GetString()!)
            .ToHashSet();

        string[] handled =
        [
            "user_message_chunk", "agent_message_chunk", "agent_thought_chunk", "tool_call",
            "tool_call_update", "plan", "available_commands_update", "current_mode_update",
        ];

        Assert.All(handled, kind => Assert.Contains(kind, schemaKinds));
    }

    [Fact]
    public void Methods_AreDeclaredBySchema()
    {
        var methods = new HashSet<string>();
        foreach (var def in Defs.EnumerateObject())
            if (def.Value.TryGetProperty("x-method", out var m) && m.ValueKind == JsonValueKind.String)
                methods.Add(m.GetString()!);

        string[] used =
        [
            AcpMethods.Initialize, AcpMethods.Authenticate, AcpMethods.SessionNew, AcpMethods.SessionLoad,
            AcpMethods.SessionPrompt, AcpMethods.SessionCancel, AcpMethods.SessionSetMode, AcpMethods.SessionUpdate,
            AcpMethods.SessionRequestPermission, AcpMethods.FsReadTextFile, AcpMethods.FsWriteTextFile,
        ];

        Assert.All(used, method => Assert.Contains(method, methods));
    }

    private static HashSet<string> SchemaProperties(string defName)
    {
        var def = Defs.GetProperty(defName);
        return def.TryGetProperty("properties", out var props)
            ? props.EnumerateObject().Select(p => p.Name).ToHashSet()
            : [];
    }

    private static List<string> SchemaRequired(string defName)
    {
        var def = Defs.GetProperty(defName);
        return def.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];
    }
}
