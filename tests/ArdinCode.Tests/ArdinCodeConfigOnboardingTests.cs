using ArdinCode.Models;
using Xunit;

namespace ArdinCode.Tests;

public class ArdinCodeConfigOnboardingTests
{
    [Fact]
    public void ValidateAndClamp_BlankEndpointFallsBackToDefault()
    {
        var config = new ArdinCodeConfig { ApiEndpoint = "   " };
        config.ValidateAndClamp();
        Assert.Equal("https://api.avalai.ir/v1", config.ApiEndpoint);
    }

    [Theory]
    [InlineData("https://api.example.com/")]
    [InlineData("https://api.example.com//")]
    [InlineData("https://api.avalai.ir/v1/")]
    public void ValidateAndClamp_PreservesUserTypedEndpoint(string input)
    {
        var config = new ArdinCodeConfig { ApiEndpoint = input };
        config.ValidateAndClamp();
        Assert.Equal(input, config.ApiEndpoint);
    }

    [Fact]
    public void HasCompletedOnboarding_DefaultsFalse()
    {
        var config = new ArdinCodeConfig();
        Assert.False(config.HasCompletedOnboarding);
    }
}
