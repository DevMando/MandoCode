using Xunit;
using ArdinCode.Services;

namespace ArdinCode.Tests;

/// <summary>
/// Tests the done_reason="length" diagnostic. The same stop reason has two opposite
/// causes — response cap reached vs context window filled — and the old one-size
/// message told window-filled users to raise maxTokens, which is exactly the wrong
/// knob (it reserves MORE of the window for output). Regression case: qwen3:4b on a
/// 4k-window daemon burned 1.3k thinking tokens against a 32k response budget and
/// produced no visible answer.
/// </summary>
public class LengthCutoffNoticeTests
{
    [Theory]
    [InlineData(32768, 32768)] // exactly at the cap
    [InlineData(31000, 32768)] // within 90% — Ollama stops a few tokens shy
    [InlineData(0, 32768)]     // unreported count — assume cap, give the safe generic advice
    public void AtOrNearResponseCap_AdvisesMaxTokens(long completionTokens, int maxTokens)
    {
        var notice = AIService.BuildLengthCutoffNotice(completionTokens, maxTokens, 16384, emptyContent: false);

        Assert.Contains("max response tokens", notice);
        Assert.Contains("continue", notice);
        Assert.DoesNotContain("CONTEXT WINDOW", notice);
    }

    [Fact]
    public void FarBelowCap_DiagnosesContextWindow_NotMaxTokens()
    {
        // The real-world case: 1.3k generated against a 32k budget = the window filled.
        var notice = AIService.BuildLengthCutoffNotice(1300, 32768, 16384, emptyContent: false);

        Assert.Contains("CONTEXT WINDOW", notice);
        Assert.Contains("raising max tokens won't help", notice);
        Assert.Contains("1,300", notice);
        // Configured window exists → steer to restarting the daemon so it applies.
        Assert.Contains("16k window", notice);
        Assert.Contains("/setup", notice);
    }

    [Fact]
    public void EmptyContent_MentionsThinkingModels()
    {
        var notice = AIService.BuildLengthCutoffNotice(1300, 32768, 16384, emptyContent: true);

        Assert.Contains("thinking model", notice);
    }

    [Fact]
    public void NoConfiguredWindow_SteersToContextLengthConfig()
    {
        var notice = AIService.BuildLengthCutoffNotice(1300, 32768, configuredContextLength: 0, emptyContent: false);

        Assert.Contains("contextLength", notice);
        Assert.DoesNotContain("tray app", notice);
    }

    [Fact]
    public void CloudModel_WindowFilled_NeverGivesLocalDaemonAdvice()
    {
        // Cloud context lives server-side — the desktop-app slider and daemon restart
        // are meaningless there. Only history trimming helps.
        var notice = AIService.BuildLengthCutoffNotice(1300, 32768, 16384, emptyContent: false, isCloudModel: true);

        Assert.Contains("server-side context window", notice);
        Assert.Contains("/clear", notice);
        Assert.DoesNotContain("desktop app", notice);
        Assert.DoesNotContain("OLLAMA_CONTEXT_LENGTH", notice);
        Assert.DoesNotContain("/setup", notice);
    }

    [Fact]
    public void CapHit_WithEmptyContent_ExplainsThinkingBudget()
    {
        // The minimax-on-cloud case: a thinking model eats a small maxTokens budget with
        // reasoning and produces nothing visible — the cap advice should say why.
        var notice = AIService.BuildLengthCutoffNotice(4096, 4096, 16384, emptyContent: true, isCloudModel: true);

        Assert.Contains("max response tokens", notice);
        Assert.Contains("thinking models", notice);
    }
}
