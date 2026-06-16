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
