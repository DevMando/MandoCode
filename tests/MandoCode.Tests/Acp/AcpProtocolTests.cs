using System.Text.Json;
using MandoCode.Acp.Protocol;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpProtocolTests
{
    private static T Read<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Deserialize(json, info)!;

    private static string Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        JsonSerializer.Serialize(value, info);

    // ───────────── requests we send ─────────────

    [Fact]
    public void InitializeRequest_SerializesExpectedShape()
    {
        var request = new InitializeRequest
        {
            ClientCapabilities = new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
                Terminal = false,
            },
            ClientInfo = new Implementation { Name = "MandoCode", Version = "1.2.3" },
        };

        using var doc = JsonDocument.Parse(Write(request, AcpJsonContext.Default.InitializeRequest));
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.True(root.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.True(root.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.False(root.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean());
        Assert.Equal("MandoCode", root.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("_meta", out _), "null members must be omitted");
    }

    [Fact]
    public void NewSessionRequest_SendsEmptyMcpServersArray()
    {
        var json = Write(new NewSessionRequest { Cwd = @"C:\work\proj" }, AcpJsonContext.Default.NewSessionRequest);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(@"C:\work\proj", doc.RootElement.GetProperty("cwd").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("mcpServers").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("mcpServers").GetArrayLength());
    }

    [Fact]
    public void PromptRequest_WritesContentBlocksWithDiscriminator()
    {
        var request = new PromptRequest
        {
            SessionId = "sess-1",
            Prompt =
            [
                new TextContent("hello"),
                new ResourceLink { Name = "a.cs", Uri = "file:///C:/p/a.cs", MimeType = "text/plain", Size = 12 },
                new ImageContent { Data = "AAAA", MimeType = "image/png" },
                new EmbeddedResource { Resource = new TextResourceContents { Uri = "file:///C:/p/b.txt", Text = "body" } },
            ],
        };

        using var doc = JsonDocument.Parse(Write(request, AcpJsonContext.Default.PromptRequest));
        var prompt = doc.RootElement.GetProperty("prompt");

        Assert.Equal("text", prompt[0].GetProperty("type").GetString());
        Assert.Equal("hello", prompt[0].GetProperty("text").GetString());
        Assert.Equal("resource_link", prompt[1].GetProperty("type").GetString());
        Assert.Equal(12, prompt[1].GetProperty("size").GetInt64());
        Assert.Equal("image", prompt[2].GetProperty("type").GetString());
        Assert.Equal("resource", prompt[3].GetProperty("type").GetString());
        Assert.Equal("body", prompt[3].GetProperty("resource").GetProperty("text").GetString());
    }

    [Fact]
    public void CancelNotification_SerializesSessionId()
    {
        var json = Write(new CancelNotification { SessionId = "s" }, AcpJsonContext.Default.CancelNotification);
        Assert.Equal("""{"sessionId":"s"}""", json);
    }

    [Fact]
    public void PermissionOutcomes_SerializeWithOutcomeDiscriminator()
    {
        var selected = Write(
            new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = "allow-1" } },
            AcpJsonContext.Default.RequestPermissionResponse);
        var cancelled = Write(
            new RequestPermissionResponse { Outcome = new CancelledPermissionOutcome() },
            AcpJsonContext.Default.RequestPermissionResponse);

        Assert.Equal("""{"outcome":{"outcome":"selected","optionId":"allow-1"}}""", selected);
        Assert.Equal("""{"outcome":{"outcome":"cancelled"}}""", cancelled);
    }

    [Fact]
    public void PermissionOutcome_RoundTrips()
    {
        var response = Read("""{"outcome":{"optionId":"x","outcome":"selected"}}""", AcpJsonContext.Default.RequestPermissionResponse);

        var selected = Assert.IsType<SelectedPermissionOutcome>(response.Outcome);
        Assert.Equal("x", selected.OptionId);
        Assert.IsType<CancelledPermissionOutcome>(
            Read("""{"outcome":{"outcome":"cancelled"}}""", AcpJsonContext.Default.RequestPermissionResponse).Outcome);
    }

    [Fact]
    public void McpServers_RoundTripEachKind()
    {
        var request = new NewSessionRequest
        {
            Cwd = "/p",
            McpServers =
            [
                new McpServerStdio { Name = "a", Command = "node", Args = ["x.js"], Env = [new EnvVariable { Name = "K", Value = "V" }] },
                new McpServerHttp { Name = "b", Url = "https://h", Headers = [new HttpHeader { Name = "H", Value = "1" }] },
                new McpServerSse { Name = "c", Url = "https://s" },
            ],
        };

        var json = Write(request, AcpJsonContext.Default.NewSessionRequest);
        var back = Read(json, AcpJsonContext.Default.NewSessionRequest);

        Assert.IsType<McpServerStdio>(back.McpServers[0]);
        Assert.IsType<McpServerHttp>(back.McpServers[1]);
        Assert.IsType<McpServerSse>(back.McpServers[2]);
        Assert.DoesNotContain("\"type\":\"stdio\"", json);
    }

    // ───────────── responses we receive ─────────────

    [Fact]
    public void InitializeResponse_ParsesCapabilitiesAndIgnoresUnknownFields()
    {
        const string json = """
        {
          "protocolVersion": 1,
          "futureTopLevelField": {"a": [1, 2, 3]},
          "agentInfo": {"name": "claude-agent", "version": "0.9.0", "title": "Claude"},
          "authMethods": [
            {"id": "claude-login", "name": "Claude login", "description": "run claude auth login"},
            {"type": "terminal", "id": "t", "name": "Terminal", "args": ["--login"]}
          ],
          "agentCapabilities": {
            "loadSession": true,
            "promptCapabilities": {"image": true, "audio": false, "embeddedContext": true, "somethingNew": true},
            "mcpCapabilities": {"http": true},
            "sessionCapabilities": {"list": {}}
          }
        }
        """;

        var response = Read(json, AcpJsonContext.Default.InitializeResponse);

        Assert.Equal(1, response.ProtocolVersion);
        Assert.Equal("claude-agent", response.AgentInfo!.Name);
        Assert.True(response.AgentCapabilities.LoadSession);
        Assert.True(response.AgentCapabilities.PromptCapabilities.Image);
        Assert.False(response.AgentCapabilities.PromptCapabilities.Audio);
        Assert.True(response.AgentCapabilities.PromptCapabilities.EmbeddedContext);
        Assert.True(response.AgentCapabilities.McpCapabilities.Http);
        Assert.False(response.AgentCapabilities.McpCapabilities.Sse);
        Assert.Equal(2, response.AuthMethods.Count);
        Assert.Equal("terminal", response.AuthMethods[1].Type);
    }

    [Fact]
    public void InitializeResponse_MissingOptionalSections_UseDefaults()
    {
        var response = Read("""{"protocolVersion":1}""", AcpJsonContext.Default.InitializeResponse);

        Assert.False(response.AgentCapabilities.PromptCapabilities.Image);
        Assert.Empty(response.AuthMethods);
        Assert.Null(response.AgentInfo);
    }

    [Fact]
    public void MissingFields_KeepInitializerDefaults()
    {
        // Regression: with init-only properties the net8 source generator overwrote initializers
        // with null for every field absent from the JSON.
        var capabilities = Read("{}", AcpJsonContext.Default.AgentCapabilities);
        Assert.NotNull(capabilities.PromptCapabilities);
        Assert.NotNull(capabilities.McpCapabilities);

        var request = Read("""{"cwd":"/p"}""", AcpJsonContext.Default.NewSessionRequest);
        Assert.NotNull(request.McpServers);
        Assert.Empty(request.McpServers);

        var notification = Read("""{"sessionId":"s","update":{"sessionUpdate":"plan"}}""", AcpJsonContext.Default.SessionNotification);
        Assert.Empty(Assert.IsType<Plan>(notification.Update).Entries);

        var chunk = Assert.IsType<AgentMessageChunk>(
            Read("""{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk"}}""", AcpJsonContext.Default.SessionNotification).Update);
        Assert.NotNull(chunk.Content);
    }

    [Fact]
    public void NewSessionResponse_ParsesModes()
    {
        var response = Read(
            """{"sessionId":"abc","modes":{"currentModeId":"default","availableModes":[{"id":"default","name":"Default"},{"id":"plan","name":"Plan","description":"x"}]},"configOptions":[{"anything":1}]}""",
            AcpJsonContext.Default.NewSessionResponse);

        Assert.Equal("abc", response.SessionId);
        Assert.Equal("default", response.Modes!.CurrentModeId);
        Assert.Equal(2, response.Modes.AvailableModes.Count);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("cancelled")]
    [InlineData("a_stop_reason_from_the_future")]
    public void PromptResponse_StopReasonIsAnOpenString(string reason)
    {
        var response = Read($$"""{"stopReason":"{{reason}}"}""", AcpJsonContext.Default.PromptResponse);
        Assert.Equal(reason, response.StopReason);
    }

    // ───────────── session/update ─────────────

    private static SessionNotification Update(string updateJson) =>
        Read("{\"sessionId\":\"s1\",\"update\":{" + updateJson + "}}", AcpJsonContext.Default.SessionNotification);

    [Fact]
    public void AgentMessageChunk_ParsesTextContent()
    {
        var n = Update("""
            "sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hel"}
            """);

        var chunk = Assert.IsType<AgentMessageChunk>(n.Update);
        Assert.Equal("Hel", Assert.IsType<TextContent>(chunk.Content).Text);
        Assert.Equal("s1", n.SessionId);
    }

    [Fact]
    public void Discriminator_MayAppearAfterOtherProperties()
    {
        // net8's [JsonPolymorphic] requires the discriminator first; real agents do not guarantee that.
        var n = Update("""
            "content":{"text":"late","type":"text"},"messageId":"m1","sessionUpdate":"agent_thought_chunk"
            """);

        var chunk = Assert.IsType<AgentThoughtChunk>(n.Update);
        Assert.Equal("late", Assert.IsType<TextContent>(chunk.Content).Text);
        Assert.Equal("m1", chunk.MessageId);
    }

    [Fact]
    public void UserMessageChunk_IsRecognised()
    {
        var n = Update("""
            "sessionUpdate":"user_message_chunk","content":{"type":"text","text":"hi"}
            """);
        Assert.IsType<UserMessageChunk>(n.Update);
    }

    [Fact]
    public void ToolCall_ParsesFieldsAndContentVariants()
    {
        var n = Update("""
            "sessionUpdate":"tool_call",
            "toolCallId":"call_1","title":"Edit a.cs","kind":"edit","status":"in_progress",
            "locations":[{"path":"C:\\p\\a.cs","line":10}],
            "rawInput":{"path":"a.cs"},
            "content":[
              {"type":"diff","path":"C:\\p\\a.cs","oldText":"a","newText":"b"},
              {"type":"content","content":{"type":"text","text":"done"}},
              {"type":"terminal","terminalId":"t1"},
              {"type":"hologram","payload":1}
            ]
            """);

        var call = Assert.IsType<ToolCall>(n.Update);
        Assert.Equal("call_1", call.ToolCallId);
        Assert.Equal("Edit a.cs", call.Title);
        Assert.Equal(ToolKinds.Edit, call.GetEffectiveKind());
        Assert.Equal(ToolCallStatuses.InProgress, call.GetEffectiveStatus());
        Assert.Equal(10, call.Locations![0].Line);
        Assert.Equal("a.cs", call.RawInput!.Value.GetProperty("path").GetString());

        Assert.Collection(call.Content!,
            c => { var d = Assert.IsType<DiffToolCallContent>(c); Assert.Equal("a", d.OldText); Assert.Equal("b", d.NewText); },
            c => Assert.Equal("done", Assert.IsType<TextContent>(Assert.IsType<ContentToolCallContent>(c).Content).Text),
            c => Assert.Equal("t1", Assert.IsType<TerminalToolCallContent>(c).TerminalId),
            c => Assert.Equal("hologram", Assert.IsType<UnknownToolCallContent>(c).Kind));
    }

    [Fact]
    public void ToolCall_Defaults_FollowTheSchema()
    {
        var call = Assert.IsType<ToolCall>(Update("""
            "sessionUpdate":"tool_call","toolCallId":"c","title":"t"
            """).Update);

        Assert.Equal(ToolKinds.Other, call.GetEffectiveKind());
        Assert.Equal(ToolCallStatuses.Pending, call.GetEffectiveStatus());
    }

    [Fact]
    public void ToolCallUpdate_PartialFieldsStayNull()
    {
        var update = Assert.IsType<ToolCallUpdate>(Update("""
            "sessionUpdate":"tool_call_update","toolCallId":"c","status":"completed"
            """).Update);

        Assert.Equal("c", update.ToolCallId);
        Assert.Equal(ToolCallStatuses.Completed, update.Status);
        Assert.Null(update.Title);
        Assert.Null(update.Content);
        Assert.Null(update.Locations);
    }

    [Fact]
    public void Plan_ParsesEntries()
    {
        var plan = Assert.IsType<Plan>(Update("""
            "sessionUpdate":"plan","entries":[
              {"content":"Read file","priority":"high","status":"completed"},
              {"content":"Edit file","priority":"medium","status":"in_progress"}]
            """).Update);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(PlanEntryStatuses.InProgress, plan.Entries[1].Status);
    }

    [Fact]
    public void AvailableCommandsAndModeUpdates_Parse()
    {
        var commands = Assert.IsType<AvailableCommandsUpdate>(Update("""
            "sessionUpdate":"available_commands_update","availableCommands":[{"name":"init","description":"Init","input":{"hint":"path"}}]
            """).Update);
        Assert.Equal("path", commands.AvailableCommands[0].Input!.Hint);

        var mode = Assert.IsType<CurrentModeUpdate>(Update("""
            "sessionUpdate":"current_mode_update","currentModeId":"plan"
            """).Update);
        Assert.Equal("plan", mode.CurrentModeId);
    }

    [Fact]
    public void ConfigOptionUpdate_CarriesTheFullOptionList_WithFlatAndGroupedChoices()
    {
        var n = Update("""
            "sessionUpdate":"config_option_update","configOptions":[
              {"id":"model","name":"Model","category":"model","type":"select","currentValue":"b",
               "options":[{"value":"a","name":"A"},{"value":"b","name":"B","description":"second"}]},
              {"id":"effort","name":"Effort","type":"select","currentValue":"low",
               "options":[{"group":"g","name":"Group","options":[{"value":"low","name":"Low"}]}]},
              {"id":"fast","name":"Fast","type":"boolean","currentValue":true}]
            """);

        var update = Assert.IsType<ConfigOptionUpdate>(n.Update);
        Assert.Equal(3, update.ConfigOptions.Count);
        Assert.Equal("b", update.ConfigOptions[0].CurrentSelection);
        Assert.Equal(["a", "b"], update.ConfigOptions[0].Choices().Select(c => c.Value));
        Assert.Equal("Group", Assert.Single(update.ConfigOptions[1].Choices()).Group);
        Assert.Null(update.ConfigOptions[2].CurrentSelection);   // a boolean is not a select
        Assert.Empty(update.ConfigOptions[2].Choices());
    }

    [Fact]
    public void SessionCapabilities_AreNeverNull_AndMeanPresenceOfAnObject()
    {
        var none = JsonSerializer.Deserialize("""{"sessionCapabilities":null}""", AcpJsonContext.Default.AgentCapabilities)!;
        Assert.False(none.SessionCapabilities.CanList());

        var some = JsonSerializer.Deserialize(
            """{"sessionCapabilities":{"list":{},"close":null,"resume":{"_meta":{}}}}""", AcpJsonContext.Default.AgentCapabilities)!;
        Assert.True(some.SessionCapabilities.CanList());
        Assert.True(some.SessionCapabilities.CanResume());
        Assert.False(some.SessionCapabilities.CanClose());
    }

    [Theory]
    [InlineData("usage_update")]
    [InlineData("session_info_update")]
    [InlineData("something_invented_next_year")]
    public void UnknownSessionUpdate_IsPreservedNotThrown(string kind)
    {
        var n = Update($$"""
            "sessionUpdate":"{{kind}}","used":123,"nested":{"a":[1]}
            """);

        var unknown = Assert.IsType<UnknownSessionUpdate>(n.Update);
        Assert.Equal(kind, unknown.Kind);
        Assert.Equal(123, unknown.Raw.GetProperty("used").GetInt32());
    }

    [Fact]
    public void SessionUpdate_WithoutDiscriminator_IsUnknown()
    {
        var n = Update(""" "content":{"type":"text","text":"x"} """);
        Assert.Null(Assert.IsType<UnknownSessionUpdate>(n.Update).Kind);
    }

    [Fact]
    public void KnownUpdate_WithMalformedBody_DegradesToUnknown()
    {
        // toolCallId has the wrong type: the message must not blow up the stream.
        var n = Update("""
            "sessionUpdate":"tool_call","toolCallId":{"oops":true},"title":"t"
            """);

        var unknown = Assert.IsType<UnknownSessionUpdate>(n.Update);
        Assert.Equal("tool_call", unknown.Kind);
    }

    [Fact]
    public void UnknownContentBlock_IsPreserved()
    {
        var n = Update("""
            "sessionUpdate":"agent_message_chunk","content":{"type":"hologram","depth":3}
            """);

        var chunk = Assert.IsType<AgentMessageChunk>(n.Update);
        var unknown = Assert.IsType<UnknownContentBlock>(chunk.Content);
        Assert.Equal("hologram", unknown.Kind);
        Assert.Equal(3, unknown.Raw.GetProperty("depth").GetInt32());
    }

    [Fact]
    public void EmbeddedResource_DistinguishesTextFromBlob()
    {
        var text = Read("""{"type":"resource","resource":{"uri":"file:///a","text":"t"}}""", AcpJsonContext.Default.ContentBlock);
        var blob = Read("""{"type":"resource","resource":{"uri":"file:///b","blob":"AAAA","mimeType":"image/png"}}""", AcpJsonContext.Default.ContentBlock);

        Assert.IsType<TextResourceContents>(Assert.IsType<EmbeddedResource>(text).Resource);
        Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResource>(blob).Resource);
    }

    [Fact]
    public void RequestPermissionRequest_ParsesToolCallAndOptions()
    {
        const string json = """
        {
          "sessionId": "s",
          "toolCall": {"toolCallId": "c1", "title": "Run tests", "kind": "execute", "status": "pending"},
          "options": [
            {"optionId": "a", "name": "Allow once", "kind": "allow_once"},
            {"optionId": "b", "name": "Reject", "kind": "reject_once"},
            {"optionId": "c", "name": "Whatever", "kind": "new_kind"}
          ]
        }
        """;

        var request = Read(json, AcpJsonContext.Default.RequestPermissionRequest);

        Assert.Equal("c1", request.ToolCall.ToolCallId);
        Assert.Equal(ToolKinds.Execute, request.ToolCall.Kind);
        Assert.Equal(3, request.Options.Count);
        Assert.Equal(PermissionOptionKinds.AllowOnce, request.Options[0].Kind);
        Assert.Equal("new_kind", request.Options[2].Kind);
    }

    [Fact]
    public void FileSystemRequests_Parse()
    {
        var read = Read("""{"sessionId":"s","path":"C:\\p\\a.txt","line":3,"limit":10}""", AcpJsonContext.Default.ReadTextFileRequest);
        Assert.Equal(@"C:\p\a.txt", read.Path);
        Assert.Equal(3, read.Line);
        Assert.Equal(10, read.Limit);

        var write = Read("""{"sessionId":"s","path":"/p/a.txt","content":"x"}""", AcpJsonContext.Default.WriteTextFileRequest);
        Assert.Equal("x", write.Content);

        Assert.Null(Read("""{"sessionId":"s","path":"/p"}""", AcpJsonContext.Default.ReadTextFileRequest).Line);
    }

    [Fact]
    public void Meta_IsPreservedAsRawJson()
    {
        var response = Read("""{"protocolVersion":1,"_meta":{"vendor/x":{"k":1}}}""", AcpJsonContext.Default.InitializeResponse);
        Assert.Equal(1, response.Meta!.Value.GetProperty("vendor/x").GetProperty("k").GetInt32());
    }

    [Fact]
    public void ErrorCodes_MatchTheSchema()
    {
        Assert.Equal(-32601, AcpErrorCodes.MethodNotFound);
        Assert.Equal(-32602, AcpErrorCodes.InvalidParams);
        Assert.Equal(-32603, AcpErrorCodes.InternalError);
        Assert.Equal(-32000, AcpErrorCodes.AuthRequired);
        Assert.Equal(-32800, AcpErrorCodes.RequestCancelled);
    }
}
