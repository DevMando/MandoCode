using System.Reflection;
using System.Text;
using MandoCode.Components;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
[Trait("Category", "Component")]
public class ClipboardCommandTests
{
    [Fact]
    public async Task TerminalControlSinksAreIsolatedAndRestoreTheirParentScope()
    {
        using var parent = new StringWriter();
        using var first = new StringWriter();
        using var second = new StringWriter();
        using (TuiConsole.Enter(new TuiSession(), parent))
        {
            await Task.WhenAll(Write(first, "first"), Write(second, "second"));
            TuiConsole.WriteTerminalControl("parent");
        }
        Assert.Equal("first", first.ToString());
        Assert.Equal("second", second.ToString());
        Assert.Equal("parent", parent.ToString());
        static async Task Write(TextWriter writer, string text)
        {
            using var scope = TuiConsole.Enter(new TuiSession(), writer);
            await Task.Yield();
            TuiConsole.WriteTerminalControl(text);
        }
    }

    [Theory]
    [InlineData("HandleCopyCommand", "Reply with Unicode: café 🚀", "Reply with Unicode: café 🚀")]
    [InlineData("HandleCopyCodeCommand", "First\n```cs\nvar name = \"café\";\n```\nThen\n```text\nsecond block 🚀\n```", "var name = \"café\";\n\nsecond block 🚀")]
    public void CopyCommands_SendClipboardSequenceToTerminal_AndOnlyConfirmationToTranscript(string method, string response, string copied)
    {
        using var terminal = new StringWriter();
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Enter(session, terminal);
            var app = new App();
            typeof(App).GetField("_lastAiResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, response);
            typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            terminal.Flush();
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(copied));
            Assert.Equal($"\u001b]52;c;{payload}\u0007", terminal.ToString());
            Assert.Single(session.Snapshot().Entries);
        }
    }

    [Theory]
    [InlineData("HandleCopyCommand", null)]
    [InlineData("HandleCopyCodeCommand", null)]
    [InlineData("HandleCopyCodeCommand", "Reply without code fences")]
    public void CopyCommands_WithNothingToCopy_DoNotSendClipboardSequence(string method, string? response)
    {
        using var terminal = new StringWriter();
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Enter(session, terminal);
            var app = new App();
            typeof(App).GetField("_lastAiResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, response);
            typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            terminal.Flush();
            Assert.Equal("", terminal.ToString());
            Assert.Single(session.Snapshot().Entries);
        }
    }
}
