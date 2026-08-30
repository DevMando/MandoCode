using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class TokenTrackingServiceTests
{
    [Fact]
    public void TotalSessionTokens_SumsProviderReportedUsage()
    {
        var tracker = new TokenTrackingService();

        tracker.RecordModelUsage(250, 50, "Chat");

        Assert.Equal(300, tracker.TotalSessionTokens);
    }

    [Fact]
    public void Reset_ClearsReportedTotals()
    {
        var tracker = new TokenTrackingService();
        tracker.RecordModelUsage(250, 50, "Chat");

        tracker.Reset();

        Assert.Equal(0, tracker.TotalSessionTokens);
    }
}
