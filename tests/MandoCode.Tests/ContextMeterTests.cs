using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ContextMeterTests
{
    [Fact]
    public void Read_FillsTheBarInProportion()
    {
        var reading = ContextMeter.Read(8_000, 16_000);

        Assert.Equal(ContextMeter.BarCells, reading.Bar.Length);
        Assert.Equal(10, reading.Bar.Count(c => c == '█'));
        Assert.Equal("8k / 16k (50%)", reading.Label);
        Assert.Equal(ContextMeter.Level.Ok, reading.Level);
    }

    [Theory]
    [InlineData(9_000, ContextMeter.Level.Ok)]      // 56%
    [InlineData(10_000, ContextMeter.Level.Warm)]   // 63%
    [InlineData(14_000, ContextMeter.Level.Full)]   // 88%
    public void Read_WarnsAsTheWindowFills(long used, ContextMeter.Level expected)
    {
        Assert.Equal(expected, ContextMeter.Read(used, 16_000).Level);
    }

    [Fact]
    public void Read_ClampsPastAFullWindow()
    {
        var reading = ContextMeter.Read(40_000, 16_000);
        Assert.Equal(new string('█', ContextMeter.BarCells), reading.Bar);
        Assert.EndsWith("(100%)", reading.Label);
    }

    [Fact]
    public void Read_WithoutAKnownWindow_ShowsTheCountOnly()
    {
        var reading = ContextMeter.Read(18_000, 0);
        Assert.Equal(string.Empty, reading.Bar);
        Assert.Equal("~18k tokens in context", reading.Label);
    }

    [Fact]
    public void KnownWindow_IsTheConfiguredLengthForLocalModels_AndUnknownForCloud()
    {
        Assert.Equal(32_768, ContextMeter.KnownWindow("qwen2.5-coder:14b", 32_768));
        Assert.Equal(0, ContextMeter.KnownWindow("deepseek-v4-flash:cloud", 32_768));
        Assert.Equal(0, ContextMeter.KnownWindow("llama3.1", 0));
    }
}
