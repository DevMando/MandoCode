using System.Text.Json;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace MandoCode.Tests;

/// <summary>
/// Retires the specific risk flagged in feat/agent-framework-migration's plan for Phase 4:
/// does MEAI's ChatMessage/AIContent polymorphic JSON serialization actually round-trip the
/// content types MandoCode's history needs (text, function calls, function results), the way
/// SK's own polymorphic serialization already does for AIService.ExportHistoryJson/
/// TryRestoreHistoryJson today? Verified here rather than assumed.
///
/// Also confirms a known open MAF bug (microsoft/agent-framework#1318, serializing history
/// containing FunctionApprovalRequestContent) doesn't apply to MandoCode: Phase 3 deliberately
/// didn't adopt ApprovalRequiredAIFunction/ToolApprovalRequestContent (see
/// AgentFunctionMiddleware's doc comment), so that content type never appears in MandoCode's
/// history in the first place.
/// </summary>
public class ChatHistoryConversionTests
{
    [Fact]
    public void ToMeaiMessages_preserves_role_and_plain_text_content()
    {
        var history = new ChatHistory("You are a helpful assistant.");
        history.AddUserMessage("List the files in the project.");
        history.AddAssistantMessage("Sure, here they are.");

        var messages = ChatHistoryConversion.ToMeaiMessages(history);

        Assert.Equal(3, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Equal("List the files in the project.", messages[1].Text);
        Assert.Equal(ChatRole.Assistant, messages[2].Role);
        Assert.Equal("Sure, here they are.", messages[2].Text);
    }

    [Fact]
    public void ToMeaiMessages_preserves_function_call_and_result_from_Items()
    {
        var history = new ChatHistory("system");
        var assistantMsg = new ChatMessageContent(AuthorRole.Assistant, items:
        [
            new Microsoft.SemanticKernel.FunctionCallContent(
                functionName: "list_all_project_files",
                pluginName: "FileSystem",
                id: "call-1",
                arguments: new KernelArguments())
        ]);
        history.Add(assistantMsg);

        var toolMsg = new ChatMessageContent(AuthorRole.Tool, items:
        [
            new Microsoft.SemanticKernel.FunctionResultContent(
                functionName: "list_all_project_files",
                pluginName: "FileSystem",
                callId: "call-1",
                result: "Program.cs\nAIService.cs")
        ]);
        history.Add(toolMsg);

        var messages = ChatHistoryConversion.ToMeaiMessages(history);

        var callContent = Assert.IsType<Microsoft.Extensions.AI.FunctionCallContent>(messages[1].Contents.Single());
        Assert.Equal("list_all_project_files", callContent.Name);

        var resultContent = Assert.IsType<Microsoft.Extensions.AI.FunctionResultContent>(messages[2].Contents.Single());
        Assert.Equal("call-1", resultContent.CallId);
        Assert.Equal("Program.cs\nAIService.cs", resultContent.Result);
    }

    [Fact]
    public void Converted_messages_round_trip_through_System_Text_Json()
    {
        var history = new ChatHistory("You are MandoCode.");
        history.AddUserMessage("Read config.json and summarize it.");

        var assistantMsg = new ChatMessageContent(AuthorRole.Assistant, items:
        [
            new Microsoft.SemanticKernel.FunctionCallContent(
                functionName: "read_file_contents",
                pluginName: "FileSystem",
                id: "call-42",
                arguments: new KernelArguments { ["relativePath"] = "config.json" })
        ]);
        history.Add(assistantMsg);

        var toolMsg = new ChatMessageContent(AuthorRole.Tool, items:
        [
            new Microsoft.SemanticKernel.FunctionResultContent(
                functionName: "read_file_contents",
                pluginName: "FileSystem",
                callId: "call-42",
                result: "{ \"key\": \"value\" }")
        ]);
        history.Add(toolMsg);
        history.AddAssistantMessage("The config has one key.");

        var messages = ChatHistoryConversion.ToMeaiMessages(history);

        // This is the actual risk: does plain System.Text.Json.JsonSerializer — no custom
        // converters, the same call shape AIService.ExportHistoryJson/TryRestoreHistoryJson
        // already use for SK's ChatMessageContent — round-trip MEAI's polymorphic AIContent
        // hierarchy out of the box?
        var json = JsonSerializer.Serialize(messages);
        var restored = JsonSerializer.Deserialize<List<ChatMessage>>(json);

        Assert.NotNull(restored);
        Assert.Equal(messages.Count, restored!.Count);

        Assert.Equal(ChatRole.System, restored[0].Role);
        Assert.Equal(ChatRole.User, restored[1].Role);
        Assert.Equal("Read config.json and summarize it.", restored[1].Text);

        var restoredCall = Assert.IsType<Microsoft.Extensions.AI.FunctionCallContent>(restored[2].Contents.Single());
        Assert.Equal("read_file_contents", restoredCall.Name);
        Assert.Equal("config.json", restoredCall.Arguments?["relativePath"]?.ToString());

        var restoredResult = Assert.IsType<Microsoft.Extensions.AI.FunctionResultContent>(restored[3].Contents.Single());
        Assert.Equal("call-42", restoredResult.CallId);

        Assert.Equal(ChatRole.Assistant, restored[4].Role);
        Assert.Equal("The config has one key.", restored[4].Text);
    }

    [Fact]
    public void ToSkMessages_reverses_ToMeaiMessages_preserving_role_and_text()
    {
        var history = new ChatHistory("system prompt");
        history.AddUserMessage("hello");
        history.AddAssistantMessage("hi there");

        var roundTripped = ChatHistoryConversion.ToSkMessages(ChatHistoryConversion.ToMeaiMessages(history));

        Assert.Equal(3, roundTripped.Count);
        Assert.Equal(AuthorRole.System, roundTripped[0].Role);
        Assert.Equal(AuthorRole.User, roundTripped[1].Role);
        Assert.Equal("hello", roundTripped[1].Content);
        Assert.Equal(AuthorRole.Assistant, roundTripped[2].Role);
        Assert.Equal("hi there", roundTripped[2].Content);
    }

    [Fact]
    public void ToSkMessages_converts_the_exact_shape_a_tool_calling_AgentResponse_returns()
    {
        // Verified empirically (throwaway spike against a real model): a tool-calling
        // AIAgent.RunAsync turn returns exactly this 3-message shape — assistant/FunctionCall,
        // tool/FunctionResult, assistant/TextContent. This is what AIService's cutover appends
        // to _chatHistory, so the conversion of THIS shape specifically is what matters.
        List<ChatMessage> agentResponseMessages =
        [
            new ChatMessage(ChatRole.Assistant, [new Microsoft.Extensions.AI.FunctionCallContent(
                callId: "call-1", name: "get_time", arguments: new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new Microsoft.Extensions.AI.FunctionResultContent(
                callId: "call-1", result: "3:45 PM")]),
            new ChatMessage(ChatRole.Assistant, "The current time is 3:45 PM."),
        ];

        var skMessages = ChatHistoryConversion.ToSkMessages(agentResponseMessages);

        Assert.Equal(3, skMessages.Count);

        Assert.Equal(AuthorRole.Assistant, skMessages[0].Role);
        var call = Assert.IsType<Microsoft.SemanticKernel.FunctionCallContent>(skMessages[0].Items.Single());
        Assert.Equal("get_time", call.FunctionName);
        Assert.Equal("call-1", call.Id);

        Assert.Equal(AuthorRole.Tool, skMessages[1].Role);
        var funcResult = Assert.IsType<Microsoft.SemanticKernel.FunctionResultContent>(skMessages[1].Items.Single());
        Assert.Equal("call-1", funcResult.CallId);
        Assert.Equal("3:45 PM", funcResult.Result);

        Assert.Equal(AuthorRole.Assistant, skMessages[2].Role);
        Assert.Equal("The current time is 3:45 PM.", skMessages[2].Content);
    }
}
