using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Unit")]
public sealed class AgentAvatarTests
{
    [Fact]
    public void AvatarsAreConsistentAcrossReferencesAndCase()
    {
        Assert.Equal("🛸", CliAgentPresentation.Avatar("Bandit"));
        Assert.DoesNotContain("⚡", CliAgentPresentation.Avatar("Bandit"));
        var names = new[] { "Quartz", "Lumen", "Fusion", "Topaz" };
        Assert.Equal(4, names.Select(CliAgentPresentation.Avatar).Distinct().Count());
        foreach (var name in names)
        {
            Assert.Equal(CliAgentPresentation.Avatar(name), CliAgentPresentation.Avatar(name.ToLowerInvariant()));
            Assert.StartsWith(CliAgentPresentation.Avatar(name), CliAgentPresentation.Name(name));
            Assert.Contains("[#c678dd]", CliAgentPresentation.Name(name));
        }
    }
}
