using Xunit;
using MandoCode.Models;

namespace MandoCode.Tests;

/// <summary>
/// The workflow planner is the product default and is intentionally not a configurable engine.
/// </summary>
public class PlannerEngineConfigTests
{
    [Theory]
    [InlineData("legacy")]
    [InlineData("workflow")]
    [InlineData("default")]
    public void TrySet_RejectsPlannerEngineSelection(string value)
    {
        var result = ConfigKeySetter.TrySet(new MandoCodeConfig(), "planner", value);

        Assert.False(result.Ok);
        Assert.Contains("always enabled", result.Message);
    }
}
