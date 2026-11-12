using System.Reflection;
using System.Text;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MandoCode.Tests.Acp;

/// <summary>Commands that act on an ACP pane's own conversation, driven through the real <see cref="App"/> component.</summary>
[Collection("TUI console routing")]
[Trait("Category", "Component")]
public class AcpPaneCommandTests
{
    private static T Field<T>(App app, string name) =>
        (T)typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;

    private static void SetField(App app, string name, object? value) =>
        typeof(App).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);

    private static string Run(App app, string method)
    {
        using var terminal = new StringWriter();
        using var scope = TuiConsole.Enter(new TuiSession(), terminal);
        typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
        terminal.Flush();
        return terminal.ToString();
    }

    private static string Clipboard(string text) => $"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\u0007";

    [Fact]
    public async Task Copy_InAnAcpPane_TakesTheAcpReply_NotTheNativeOne()
    {
        await using var services = AcpTestHost.Services();
        var pane = services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "claude");
        var app = new App { Pane = pane };
        SetField(app, "_lastAiResponse", "native reply");
        Field<List<ChatMsg>>(app, "_acpMessages").AddRange(
        [
            new ChatMsg { Role = "assistant", Text = "older acp reply" },
            new ChatMsg { Role = "user", Text = "question" },
            new ChatMsg { Role = "assistant", Text = "acp reply" },
        ]);

        Assert.Equal(Clipboard("acp reply"), Run(app, "HandleCopyCommand"));
    }

    [Fact]
    public async Task CopyCode_InAnAcpPane_TakesTheCodeBlocksOfTheAcpReply()
    {
        await using var services = AcpTestHost.Services();
        var pane = services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "claude");
        var app = new App { Pane = pane };
        SetField(app, "_lastAiResponse", "```cs\nnative();\n```");
        Field<List<ChatMsg>>(app, "_acpMessages").Add(
            new ChatMsg { Role = "assistant", Text = "Here:\n```cs\nacp();\n```\nand\n```sh\nls\n```" });

        Assert.Equal(Clipboard("acp();\n\nls"), Run(app, "HandleCopyCodeCommand"));
    }

    [Fact]
    public async Task Copy_InAnAcpPaneWithNoAcpReply_DoesNotFallBackToTheNativeConversation()
    {
        await using var services = AcpTestHost.Services();
        var pane = services.GetRequiredService<AgentWorkspace>().Add(acpAgent: "claude");
        var app = new App { Pane = pane };
        SetField(app, "_lastAiResponse", "native reply");

        Assert.Equal("", Run(app, "HandleCopyCommand"));
    }

    [Fact]
    public async Task Copy_InANativePane_StillTakesTheNativeReply()
    {
        await using var services = AcpTestHost.Services();
        var pane = services.GetRequiredService<AgentWorkspace>().Add();
        var app = new App { Pane = pane };
        SetField(app, "_lastAiResponse", "native reply");
        Field<List<ChatMsg>>(app, "_acpMessages").Add(new ChatMsg { Role = "assistant", Text = "acp reply" });

        Assert.Equal(Clipboard("native reply"), Run(app, "HandleCopyCommand"));
    }
}
