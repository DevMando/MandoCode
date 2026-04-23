using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

public class MandoCodeConfigTests
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
        Assert.Equal(expected, MandoCodeConfig.IsValidRequestTimeout(value));
    }

    [Fact]
    public void CreateDefault_UsesFifteenMinuteTimeout()
    {
        var config = MandoCodeConfig.CreateDefault();
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
        Assert.Equal(expected, MandoCodeConfig.IsValidToolResultCharBudget(value));
    }

    [Fact]
    public void CreateDefault_UsesSensibleToolResultBudget()
    {
        var config = MandoCodeConfig.CreateDefault();
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
        Assert.Equal(expected, MandoCodeConfig.IsValidMaxAutoContinuations(value));
    }

    [Fact]
    public void CreateDefault_EnablesAutoContinuation()
    {
        var config = MandoCodeConfig.CreateDefault();
        Assert.True(config.EnableAutoContinuation);
        Assert.Equal(3, config.MaxAutoContinuations);
    }
}
