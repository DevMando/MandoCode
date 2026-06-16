using Xunit;
using ArdinCode.Services;

namespace ArdinCode.Tests;

/// <summary>
/// Tests the done_reason="length" diagnostic. The same stop reason has two opposite
/// causes — response cap reached vs context window filled — and the old one-size
/// message told window-filled users to raise maxTokens, which is exactly the wrong
/// knob (it reserves MORE of the window for output).
/// </summary>
public class LengthCutoffNoticeTests
{
    [Theory]
    [InlineData(16384, 16384)] // exactly at the cap
    [InlineData(15000, 16384)] // within 90% — stops a few tokens shy
    [InlineData(0, 16384)]     // unreported count — assume cap, give the safe generic advice
    public void AtOrNearResponseCap_AdvisesMaxTokens(long completionTokens, int maxTokens)
    {
        var notice = AIService.BuildLengthCutoffNotice(completionTokens, maxTokens, emptyContent: false);

        Assert.Contains("max response tokens", notice);
        Assert.Contains("continue", notice);
        Assert.DoesNotContain("CONTEXT WINDOW", notice);
    }

    [Fact]
    public void FarBelowCap_DiagnosesContextWindow_NotMaxTokens()
    {
        var notice = AIService.BuildLengthCutoffNotice(1300, 16384, emptyContent: false);

        Assert.Contains("CONTEXT WINDOW", notice);
        Assert.Contains("raising max tokens won't help", notice);
        Assert.Contains("1,300", notice);
        Assert.Contains("server-side context window", notice);
        Assert.Contains("/clear", notice);
    }

    [Fact]
    public void EmptyContent_MentionsReasoning()
    {
        var notice = AIService.BuildLengthCutoffNotice(1300, 16384, emptyContent: true);

        Assert.Contains("reasoning", notice);
    }

    [Fact]
    public void CapHit_WithEmptyContent_ExplainsThinkingBudget()
    {
        var notice = AIService.BuildLengthCutoffNotice(4096, 4096, emptyContent: true);

        Assert.Contains("max response tokens", notice);
        Assert.Contains("thinking models", notice);
    }
}
