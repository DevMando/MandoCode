using System.Reflection;
using System.Text;
using MandoCode.Components;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Collection("TUI console routing")]
public class ClipboardCommandTests
{
    [Theory]
    [InlineData("HandleCopyCommand", "Reply with Unicode: café 🚀", "Reply with Unicode: café 🚀")]
    [InlineData("HandleCopyCodeCommand", "First\n```cs\nvar name = \"café\";\n```\nThen\n```text\nsecond block 🚀\n```", "var name = \"café\";\n\nsecond block 🚀")]
    public void CopyCommands_SendClipboardSequenceToTerminal_AndOnlyConfirmationToTranscript(string method, string response, string copied)
    {
        var previous = Console.Out;
        using var terminal = new StringWriter();
        Console.SetOut(terminal);
        try
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Begin(session);
            var app = new App();
            typeof(App).GetField("_lastAiResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, response);
            typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            Console.Out.Flush();
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(copied));
            Assert.Equal($"\u001b]52;c;{payload}\u0007", terminal.ToString());
            Assert.Single(session.Snapshot().Entries);
        }
        finally { Console.SetOut(previous); }
    }

    [Theory]
    [InlineData("HandleCopyCommand", null)]
    [InlineData("HandleCopyCodeCommand", null)]
    [InlineData("HandleCopyCodeCommand", "Reply without code fences")]
    public void CopyCommands_WithNothingToCopy_DoNotSendClipboardSequence(string method, string? response)
    {
        var previous = Console.Out;
        using var terminal = new StringWriter();
        Console.SetOut(terminal);
        try
        {
            var session = new TuiSession();
            using var scope = TuiConsole.Begin(session);
            var app = new App();
            typeof(App).GetField("_lastAiResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, response);
            typeof(App).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null);
            Console.Out.Flush();
            Assert.Equal("", terminal.ToString());
            Assert.Single(session.Snapshot().Entries);
        }
        finally { Console.SetOut(previous); }
    }
}
