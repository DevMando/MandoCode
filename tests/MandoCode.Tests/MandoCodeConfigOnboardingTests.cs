using MandoCode.Models;
using Xunit;

namespace MandoCode.Tests;

public class MandoCodeConfigOnboardingTests
{
    [Fact]
    public void ValidateAndClamp_BlankEndpointFallsBackToDefault()
    {
        var config = new MandoCodeConfig { OllamaEndpoint = "   " };
        config.ValidateAndClamp();
        Assert.Equal("http://localhost:11434", config.OllamaEndpoint);
    }

    [Theory]
    [InlineData("http://localhost:11434/")]
    [InlineData("http://localhost:11434//")]
    [InlineData("http://10.0.0.5:11434/")]
    public void ValidateAndClamp_PreservesUserTypedEndpoint(string input)
    {
        // Config never silently mutates the user's URL. Heal happens at probe time
        // and only when the as-typed URL actually fails.
        var config = new MandoCodeConfig { OllamaEndpoint = input };
        config.ValidateAndClamp();
        Assert.Equal(input, config.OllamaEndpoint);
    }

    [Fact]
    public void HasCompletedOnboarding_DefaultsFalse()
    {
        var config = new MandoCodeConfig();
        Assert.False(config.HasCompletedOnboarding);
    }

    [Fact]
    public void DefaultCloudModel_HasExpectedTag()
    {
        // Pinned constant — onboarding auto-pulls this when a signed-in user has no
        // models, so any change here changes the out-of-box experience.
        Assert.Equal("minimax-m2.7:cloud", MandoCodeConfig.DefaultCloudModel);
    }
}
