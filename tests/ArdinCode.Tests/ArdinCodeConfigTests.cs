using Xunit;
using ArdinCode.Models;

namespace ArdinCode.Tests;

public class ArdinCodeConfigTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(15, true)]
    [InlineData(60, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(61, false)]
    public void IsValidRequestTimeout_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsValidRequestTimeout(value));
    }

    [Fact]
    public void CreateDefault_UsesFifteenMinuteTimeout()
    {
        var config = ArdinCodeConfig.CreateDefault();
        Assert.Equal(15, config.RequestTimeoutMinutes);
    }

    [Theory]
    [InlineData(50_000L, true)]
    [InlineData(400_000L, true)]
    [InlineData(4_000_000L, true)]
    [InlineData(49_999L, false)]
    [InlineData(4_000_001L, false)]
    [InlineData(0L, false)]
    public void IsValidToolResultCharBudget_EnforcesBounds(long value, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsValidToolResultCharBudget(value));
    }

    [Fact]
    public void CreateDefault_UsesSensibleToolResultBudget()
    {
        var config = ArdinCodeConfig.CreateDefault();
        Assert.Equal(100_000L, config.ToolResultCharBudget);
    }

    [Theory]
    [InlineData(0, true)]      // 0 = "leave Ollama's default alone"
    [InlineData(2048, true)]
    [InlineData(8192, true)]
    [InlineData(262144, true)]
    [InlineData(1, false)]
    [InlineData(2047, false)]
    [InlineData(262145, false)]
    [InlineData(-1, false)]
    public void IsValidContextLength_EnforcesBounds_AndAllowsZero(int value, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsValidContextLength(value));
    }

    [Fact]
    public void ValidateAndClamp_PreservesZeroContextLength_ClampsOutOfRange()
    {
        var zero = new ArdinCodeConfig { ContextLength = 0 };
        zero.ValidateAndClamp();
        Assert.Equal(0, zero.ContextLength);

        var tiny = new ArdinCodeConfig { ContextLength = 100 };
        tiny.ValidateAndClamp();
        Assert.Equal(ArdinCodeConfig.MinContextLength, tiny.ContextLength);

        var huge = new ArdinCodeConfig { ContextLength = 999_999_999 };
        huge.ValidateAndClamp();
        Assert.Equal(ArdinCodeConfig.MaxContextLength, huge.ContextLength);
    }

    [Fact]
    public void CreateDefault_SetsLocalContextWindow()
    {
        var config = ArdinCodeConfig.CreateDefault();
        Assert.Equal(8192, config.ContextLength);
    }

    [Theory]
    [InlineData("minimax-m2.7:cloud", true)]
    [InlineData("kimi-k2.6:cloud", true)]
    [InlineData("qwen3-coder:480b-cloud", true)]
    [InlineData("QWEN3:480B-CLOUD", true)]   // case-insensitive
    [InlineData("qwen3.5:9b", false)]
    [InlineData("mistral", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCloudModel_DetectsCloudSuffix(string? tag, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsCloudModel(tag));
    }

    [Theory]
    // Cloud → 0: context is managed server-side, leave local config alone.
    [InlineData("minimax-m2.7:cloud", 0)]
    [InlineData("qwen3-coder:480b-cloud", 0)]
    // Local tiers: the tag's parameter count implies the user's hardware.
    [InlineData("qwen3.5:0.8b", 8192)]
    [InlineData("qwen3.5:2b", 8192)]
    [InlineData("qwen3.5:4b", 16384)]
    [InlineData("qwen3.5:9b", 32768)]
    [InlineData("qwen2.5-coder:14b", 32768)]
    [InlineData("qwen3:8b-q4_K_M", 32768)]   // size parses past the quant suffix
    // Unparseable local tags get the safe floor.
    [InlineData("mistral", 8192)]
    [InlineData("llama3.1:latest", 8192)]
    [InlineData(null, 8192)]
    public void RecommendedContextLength_MapsTierToWindow(string? tag, int expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.RecommendedContextLength(tag));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    public void IsValidMaxAutoContinuations_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsValidMaxAutoContinuations(value));
    }

    [Fact]
    public void CreateDefault_EnablesAutoContinuation()
    {
        var config = ArdinCodeConfig.CreateDefault();
        Assert.True(config.EnableAutoContinuation);
        Assert.Equal(3, config.MaxAutoContinuations);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(180, true)]
    [InlineData(1800, true)]
    [InlineData(29, false)]
    [InlineData(0, false)]
    [InlineData(1801, false)]
    public void IsValidModelResponseTimeout_EnforcesBounds(int value, bool expected)
    {
        Assert.Equal(expected, ArdinCodeConfig.IsValidModelResponseTimeout(value));
    }

    [Fact]
    public void CreateDefault_UsesSevenMinuteStallWatchdog()
    {
        // 420s, not 180: calls are non-streaming, so the watchdog gets no signal during
        // generation — a long reply on a slow provider (~10 tok/s observed) legitimately
        // runs past 3 minutes and was killed mid-generation at the old default.
        var config = ArdinCodeConfig.CreateDefault();
        Assert.Equal(420, config.ModelResponseTimeoutSeconds);
    }

    [Theory]
    [InlineData(5, ArdinCodeConfig.MinModelResponseTimeoutSeconds)]
    [InlineData(99999, ArdinCodeConfig.MaxModelResponseTimeoutSeconds)]
    [InlineData(240, 240)]
    public void ValidateAndClamp_BoundsModelResponseTimeout(int input, int expected)
    {
        var config = ArdinCodeConfig.CreateDefault();
        config.ModelResponseTimeoutSeconds = input;
        config.ValidateAndClamp();
        Assert.Equal(expected, config.ModelResponseTimeoutSeconds);
    }
}
