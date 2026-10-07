using MandoCode.Services;
using Spectre.Console;
using Xunit;
namespace MandoCode.Tests;
public class CommandPreviewTests
{
    [Fact]
    public void PowerShellPreviewPreservesExactInvocationAndQuotedSemicolons()
    {
        const string command = "powershell -NoProfile -Command \"$f = Get-Item 'Google.Protobuf.dll'; Write-Output 'a;b'; Write-Output $f.Length\"";
        var preview = CommandPreview.Create(command, "C:\\project");
        Assert.Equal(command, preview.Exact);
        Assert.Equal("PowerShell", preview.Shell);
        Assert.Contains(";\nWrite-Output 'a;b';\n", preview.Script);
        Assert.DoesNotContain("a;\nb", preview.Script);
        Assert.True(preview.HasScriptView);
        Assert.Contains("[mediumpurple1]$f[/]", SyntaxHighlighter.Highlight(preview.Script, "powershell"));
        _ = new Markup(SyntaxHighlighter.Highlight(preview.Script, "powershell"));
    }
    [Theory]
    [InlineData("git status --short")]
    [InlineData("powershell -File script.ps1")]
    [InlineData("powershell -EncodedCommand ABCD")]
    public void UnrecognizedInvocationsRemainFullyVisible(string command)
    {
        var preview = CommandPreview.Create(command, ".");
        Assert.Equal(command, preview.Script);
        Assert.Equal(command, preview.Exact);
        Assert.False(preview.HasScriptView);
    }
    [Fact]
    public async Task PreviewStaysInsideCollapsibleToolActivity()
    {
        var session = new TuiSession();
        session.BeginToolTurn();
        using (session.CaptureToolOutput(true)) { }
        session.AppendCommand(CommandPreview.Create("powershell -Command \"Write-Output 'hello'; Write-Output 'world'\"", "project"));
        var entry = session.Snapshot().Entries.Single(e => e.Command is not null);
        Assert.NotNull(entry.ToolOutput);
        var expanded = await TuiLayoutTests.Frame(session, 80, 24);
        Assert.Contains("Working directory: project", expanded);
        Assert.Contains("Exact invocation", expanded);
        session.CompleteToolTurn();
        var collapsed = await TuiLayoutTests.Frame(session, 80, 24);
        Assert.DoesNotContain("Working directory: project", collapsed);
        entry.ToolOutput!.Expanded = true;
        Assert.Contains("Working directory: project", await TuiLayoutTests.Frame(session, 80, 24));
    }
}