using System.Net;
using HtmlAgilityPack;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;
[Trait("Category", "Integration")]
public sealed class TranscriptExportTests
{
    [Fact]
    public void ExportIncludesCodeAndCollapsedToolDetailsWithEscapedUserContent()
    {
        var session = new TuiSession(); session.Append(new Text("STARTUP SHOULD NOT EXPORT"));
        session.AppendUserPrompt("Explain <script>alert(1)</script>");
        session.Append(new Text("MandoCode: Response with code\nConsole.WriteLine(\"hello\");"));
        using (session.CaptureToolOutput(true)) session.Append(new Markup("[green]FULL TOOL OUTPUT[/]"));
        session.CompleteToolTurn(); session.SetActivity("SPINNER SHOULD NOT EXPORT"); session.SetPreview("PREVIEW SHOULD NOT EXPORT");
        session.AppendUserPrompt("/transcript-save");
        var html = TranscriptExport.BuildHtml(session, "Agent <one>", "project", "vision-model", DateTimeOffset.UtcNow);
        Assert.StartsWith("<!DOCTYPE html>", html);
        var document = new HtmlDocument(); document.LoadHtml(html);
        var text = WebUtility.HtmlDecode(document.DocumentNode.InnerText);
        Assert.Contains("FULL TOOL OUTPUT", text); Assert.Contains("1 tool call", text);
        Assert.Contains("Console.WriteLine(\"hello\");", text); Assert.Contains("<script>alert(1)</script>", text);
        Assert.Null(document.DocumentNode.SelectSingleNode("//script"));
        Assert.DoesNotContain("STARTUP SHOULD NOT EXPORT", html); Assert.DoesNotContain("SPINNER SHOULD NOT EXPORT", html); Assert.DoesNotContain("PREVIEW SHOULD NOT EXPORT", html);
        Assert.DoesNotContain("/transcript-save", html); Assert.Contains("color:#008000", html);
    }
    [Fact]
    public async Task SaveResolvesQuotedPathsAndNeverOverwritesExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var session = new TuiSession(); session.AppendUserPrompt("Howdy"); session.Append(new Text("Hello"));
            var path = await TranscriptExport.SaveAsync(session, "Agent", root, "model", "\"My Transcript.html\"");
            Assert.Equal(Path.Combine(root, "My Transcript.html"), path);
            var original = await File.ReadAllTextAsync(path); Assert.Contains("Howdy", original);
            await Assert.ThrowsAsync<IOException>(() => TranscriptExport.SaveAsync(session, "Agent", root, "model", "\"My Transcript.html\""));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
            var defaultPath = await TranscriptExport.SaveAsync(session, "Agent", root, "model");
            Assert.StartsWith("mandocode-transcript-", Path.GetFileName(defaultPath)); Assert.EndsWith(".html", defaultPath);
            await Assert.ThrowsAsync<IOException>(() => TranscriptExport.SaveAsync(session, "Agent", root, "model", "bad.txt"));
        } finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void EmptyConversationAndSaveCommandAloneDoNotCreateExports()
    {
        var session = new TuiSession(); session.AppendUserPrompt("/transcript-save \"Case Preserved.html\"");
        Assert.Throws<IOException>(() => TranscriptExport.BuildHtml(session, "Agent", "root", "model", DateTimeOffset.UtcNow));
        Assert.Contains("/transcript-save", SlashCommands.All.Keys);
    }
}