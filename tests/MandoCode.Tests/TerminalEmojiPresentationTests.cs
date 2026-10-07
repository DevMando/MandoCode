using MandoCode.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace MandoCode.Tests;

public sealed class TerminalEmojiPresentationTests
{
    [Theory]
    [InlineData("⚙Pinging", "⚙ Pinging")]
    [InlineData("🕹️Games", "🕹️ Games")]
    [InlineData("🤖 Agent", "🤖 Agent")]
    [InlineData("😎!", "😎!")]
    [InlineData("∑x", "∑x")]
    public void EmojiLabelsHaveOneSpaceWithoutChangingPunctuation(string input, string expected)
        => Assert.Equal(expected.ToCharArray(), TerminalEmojiPresentation.SpaceLabels(input).ToCharArray());
    [Theory]
    [InlineData("🛠")]
    [InlineData("🖥")]
    [InlineData("🖨")]
    [InlineData("🗂")]
    [InlineData("🗑")]
    [InlineData("🛡")]
    public void AmbiguousSupplementaryEmojiRequestsTwoCellPresentation(string glyph)
    {
        var normalized = TerminalEmojiPresentation.Normalize(glyph);
        Assert.Equal((glyph + "\uFE0F").ToCharArray(), normalized.ToCharArray());
        Assert.Equal(2, Segment.CellCount([new Segment(normalized)]));
        Assert.Equal(normalized.ToCharArray(), TerminalEmojiPresentation.Normalize(normalized).ToCharArray());
    }

    [Theory]
    [InlineData("🛠️")]
    [InlineData("🛠︎")]
    [InlineData("🕵🏽")]
    [InlineData("🏳️‍🌈")]
    [InlineData("🤖 😎 🧠 📷")]
    [InlineData("C:\\files\\a.cs plain text")]
    [InlineData("⚙ ☀")]
    public void ExplicitPresentationAndCompositeEmojiArePreserved(string text)
        => Assert.Equal(text.ToCharArray(), TerminalEmojiPresentation.Normalize(text).ToCharArray());

    [Fact]
    public void MarkdownNormalizesProseButPreservesInlineAndFencedCode()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
        console.Profile.Width = 80;
        console.Write(MarkdownHtmlRenderer.BuildRenderable("## 🛠 Coding Help\n\n- 🛠 Run commands\n\n`🛠 literal`\n\n```text\n🛠 code\n```"));
        var frame = writer.ToString();
        Assert.True(frame.Contains("🛠️ Coding Help", StringComparison.Ordinal));
        Assert.True(frame.Contains("🛠️ Run commands", StringComparison.Ordinal));
        Assert.True(frame.Contains("🛠 literal", StringComparison.Ordinal));
        Assert.True(frame.Contains("🛠 code", StringComparison.Ordinal));
        Assert.False(frame.Contains("🛠️ literal", StringComparison.Ordinal));
        Assert.False(frame.Contains("🛠️ code", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptPresentationDoesNotChangeSubmittedText()
    {
        var session = new TuiSession();
        session.AppendUserPrompt("🛠 help");
        var entry = Assert.Single(session.Snapshot().Entries);
        Assert.Equal("🛠 help", entry.UserPrompt);
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
        console.Write(entry.Content);
        Assert.True(writer.ToString().Contains("🛠️ help", StringComparison.Ordinal));
    }
}
