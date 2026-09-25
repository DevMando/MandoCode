using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ReplyTextTests
{
    [Theory]
    [InlineData("A.\n\nB.", new[] { "A." }, "\n\nB.")]
    [InlineData("First  part\nsecond part. Then more.", new[] { "First part second part." }, " Then more.")]
    [InlineData("one two three", new[] { "one", "three" }, " two ")]
    public void TryRemoveInOrder_IgnoresWhitespace_AndReturnsTheRest(string text, string[] parts, string expected)
    {
        Assert.True(ReplyText.TryRemoveInOrder(text, parts, out var rest));
        Assert.Equal(expected, rest);
    }

    [Theory]
    [InlineData("B then A", new[] { "A", "B" })]
    [InlineData("rewritten", new[] { "original" })]
    public void TryRemoveInOrder_FailsWhenAPartIsMissingOrOutOfOrder(string text, string[] parts)
    {
        Assert.False(ReplyText.TryRemoveInOrder(text, parts, out _));
    }

    [Fact]
    public void Unshown_ReturnsTheRest_WhenTheEarlyPartsMatch()
    {
        Assert.Equal("Here is the answer.",
            ReplyText.Unshown("Let me check the docs.\nHere is the answer.", new[] { "Let me check the docs." }));
    }

    [Fact]
    public void Unshown_ReturnsEverything_WhenNothingWasShown_OrTheTextWasRewritten()
    {
        Assert.Equal("The answer.", ReplyText.Unshown("  The answer. ", Array.Empty<string>()));
        Assert.Equal("The file is updated.",
            ReplyText.Unshown("The file is updated.", new[] { "{\"name\":\"write_file\"}" }));
    }
}
