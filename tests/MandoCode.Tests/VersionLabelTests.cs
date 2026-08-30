using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// The banner's version label. Getting this wrong is how a stale or mislabelled binary goes
/// unnoticed — the numeric assembly version alone cannot distinguish a prerelease test build from
/// the release it was cut from.
/// </summary>
public class VersionLabelTests
{
    [Fact]
    public void KeepsPrereleaseTag_AndDropsBuildMetadata()
    {
        Assert.Equal(
            "v0.16.0-rc.1",
            VersionLabel.Build("0.16.0-rc.1+a1a0df8d15c1a5da", new Version(0, 16, 0, 0)));
    }

    [Fact]
    public void PlainVersion_RendersUnchanged()
    {
        Assert.Equal("v0.15.0", VersionLabel.Build("0.15.0+deadbeef", new Version(0, 15, 0, 0)));
    }

    [Fact]
    public void NoBuildMetadata_IsFine()
    {
        Assert.Equal("v1.2.3-rc.1", VersionLabel.Build("1.2.3-rc.1", new Version(1, 2, 3, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToAssemblyVersion_WhenInformationalVersionIsMissing(string? info)
    {
        // Three-part, matching what the banner showed before informational versions were read.
        Assert.Equal("v2.4.6", VersionLabel.Build(info, new Version(2, 4, 6, 99)));
    }

    [Fact]
    public void FallsBackToAssemblyVersion_WhenInformationalVersionIsOnlyBuildMetadata()
    {
        Assert.Equal("v2.4.6", VersionLabel.Build("+abc123", new Version(2, 4, 6, 0)));
    }

    [Fact]
    public void EmptyWhenNothingIsAvailable()
    {
        Assert.Equal("", VersionLabel.Build(null, null));
    }

    [Fact]
    public void RunningAssembly_ReportsAVersion()
    {
        // VersionLabel itself lives in the MandoCode assembly — a marker declared here would resolve
        // to the test assembly and assert nothing.
        var label = VersionLabel.ForAssembly(typeof(VersionLabel).Assembly);

        Assert.StartsWith("v", label);
        Assert.DoesNotContain("+", label);   // build metadata is stripped
    }
}
