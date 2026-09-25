using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ReplyPreviewTests
{
    [Fact]
    public void Tail_KeepsTheLastLines()
    {
        var lines = ReplyPreview.Tail("one\ntwo\nthree\nfour", width: 40, maxLines: 2);
        Assert.Equal(new[] { "three", "four" }, lines);
    }

    [Fact]
    public void Tail_WrapsLongLines_SoNoLineIsWiderThanTheTerminal()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 30));
        var lines = ReplyPreview.Tail(text, width: 20, maxLines: 50);

        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(ReplyPreview.Cells(l) <= 20, $"'{l}' is wider than 20 cells"));
    }

    [Fact]
    public void Tail_SplitsAWordWiderThanTheLine()
    {
        var lines = ReplyPreview.Tail(new string('x', 25), width: 10, maxLines: 10);
        Assert.Equal(new[] { new string('x', 10), new string('x', 10), new string('x', 5) }, lines);
    }

    [Fact]
    public void Tail_CountsWideCharactersAsTwoCells()
    {
        var lines = ReplyPreview.Tail("日本語日本語", width: 6, maxLines: 10);
        Assert.Equal(new[] { "日本語", "日本語" }, lines);
    }

    [Fact]
    public void Tail_DropsControlCharacters_AndTrimsBlankEnds()
    {
        var lines = ReplyPreview.Tail("\n\nhello\u001b[31m\tthere\n\n", width: 40, maxLines: 5);
        Assert.Equal(new[] { "hello[31m    there" }, lines);
    }

    [Fact]
    public void Tail_OfNothing_IsEmpty()
    {
        Assert.Empty(ReplyPreview.Tail(null, 40, 5));
        Assert.Empty(ReplyPreview.Tail("   ", 40, 5));
    }
}
